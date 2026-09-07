#!/usr/bin/env bash
# Seeds a small, KNOWN dataset for the demo (docs/DEMO.md) and produces real, downloadable
# statements by driving the actual generation pipeline - the seed tool alone writes database
# rows without objects, which is right for benchmarks and wrong for a demo.
#
# Needs on the host: docker compose (the stack up), the .NET 10 SDK, curl, and jq or Python.
# Safe to re-run: an already-seeded database is detected and the seed step is skipped.
set -euo pipefail

API="${API_URL:-http://localhost:8081}"

# Everything the script needs is already in .env (the same file compose reads).
ENV_FILE="$(cd "$(dirname "$0")/.." && pwd)/.env"
[ -f "$ENV_FILE" ] || { echo "no .env found - run: cp .env.example .env"; exit 1; }
set -a; . "$ENV_FILE"; set +a

# The seed tool takes its connection from Postgres__PrimaryConnectionString and refuses to
# guess. Build it from .env unless the caller has set one explicitly. Through PgBouncer on 6432
# as app_generation - the same route the generation worker uses; the postgres superuser is
# deliberately not in PgBouncer's userlist.
if [ -z "${Postgres__PrimaryConnectionString:-}" ]; then
  export Postgres__PrimaryConnectionString="Host=localhost;Port=${PGBOUNCER_PORT:-6432};Database=statements_generation;Username=app_generation;Password=${APP_GENERATION_PASSWORD}"
fi

# Reads one field from JSON on stdin. jq if it is installed (docs/DEMO.md uses it anyway),
# otherwise python; the expression is a jq path such as `.status` or `.runId // .id`.
# On Windows `command -v python` can find the Microsoft Store stub, which is why each candidate is
# executed once before it is trusted.
json() {
  if command -v jq >/dev/null 2>&1; then jq -r "$1"; return; fi
  local py=""
  for candidate in python3 python; do
    if "$candidate" -c "pass" >/dev/null 2>&1; then py="$candidate"; break; fi
  done
  [ -n "$py" ] || { echo "seed-demo.sh needs jq or python on PATH to read API responses" >&2; exit 1; }
  "$py" -c '
import sys, json
d = json.load(sys.stdin)
for alt in sys.argv[1].split("//"):
    v = d.get(alt.strip().lstrip("."))
    if v is not None:
        print(v); break
' "$1"
}

psql_statements() {
  docker compose exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" postgres \
    psql -U postgres -d statements -tA -v ON_ERROR_STOP=1 "$@"
}

echo "== 1/4 waiting for readiness"
for url in "$API/health/ready" "${GATEWAY_URL:-http://localhost:8082}/health/ready"; do
  until curl -fsS "$url" > /dev/null 2>&1; do sleep 2; done
done

echo "== 2/4 seeding 25 customers (deterministic: --seed 7)"
# The seed tool is a bulk COPY and is not idempotent: a second run would fail on the customer
# external_ref unique constraint. Detect the earlier run instead of failing on it.
EXISTING=$(psql_statements -c "SELECT count(*) FROM customer WHERE external_ref LIKE 'EXT-7-%';" | tr -d '[:space:]')
if [ "${EXISTING:-0}" -gt 0 ]; then
  echo "   already seeded ($EXISTING customers) - skipping. For a clean slate: docker compose down -v"
else
  dotnet run --project tools/seed -c Release -- --customers 25 --months 3 --seed 7
fi

# A customer with a FIXED, documented id, so the README can name it instead of asking the reader
# to paste one. Idempotent: the same rows on every run. The account id hashes to "known" in the
# mock ledger (LedgerGenerator.IsKnown), so the generation run renders a real statement for it.
DEMO_CUSTOMER_ID="11111111-1111-1111-1111-111111111111"
psql_statements -c "
  INSERT INTO customer (id, external_ref, status)
  VALUES ('$DEMO_CUSTOMER_ID', 'DEMO-CUSTOMER', 'ACTIVE')
  ON CONFLICT (id) DO NOTHING;
  INSERT INTO account (id, customer_id, account_number_masked, product_type, status, opened_at, closed_at)
  VALUES ('11111111-1111-1111-1111-111111111112', '$DEMO_CUSTOMER_ID', '****1111', 'CURRENT', 'ACTIVE', '2020-01-01T00:00:00Z', NULL)
  ON CONFLICT (id) DO NOTHING;" > /dev/null
echo "   demo customer $DEMO_CUSTOMER_ID is in place"

echo "== 3/4 requesting a generation run for last month (real render -> encrypt -> upload)"
STAFF_TOKEN=$(curl -fsS -X POST \
  "$API/v1/dev/tokens?customerId=00000000-0000-0000-0000-000000000001&staff=true" \
  | json .accessToken)

PERIOD_START=$(date -d "$(date +%Y-%m-01) -1 month" +%Y-%m-01 2>/dev/null \
  || date -v-1m +%Y-%m-01)
PERIOD_END=$(date -d "$PERIOD_START +1 month -1 day" +%Y-%m-%d 2>/dev/null \
  || date -v+1m -v-1d -j -f %Y-%m-%d "$PERIOD_START" +%Y-%m-%d)

# Run creation is idempotent per period: a repeat request returns the existing run.
RUN_ID=$(curl -fsS -X POST "$API/v1/statement-runs" \
  -H "Authorization: Bearer $STAFF_TOKEN" -H 'Content-Type: application/json' \
  -d "{\"periodStart\":\"$PERIOD_START\",\"periodEnd\":\"$PERIOD_END\"}" \
  | json '.runId // .id')
echo "   run $RUN_ID for $PERIOD_START..$PERIOD_END"

echo "== 4/4 waiting for the run to complete"
STATUS=""
for _ in $(seq 1 120); do
  STATUS=$(curl -fsS "$API/v1/statement-runs/$RUN_ID" -H "Authorization: Bearer $STAFF_TOKEN" \
    | json .status)
  [ "$STATUS" = "COMPLETED" ] && break
  sleep 5
done
echo "   run status: $STATUS"
[ "$STATUS" = "COMPLETED" ] || { echo "the run did not complete; see: docker compose logs generation-worker --tail 50" >&2; exit 1; }

# The seed tool's own statement rows point at objects that were never written - fine for a
# benchmark, a 404 in a demo. Keep only statements the generation run actually produced, so
# every row a reviewer can list is one they can download.
PRUNED=$(psql_statements -c "
  DELETE FROM statement s
   WHERE NOT EXISTS (SELECT 1 FROM statement_run_item r WHERE r.statement_id = s.id)
  RETURNING 1;" | grep -c 1 || true)
echo "   removed $PRUNED seed-only statement rows (no object behind them)"

echo
echo "Demo-ready. The documented demo customer and their generated statement:"
psql_statements -c "
  SELECT 'CUSTOMER_ID=' || s.customer_id || E'\nSTATEMENT_ID=' || s.id || E'\nPERIOD=' || s.period_start
    FROM statement s
    JOIN statement_run_item r ON r.statement_id = s.id
   WHERE s.status = 'AVAILABLE' AND s.period_start = '$PERIOD_START'
     AND s.customer_id = '$DEMO_CUSTOMER_ID'
   ORDER BY s.id LIMIT 1;"
echo "Mint a customer token:  TOKEN=\$(./scripts/demo-token.sh $DEMO_CUSTOMER_ID)"
