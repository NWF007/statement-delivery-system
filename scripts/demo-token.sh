#!/usr/bin/env bash
# Prints a development JWT for one customer, minted by the running Delivery.Api's own
# /v1/dev/tokens endpoint (Development only; the signing key never leaves the service).
#
# Usage:  ./scripts/demo-token.sh <customerId> [staff|dpo]
#         TOKEN=$(./scripts/demo-token.sh "$CUSTOMER_ID")
#         STAFF=$(./scripts/demo-token.sh "$CUSTOMER_ID" staff)     # adds the operator scope
#         DPO=$(./scripts/demo-token.sh "$CUSTOMER_ID" dpo)         # operator + erasure scope
#
# Needs only curl. Override the API with API_URL (default http://localhost:8081).
set -euo pipefail

API="${API_URL:-http://localhost:8081}"
CUSTOMER="${1:?usage: demo-token.sh <customerId> [staff|dpo]}"
case "${2:-}" in
  "")    QUERY="" ;;
  staff) QUERY="&staff=true" ;;
  dpo)   QUERY="&staff=true&dpo=true" ;;
  *)     echo "second argument must be staff or dpo" >&2; exit 2 ;;
esac

RESP=$(curl -fsS -X POST "$API/v1/dev/tokens?customerId=$CUSTOMER$QUERY") || {
  echo "could not mint a token from $API - is the stack up (docker compose ps) and ASPNETCORE_ENVIRONMENT=Development?" >&2
  exit 1
}
# Extract accessToken without a JSON dependency; the token itself contains no quotes.
echo "$RESP" | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p'
