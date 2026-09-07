# Guided walkthrough (~10 minutes)

A self-service tour of the properties the system exists for: single-use links, uniform denials,
a tamper-evident audit trail, and erasure inside an immutable store. Every command is meant to be
pasted verbatim into bash (Git Bash on Windows works), and the response you should see is shown
next to it.

## Setup

Needs, on the host: Docker with Compose v2, bash, `curl`, `jq`, and the .NET 10 SDK
(`global.json` pins 10.0.400; the seed tool is a .NET project). The seed step reads its database
credentials from `.env`; nothing has to be exported by hand.

```bash
docker compose up --build -d
./scripts/seed-demo.sh          # ~1 minute: seeds 25 customers, runs REAL generation for last month
```

The script is safe to re-run (an already-seeded database is detected and skipped). It always
creates ten demo customers with fixed ids, `11111111-1111-1111-1111-111111111101` through `…110`,
and the generation run produces last month's statement for each. This tour uses the first two.
Mint two tokens, then read the first customer's statement id and period from the API:

```bash
export API=http://localhost:8081 GW=http://localhost:8082
export CUSTOMER_ID=11111111-1111-1111-1111-111111111101
export OTHER_CUSTOMER_ID=11111111-1111-1111-1111-111111111102
export TOKEN=$(./scripts/demo-token.sh "$CUSTOMER_ID")           # that customer
export STAFF=$(./scripts/demo-token.sh "$CUSTOMER_ID" staff)     # plus the operator scope
LIST=$(curl -fsS "$API/v1/customers/$CUSTOMER_ID/statements?from=$(( $(date +%Y) - 1 ))-01-01&to=$(date +%Y-%m-%d)" \
  -H "Authorization: Bearer $TOKEN")
export STATEMENT_ID=$(echo "$LIST" | jq -r '.items[0].id')
export PERIOD=$(echo "$LIST" | jq -r '.items[0].period.start')
```

The script also prints every demo customer's statement id at the end, if you would rather paste.

`demo-token.sh` calls the API's own `POST /v1/dev/tokens`, which exists only in the Development
environment; the signing key never leaves the service.

## 1. The happy path (2 min)

```bash
# List - the date range is mandatory and capped at 84 months (partition pruning is not optional)
curl -fsS "$API/v1/customers/$CUSTOMER_ID/statements?from=$PERIOD&to=$(date +%Y-%m-%d)" \
  -H "Authorization: Bearer $TOKEN" | jq '.items[0] | {id, period, status}'
#    -> { "id": "<one of the customer's statements>", "period": { "start": "<PERIOD>", "end": ... }, "status": "AVAILABLE" }

# Issue a single-use link
LINK=$(curl -fsS -X POST "$API/v1/statements/$STATEMENT_ID/download-links?period=$PERIOD" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{}' | jq -r .url)
echo "$LINK"
#    -> http://localhost:8082/v1/d/<43 url-safe characters>

# Download the PDF through the gateway (decrypt-and-stream, never a presigned URL)
curl -fsS "$LINK" -o /tmp/statement.pdf && file /tmp/statement.pdf
#    -> /tmp/statement.pdf: PDF document, version 1.4, 1 page(s)
```

The audit entries so far:

```bash
docker compose exec -T -e PGPASSWORD=local-dev-postgres-password postgres psql -U postgres -d statements -c \
  "SELECT action, outcome, occurred_at FROM audit_event
    WHERE statement_id='$STATEMENT_ID' ORDER BY occurred_at DESC LIMIT 5;"
#    -> DOWNLOAD_COMPLETED / DOWNLOAD_STARTED / LINK_ISSUED / STATEMENT_GENERATED, all SUCCESS
```

## 2. Single-use (1 min)

```bash
curl -s -o /dev/null -w '%{http_code}\n' "$LINK"        # -> 404
docker compose exec -T -e PGPASSWORD=local-dev-postgres-password postgres psql -U postgres -d statements -c \
  "SELECT action, denial_reason_code FROM audit_event
    WHERE action='ACCESS_DENIED' ORDER BY occurred_at DESC LIMIT 1;"   # -> ACCESS_DENIED | CONSUMED
```

The replay produces the SAME 404 as a token that never existed: denials are uniform, and the real
reason is preserved in the audit trail, not in the response.

## 3. IDOR resistance (1 min)

```bash
# The second demo customer's statement, fetched with THEIR token, then requested with OURS
OTHER_TOKEN=$(./scripts/demo-token.sh "$OTHER_CUSTOMER_ID")
OTHER=$(curl -fsS "$API/v1/customers/$OTHER_CUSTOMER_ID/statements?from=$PERIOD&to=$(date +%Y-%m-%d)" \
  -H "Authorization: Bearer $OTHER_TOKEN" | jq -r '.items[0].id')
curl -s -o /dev/null -w '%{http_code}\n' \
  "$API/v1/statements/$OTHER?period=$PERIOD" -H "Authorization: Bearer $TOKEN"    # -> 404, NOT 403
curl -s -o /dev/null -w '%{http_code}\n' \
  "$API/v1/statements/$STATEMENT_ID?period=$PERIOD" -H "Authorization: Bearer $TOKEN"   # -> 200
```

A 403 would confirm the resource EXISTS; not-found and not-yours are deliberately
indistinguishable (ADR-0012).

## 4. The audit chain (2 min)

```bash
curl -fsS "$API/v1/audit/verify" -H "Authorization: Bearer $STAFF" | jq '{verified, chainsChecked, eventsChecked}'
#    -> { "verified": true, "chainsChecked": 16, "eventsChecked": <n> }

# Tampering is rejected by the trigger...
docker compose exec -T -e PGPASSWORD=local-dev-postgres-password postgres psql -U postgres -d statements -c \
  "UPDATE audit_event SET outcome='SUCCESS' WHERE action='ACCESS_DENIED';" || true
#    -> ERROR:  audit_event is append-only (attempted UPDATE)
```

Two independent layers: the trigger, and the fact that no application role holds UPDATE or
DELETE on `audit_event` at all. If both were bypassed by a superuser, re-verification catches the
hash break; what it cannot catch is described in [LIMITATIONS.md](LIMITATIONS.md).

## 5. Crypto-erasure (3 min)

This is why the encryption design exists. The object is under a compliance lock and cannot be
deleted by anyone, so erasure destroys one key row instead, and every copy everywhere - live,
versioned, backed up - becomes unreadable at once.

```bash
# 5a. Inside the retention window: refused, WITH the statute (erasure needs the DPO scope)
export DPO=$(./scripts/demo-token.sh "$CUSTOMER_ID" dpo)
curl -s -X POST "$API/v1/customers/$CUSTOMER_ID/erasure" \
  -H "Authorization: Bearer $DPO" \
  -H 'Content-Type: application/json' -d '{"reason":"POPIA s24","requestReference":"DSR-DEMO-1"}' | jq
#    -> 409 { reason: STATUTORY_RETENTION, basis: "Companies Act s24 / FICA s23", retainUntil }

# 5b. A legal hold blocks it even after retention - with the case reference
HOLD_ID=$(curl -fsS -X POST "$API/v1/statements/$STATEMENT_ID/legal-holds" \
  -H "Authorization: Bearer $STAFF" -H 'Content-Type: application/json' \
  -d '{"reason":"litigation","caseReference":"CASE-DEMO-42"}' | jq -r .holdId)
echo "$HOLD_ID"                                                          # -> a UUID

# 5c. Release the hold, backdate retention, request + fast-forward the erasure
curl -fsS -X DELETE "$API/v1/legal-holds/$HOLD_ID" -H "Authorization: Bearer $STAFF" \
  -H 'Content-Type: application/json' -d '{"releaseReason":"matter closed"}'     # -> 204
docker compose exec -T -e PGPASSWORD=local-dev-postgres-password postgres psql -U postgres -d statements -c \
  "UPDATE statement SET retain_until='2020-01-01' WHERE customer_id='$CUSTOMER_ID';"
curl -s -X POST "$API/v1/customers/$CUSTOMER_ID/erasure" \
  -H "Authorization: Bearer $DPO" \
  -H 'Content-Type: application/json' -d '{"reason":"POPIA s24","requestReference":"DSR-DEMO-1"}' | jq
#    -> 202 { erasureId, scheduledFor, coolingOffEnds }   (seven days out)
# close the cooling-off window:
docker compose exec -T -e PGPASSWORD=local-dev-postgres-password postgres psql -U postgres -d statements -c \
  "UPDATE erasure_request SET due_at=now() WHERE customer_id='$CUSTOMER_ID';
   UPDATE customer_key SET destruction_due_at=now() WHERE customer_id='$CUSTOMER_ID';"
# The executor paces itself daily; a restart resets that pace - its FIRST tick after leader
# election always runs the erasure pass, and the Development sweep tick is 60s:
docker compose restart retention-worker && sleep 70

# 5d. The payoff: the OBJECT IS STILL IN THE BUCKET...
docker compose exec minio sh -c 'mc alias set local http://localhost:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null && mc ls --recursive local/statements/ | head -3'
#    -> three .enc objects, unchanged

# ...and decryption is now impossible - the key row is a tombstone and a new link is refused with 410
docker compose exec -T -e PGPASSWORD=local-dev-postgres-password postgres psql -U postgres -d statements -c \
  "SELECT status, wrapped_cek IS NULL AS material_gone, destroyed_at FROM customer_key
    WHERE customer_id='$CUSTOMER_ID';"
#    -> DESTROYED | t | <timestamp>
curl -s -X POST "$API/v1/statements/$STATEMENT_ID/download-links?period=$PERIOD" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{}' | jq '{status, title}'
#    -> { "status": 410, "title": "Statement no longer available" }
```

## 6. Observability (1 min)

Open the Aspire dashboard (http://localhost:18888): one trace spanning
issue → redeem → consume → decrypt → stream, with the database spans inside it.

## If something goes sideways

- `docker compose logs delivery-api --tail 50`
- `curl $API/health/ready | jq` - names the failing dependency
- The seed script can be re-run at any time; it skips the seed step when the 25 customers already
  exist, and run creation is idempotent per period (a repeat request returns the same `runId`).
- To start over from nothing: `docker compose down -v`, then `up` and the seed script again.
- After section 5 the demo customer's key is gone for good. Pick another `CUSTOMER_ID` from the
  `statement` table, or reset the stack, before repeating the walkthrough.
