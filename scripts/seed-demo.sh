#!/usr/bin/env bash
# Seeds a small, KNOWN dataset for the demo (docs/DEMO.md) and produces real, downloadable
# statements by driving the actual generation pipeline - the seed tool alone writes database
# rows without objects, which is right for benchmarks and wrong for a demo.
set -euo pipefail

API="${API_URL:-http://localhost:8081}"

echo "== 1/4 waiting for readiness"
for url in "$API/health/ready" "${GATEWAY_URL:-http://localhost:8082}/health/ready"; do
  until curl -fsS "$url" > /dev/null 2>&1; do sleep 2; done
done

echo "== 2/4 seeding 25 customers (deterministic: --seed 7)"
dotnet run --project tools/seed -c Release -- --customers 25 --months 3 --seed 7

echo "== 3/4 requesting a generation run for last month (real render -> encrypt -> upload)"
STAFF_TOKEN=$(curl -fsS -X POST \
  "$API/v1/dev/tokens?customerId=00000000-0000-0000-0000-000000000001&staff=true" \
  | python -c "import sys,json;print(json.load(sys.stdin)['accessToken'])")

PERIOD_START=$(date -d "$(date +%Y-%m-01) -1 month" +%Y-%m-01 2>/dev/null \
  || date -v-1m +%Y-%m-01)
PERIOD_END=$(date -d "$PERIOD_START +1 month -1 day" +%Y-%m-%d 2>/dev/null \
  || date -v+1m -v-1d -j -f %Y-%m-%d "$PERIOD_START" +%Y-%m-%d)

RUN_ID=$(curl -fsS -X POST "$API/v1/statement-runs" \
  -H "Authorization: Bearer $STAFF_TOKEN" -H 'Content-Type: application/json' \
  -d "{\"periodStart\":\"$PERIOD_START\",\"periodEnd\":\"$PERIOD_END\"}" \
  | python -c "import sys,json;d=json.load(sys.stdin);print(d.get('runId') or d.get('id'))")
echo "   run $RUN_ID for $PERIOD_START..$PERIOD_END"

echo "== 4/4 waiting for the run to complete"
for _ in $(seq 1 120); do
  STATUS=$(curl -fsS "$API/v1/statement-runs/$RUN_ID" -H "Authorization: Bearer $STAFF_TOKEN" \
    | python -c "import sys,json;print(json.load(sys.stdin)['status'])")
  [ "$STATUS" = "COMPLETED" ] && break
  sleep 5
done
echo "   run status: $STATUS"

echo
echo "Demo-ready. A customer and one of their generated statements:"
docker compose exec -T postgres psql -U postgres -d statements -tA -c "
  SELECT 'CUSTOMER_ID=' || customer_id || E'\nSTATEMENT_ID=' || id || E'\nPERIOD=' || period_start
    FROM statement WHERE status='AVAILABLE' AND period_start='$PERIOD_START'
   ORDER BY id LIMIT 1;"
echo "Mint a customer token:  curl -X POST '$API/v1/dev/tokens?customerId=<CUSTOMER_ID>'"
