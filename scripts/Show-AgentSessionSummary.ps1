# Reads %ProgramData%\URSPrinterAgent\agent.session.json and prints non-secret fields.
# Does not decrypt DPAPI blobs; shows only cleartext columns if present.

$ErrorActionPreference = 'Stop'
$path = Join-Path $env:ProgramData 'URSPrinterAgent\agent.session.json'

if (-not (Test-Path $path)) {
    Write-Warning "Not found: $path (enroll may not have run yet)."
    exit 1
}

$json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json

Write-Host "File: $path"
Write-Host "AgentId:        $($json.agentId)"
Write-Host "RestaurantId:   $($json.restaurantId)"
Write-Host "ExpiresAtUtc:   $($json.expiresAtUtc)"
if ($json.accessTokenProtected) { Write-Host "Access token:   protected (DPAPI)" }
elseif ($json.accessToken) { Write-Host "Access token:   present (plaintext)" }
else { Write-Host "Access token:   (missing)" }
if ($json.refreshTokenProtected) { Write-Host "Refresh token:  protected (DPAPI)" }
elseif ($json.refreshToken) { Write-Host "Refresh token:  present (plaintext)" }
else { Write-Host "Refresh token:  (missing - old session or pre-refresh build)" }

$inst = Join-Path $env:ProgramData 'URSPrinterAgent\client.instance'
if (Test-Path $inst) {
    Write-Host "ClientInstance: $((Get-Content -LiteralPath $inst -Raw).Trim())"
} else {
    Write-Host "ClientInstance: (file missing - created on first enroll)"
}
