# Prints a development JWT for one customer, minted by the running Delivery.Api's own
# /v1/dev/tokens endpoint (Development only; the signing key never leaves the service).
#
# Usage:  .\scripts\demo-token.ps1 <customerId> [staff|dpo]
#         $TOKEN = .\scripts\demo-token.ps1 $CUSTOMER_ID
#         $STAFF = .\scripts\demo-token.ps1 $CUSTOMER_ID staff     # adds the operator scope
#         $DPO   = .\scripts\demo-token.ps1 $CUSTOMER_ID dpo       # operator + erasure scope
#
# Override the API with $env:API_URL (default http://localhost:8081).
param(
    [Parameter(Mandatory = $true)][string]$CustomerId,
    [ValidateSet('', 'staff', 'dpo')][string]$Scope = ''
)
$ErrorActionPreference = 'Stop'
$api = if ($env:API_URL) { $env:API_URL } else { 'http://localhost:8081' }
$query = switch ($Scope) { 'staff' { '&staff=true' } 'dpo' { '&staff=true&dpo=true' } default { '' } }

try {
    $resp = Invoke-RestMethod -Method Post -Uri "$api/v1/dev/tokens?customerId=$CustomerId$query"
} catch {
    Write-Error "could not mint a token from $api - is the stack up (docker compose ps) and ASPNETCORE_ENVIRONMENT=Development? $($_.Exception.Message)"
    exit 1
}
$resp.accessToken
