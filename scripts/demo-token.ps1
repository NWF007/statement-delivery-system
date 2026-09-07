<#
.SYNOPSIS
  Mints a development bearer token for the Delivery API, signed locally with the key in .env.

.DESCRIPTION
  .\scripts\demo-token.ps1                    # customer scope for the first demo customer
  .\scripts\demo-token.ps1 staff              # + operator scope (audit, holds, runs, reconciliation)
  .\scripts\demo-token.ps1 dpo                # + erasure scope (stacks on staff)
  .\scripts\demo-token.ps1 customer <id>      # any customer id; the demo seed has ...101 to ...110

  Only the token is written to the output stream, so $TOKEN = .\scripts\demo-token.ps1 works.
  Hints go to the information/error streams. The signing key is read from .env
  (JWT_DEV_SIGNING_KEY), never hard-coded; the API refuses to start with a symmetric key outside
  Development, so this can only ever talk to a local stack. Works on Windows PowerShell 5.1 and
  PowerShell 7.
#>
[CmdletBinding()]
param(
    [ValidateSet('customer', 'staff', 'dpo')]
    [string] $Scope = 'customer',

    [string] $CustomerId = '11111111-1111-1111-1111-111111111101'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) {
    Write-Error "$envFile not found. Run 'Copy-Item .env.example .env' in the repo root first."
    exit 1
}

# Read the JWT settings from .env without dot-sourcing it (it holds other secrets).
$settings = @{}
foreach ($line in Get-Content $envFile) {
    if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $settings[$Matches[1]] = $Matches[2].Trim() }
}

$key = $settings['JWT_DEV_SIGNING_KEY']
$issuer = $settings['JWT_ISSUER']
$audience = $settings['JWT_AUDIENCE']
$port = if ($settings['DELIVERY_API_PORT']) { $settings['DELIVERY_API_PORT'] } else { '8081' }

if ([string]::IsNullOrWhiteSpace($key)) {
    Write-Error 'JWT_DEV_SIGNING_KEY is not set in .env, so nothing can be signed.'
    exit 1
}
if ($key.Length -lt 32) {
    Write-Error 'JWT_DEV_SIGNING_KEY must be at least 32 characters (HMAC-SHA256).'
    exit 1
}

function ConvertTo-Base64Url([byte[]] $bytes) {
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$exp = $now + 3600

# Same claims the API's own /v1/dev/tokens endpoint issues. Scope values mirror DeliveryApiExtensions.
$payload = [ordered]@{
    sub = $CustomerId
    iss = $issuer
    aud = $audience
    iat = $now
    nbf = $now
    exp = $exp
}
switch ($Scope) {
    'staff' { $payload['scope'] = 'audit.verify' }
    'dpo'   { $payload['scope'] = 'audit.verify erasure.execute' }
}

$utf8 = [System.Text.Encoding]::UTF8
$header = ConvertTo-Base64Url $utf8.GetBytes('{"alg":"HS256","typ":"JWT"}')
$body = ConvertTo-Base64Url $utf8.GetBytes(($payload | ConvertTo-Json -Compress))
$signingInput = "$header.$body"

$hmac = New-Object System.Security.Cryptography.HMACSHA256
$hmac.Key = $utf8.GetBytes($key)
$signature = ConvertTo-Base64Url ($hmac.ComputeHash($utf8.GetBytes($signingInput)))
$hmac.Dispose()

# Hints on stderr so they never land in a pipe or a captured variable, from any host shell.
[Console]::Error.WriteLine("token for $CustomerId (scope: $Scope), valid one hour. Paste it into the Bearer Token box at the top of the Scalar page:")
[Console]::Error.WriteLine("  http://localhost:$port/scalar/v1")
Write-Output "$signingInput.$signature"
