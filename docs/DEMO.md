# Demo script (~10 minutes)

A driving script for a live demonstration. Every command has been written to be pasted
verbatim; run the setup **before** the call.

## Setup (before the call)

```bash
docker compose up --build -d
./scripts/seed-demo.sh          # seeds 25 customers, runs REAL generation for last month
```

The script ends by printing a `CUSTOMER_ID`, `STATEMENT_ID` and `PERIOD`. Export them:

```bash
export API=http://localhost:8081 GW=http://localhost:8082
export CUSTOMER_ID=... STATEMENT_ID=... PERIOD=...
export TOKEN=$(curl -fsS -X POST "$API/v1/dev/tokens?customerId=$CUSTOMER_ID" | jq -r .accessToken)
export STAFF=$(curl -fsS -X POST "$API/v1/dev/tokens?customerId=$CUSTOMER_ID&staff=true" | jq -r .accessToken)
```

## 1. The happy path (2 min)

```bash
# List - note the mandatory bounded date range (partition pruning is not optional)
curl -fsS "$API/v1/customers/$CUSTOMER_ID/statements?from=$PERIOD&to=2099-01-01" \
  -H "Authorization: Bearer $TOKEN" | jq '.items[0]'

# Issue a single-use link
LINK=$(curl -fsS -X POST "$API/v1/statements/$STATEMENT_ID/download-links?period=$PERIOD" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{}' | jq -r .url)

# Download the PDF through the gateway (decrypt-and-stream, never a presigned URL)
curl -fsS "$LINK" -o /tmp/statement.pdf && file /tmp/statement.pdf
```

Show the audit entry:

```bash
docker compose exec -T postgres psql -U postgres -d statements -c \
  "SELECT action, outcome, occurred_at FROM audit_event
    WHERE statement_id='$STATEMENT_ID' ORDER BY occurred_at DESC LIMIT 5;"
```

## 2. Single-use (1 min)

```bash
curl -s -o /dev/null -w '%{http_code}\n' "$LINK"        # -> 404
docker compose exec -T postgres psql -U postgres -d statements -c \
  "SELECT action, denial_reason_code FROM audit_event
    WHERE action='ACCESS_DENIED' ORDER BY occurred_at DESC LIMIT 1;"   # -> CONSUMED
```

Say: replay produces the SAME 404 as a token that never existed - uniform denials, with the
real reason preserved in the audit trail, not the response.

## 3. IDOR resistance (1 min)

```bash
# Another customer's statement: 404, NOT 403
OTHER=$(docker compose exec -T postgres psql -U postgres -d statements -tA -c \
  "SELECT id FROM statement WHERE customer_id <> '$CUSTOMER_ID' AND status='AVAILABLE' LIMIT 1;")
curl -s -o /dev/null -w '%{http_code}\n' \
  "$API/v1/statements/$OTHER?period=$PERIOD" -H "Authorization: Bearer $TOKEN"
```

Say: a 403 would confirm the resource EXISTS; not-found and not-yours are deliberately
indistinguishable (ADR-0012).

## 4. The audit chain (2 min)

```bash
curl -fsS "$API/v1/audit/verify" -H "Authorization: Bearer $STAFF" | jq '{verified, chainsChecked, eventsChecked}'

# Tampering is rejected by the trigger...
docker compose exec -T postgres psql -U postgres -d statements -c \
  "UPDATE audit_event SET outcome='SUCCESS' WHERE action='ACCESS_DENIED';" || true
```

Say: two independent layers - the trigger, and the fact that no application role holds UPDATE
or DELETE on audit_event at all. And if both were bypassed by a superuser, re-verification
catches the hash break - that is the layer the demo just proved.

## 5. Crypto-erasure (3 min) - THE segment to rehearse

```bash
# 5a. Inside the retention window: refused, WITH the statute (erasure needs the DPO scope)
export DPO=$(curl -fsS -X POST "$API/v1/dev/tokens?customerId=$CUSTOMER_ID&staff=true&dpo=true" | jq -r .accessToken)
curl -s -X POST "$API/v1/customers/$CUSTOMER_ID/erasure" \
  -H "Authorization: Bearer $DPO" \
  -H 'Content-Type: application/json' -d '{"reason":"POPIA s24","requestReference":"DSR-DEMO-1"}' | jq
#    -> 409 { reason: STATUTORY_RETENTION, basis: "Companies Act s24 / FICA s23", retainUntil }

# 5b. A legal hold blocks it even after retention - with the case reference
curl -fsS -X POST "$API/v1/statements/$STATEMENT_ID/legal-holds" \
  -H "Authorization: Bearer $STAFF" -H 'Content-Type: application/json' \
  -d '{"reason":"litigation","caseReference":"CASE-DEMO-42"}' | jq

# 5c. Release the hold, backdate retention, request + fast-forward the erasure
curl -fsS -X DELETE "$API/v1/legal-holds/<holdId>" -H "Authorization: Bearer $STAFF" \
  -H 'Content-Type: application/json' -d '{"releaseReason":"matter closed"}'
docker compose exec -T postgres psql -U postgres -d statements -c \
  "UPDATE statement SET retain_until='2020-01-01' WHERE customer_id='$CUSTOMER_ID';"
# request erasure (as 5a), then close the cooling-off window:
docker compose exec -T postgres psql -U postgres -d statements -c \
  "UPDATE erasure_request SET due_at=now() WHERE customer_id='$CUSTOMER_ID';
   UPDATE customer_key SET destruction_due_at=now() WHERE customer_id='$CUSTOMER_ID';"
# The executor paces itself daily; a restart resets that pace - its FIRST tick after leader
# election always runs the erasure pass, and the Development sweep tick is 60s:
docker compose restart retention-worker && sleep 70

# 5d. The payoff: the OBJECT IS STILL IN THE BUCKET...
docker compose exec minio mc ls --recursive local/statements/ | head -3

# ...and decryption is now impossible - the download 410s and the key row is a tombstone
docker compose exec -T postgres psql -U postgres -d statements -c \
  "SELECT status, wrapped_cek IS NULL AS material_gone, destroyed_at FROM customer_key
    WHERE customer_id='$CUSTOMER_ID';"
```

Say, verbatim: *"This is why the encryption design exists - the object is under a compliance
lock and cannot be deleted by anyone. We destroyed one row instead, and every copy everywhere -
live, versioned, backed up - became unreadable at once."*

## 6. Observability (1 min)

Open the Aspire dashboard (http://localhost:18888): one trace spanning
issue → redeem → consume → decrypt → stream, with the database spans inside it.

## If something goes sideways

- `docker compose logs delivery-api --tail 50`
- `curl $API/health/ready | jq` - names the failing dependency
- The seed script is idempotent per period: re-running the generation run returns the same
  runId (acceptance 55's idempotency, demonstrable in itself).
