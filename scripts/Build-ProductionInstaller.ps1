# Mirrors CI secret injection, then builds URSPrinterAgentSetup.exe locally.
# Usage (PowerShell, do not commit secrets):
#   $env:REDIS_PASSWORD = '...'
#   $env:REDIS_HOST = '10.60.0.2'
#   $env:BACKEND_URL = 'https://universalrestaurant.systems'  # optional
#   .\Build-ProductionInstaller.ps1

[CmdletBinding()]
param(
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
$agentJson = Join-Path $RepoRoot 'PrinterAgent.Worker\agent.json'

if (-not $env:REDIS_PASSWORD) {
    throw 'Set REDIS_PASSWORD environment variable (same as GitHub Actions secret).'
}

$json = Get-Content -LiteralPath $agentJson -Raw | ConvertFrom-Json
$json.Redis.Password = $env:REDIS_PASSWORD
if ($env:REDIS_HOST) { $json.Redis.Host = $env:REDIS_HOST }
if ($env:REDIS_USER) { $json.Redis.User = $env:REDIS_USER }
if ($env:BACKEND_URL) { $json.BackendUrl = $env:BACKEND_URL }

$content = $json | ConvertTo-Json -Depth 20
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($agentJson, $content, $utf8NoBom)

try {
    Push-Location $RepoRoot
    dotnet build PrinterAgent.Bundle/PrinterAgent.Bundle.wixproj -c Release -p:SelfSignedMsiSigning=true
    $out = Join-Path $RepoRoot 'PrinterAgent.Bundle\bin\Release\URSPrinterAgentSetup.exe'
    if (-not (Test-Path -LiteralPath $out)) { throw "Missing output: $out" }
    Write-Host "Built: $out" -ForegroundColor Green
} finally {
    # Restore template password placeholder so git working tree stays clean.
    $restore = Get-Content -LiteralPath $agentJson -Raw | ConvertFrom-Json
    $restore.Redis.Password = ''
    $restoreContent = $restore | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($agentJson, $restoreContent, $utf8NoBom)
    Pop-Location
}
