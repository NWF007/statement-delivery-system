<#
.SYNOPSIS
  Mints the four dev JWTs the Postman collection uses and prints them ready to paste.

.DESCRIPTION
  Calls the running Delivery.Api's /v1/dev/tokens endpoint, which signs with the service's own
  key - so this script holds no secret. The collection also mints these in-band (see its
  pre-request script); this is for getting them on the clipboard or checking the endpoint directly.

.EXAMPLE
  ./mint-tokens.ps1
  ./mint-tokens.ps1 -DeliveryUrl http://localhost:8081 -CustomerA 11111111-1111-1111-1111-111111111111
#>
param(
  [string]$DeliveryUrl = "http://localhost:8081",
  [string]$CustomerA   = "11111111-1111-1111-1111-111111111111",
  [string]$CustomerB   = "22222222-2222-2222-2222-222222222222"
)

$ErrorActionPreference = "Stop"

function Mint([string]$CustomerId, [string]$QuerySuffix) {
  try {
    $resp = Invoke-RestMethod -Method Post -Uri "$DeliveryUrl/v1/dev/tokens?customerId=$CustomerId$QuerySuffix"
    return $resp.accessToken
  } catch {
    Write-Error "mint failed against $DeliveryUrl - is the API up and in Development? $_"
    exit 1
  }
}

Write-Host "Delivery:   $DeliveryUrl"
Write-Host "Customer A: $CustomerA"
Write-Host "Customer B: $CustomerB"
Write-Host ""
Write-Host "jwtCustomerA = $(Mint $CustomerA '')"
Write-Host "jwtCustomerB = $(Mint $CustomerB '')"
Write-Host "jwtStaff     = $(Mint $CustomerA '&staff=true')"
# DPO scope stacks ON staff - the endpoint only adds erasure.execute when BOTH flags are present.
Write-Host "jwtDpo       = $(Mint $CustomerA '&staff=true&dpo=true')"
Write-Host ""
Write-Host "Paste each value into the matching (secret) variable in the local environment."
