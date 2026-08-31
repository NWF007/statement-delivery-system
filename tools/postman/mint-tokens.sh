#!/usr/bin/env bash
# Mints the four dev JWTs the Postman collection uses and prints them ready to paste into the
# environment. Uses the running Delivery.Api's own /v1/dev/tokens endpoint, so the signing key
# never leaves the service - there is nothing secret in this script.
#
# The collection mints these in-band too (see its pre-request script); this exists for when you
# want them on the clipboard, or to sanity-check the endpoint outside Postman.
#
# Usage:  DELIVERY_URL=http://localhost:8081 ./mint-tokens.sh [customerA-guid] [customerB-guid]
set -euo pipefail

DELIVERY_URL="${DELIVERY_URL:-http://localhost:8081}"
CUSTOMER_A="${1:-11111111-1111-1111-1111-111111111111}"
CUSTOMER_B="${2:-22222222-2222-2222-2222-222222222222}"

mint() {  # customerId  query-suffix
  local resp
  resp="$(curl -fsS -X POST "${DELIVERY_URL}/v1/dev/tokens?customerId=${1}${2}")" || {
    echo "  mint failed against ${DELIVERY_URL} - is the API up and in Development?" >&2
    exit 1
  }
  # Extract accessToken without a JSON dependency.
  echo "$resp" | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p'
}

echo "Delivery: ${DELIVERY_URL}"
echo "Customer A: ${CUSTOMER_A}"
echo "Customer B: ${CUSTOMER_B}"
echo
echo "jwtCustomerA = $(mint "${CUSTOMER_A}" '')"
echo "jwtCustomerB = $(mint "${CUSTOMER_B}" '')"
echo "jwtStaff     = $(mint "${CUSTOMER_A}" '&staff=true')"
# DPO scope stacks ON staff - the endpoint only adds erasure.execute when BOTH flags are present.
echo "jwtDpo       = $(mint "${CUSTOMER_A}" '&staff=true&dpo=true')"
echo
echo "Paste each value into the matching (secret) variable in the local environment."
