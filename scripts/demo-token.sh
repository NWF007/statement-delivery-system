#!/usr/bin/env bash
# Mints a development bearer token for the Delivery API, signed locally with the key in .env.
#
#   ./scripts/demo-token.sh                 # customer scope for the first demo customer
#   ./scripts/demo-token.sh staff           # + operator scope (audit, holds, runs, reconciliation)
#   ./scripts/demo-token.sh dpo             # + erasure scope (stacks on staff)
#   ./scripts/demo-token.sh customer <id>   # any customer id; the demo seed has ...101 to ...110
#
# Only the token goes to stdout, so `TOKEN=$(./scripts/demo-token.sh)` works. Hints go to stderr.
# The signing key is read from .env (JWT_DEV_SIGNING_KEY); it is never hard-coded here, and the
# API refuses to start with a symmetric key outside Development, so this can only ever talk to a
# local stack. Needs bash, openssl and base64 - all present in Git Bash on Windows.
set -euo pipefail

scope="${1:-customer}"
customer_id="${2:-11111111-1111-1111-1111-111111111101}"

case "$scope" in
  customer|staff|dpo) ;;
  -h|--help|help)
    sed -n '2,10p' "$0" >&2
    exit 0 ;;
  *)
    echo "usage: $0 [customer|staff|dpo] [customerId]" >&2
    exit 2 ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
env_file="$root/.env"
if [[ ! -f "$env_file" ]]; then
  echo "error: $env_file not found. Run 'cp .env.example .env' in the repo root first." >&2
  exit 1
fi

# Read the three JWT settings from .env without sourcing it (it contains other secrets and
# arbitrary shell-unsafe values).
read_env() { sed -n "s/^$1=//p" "$env_file" | tail -n 1 | tr -d '\r'; }
key="$(read_env JWT_DEV_SIGNING_KEY)"
issuer="$(read_env JWT_ISSUER)"
audience="$(read_env JWT_AUDIENCE)"

if [[ -z "$key" ]]; then
  echo "error: JWT_DEV_SIGNING_KEY is not set in .env, so nothing can be signed." >&2
  exit 1
fi
if [[ ${#key} -lt 32 ]]; then
  echo "error: JWT_DEV_SIGNING_KEY must be at least 32 characters (HMAC-SHA256)." >&2
  exit 1
fi

b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }

now="$(date +%s)"
exp="$((now + 3600))"

# Same claims the API's own /v1/dev/tokens endpoint issues: sub is the customer, and the scope
# claim is only present when asked for. Scope values mirror DeliveryApiExtensions.
case "$scope" in
  customer) scope_claim="" ;;
  staff)    scope_claim=',"scope":"audit.verify"' ;;
  dpo)      scope_claim=',"scope":"audit.verify erasure.execute"' ;;
esac

header='{"alg":"HS256","typ":"JWT"}'
payload="{\"sub\":\"$customer_id\",\"iss\":\"$issuer\",\"aud\":\"$audience\",\"iat\":$now,\"nbf\":$now,\"exp\":$exp$scope_claim}"

signing_input="$(printf '%s' "$header" | b64url).$(printf '%s' "$payload" | b64url)"
signature="$(printf '%s' "$signing_input" | openssl dgst -sha256 -hmac "$key" -binary | b64url)"

echo "token for $customer_id (scope: $scope), valid one hour. Paste it into the Bearer Token box at the top of the Scalar page:" >&2
echo "  http://localhost:${DELIVERY_API_PORT:-$(read_env DELIVERY_API_PORT)}/scalar/v1" >&2
printf '%s.%s\n' "$signing_input" "$signature"
