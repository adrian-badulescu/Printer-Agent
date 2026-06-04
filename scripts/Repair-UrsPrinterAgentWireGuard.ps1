# Repairs WireGuard tunnel for URS Printer Agent when .conf or WireGuardTunnel$* service is missing.
# Run PowerShell as Administrator.

$ErrorActionPreference = 'Continue'
$dataDir = Join-Path $env:ProgramData 'URSPrinterAgent'
$confPath = Join-Path $dataDir 'wireguard\urs-printer-agent.conf'
$wgService = 'WireGuardTunnel$urs-printer-agent'
$agentSvc = 'URSPrinterAgent'

Write-Host '=== 1) WireGuard for Windows installed? ===' -ForegroundColor Cyan
$wgExe = @(
    "${env:ProgramW6432}\WireGuard\wireguard.exe",
    "${env:ProgramFiles}\WireGuard\wireguard.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $wgExe) {
    Write-Warning 'wireguard.exe not found. Install WireGuard (URSPrinterAgentSetup bundle or https://www.wireguard.com/install/).'
    exit 1
}
Write-Host "OK: $wgExe"

Write-Host "`n=== 2) Agent config .conf ===" -ForegroundColor Cyan
if (-not (Test-Path $confPath)) {
    Write-Warning "Missing $confPath"
    Write-Host 'The agent downloads this from GET /api/agents/{agentId}/wireguard-conf after enroll.'
    Write-Host 'Restart the agent service and watch worker.log for "WireGuard provisioning".'
    Write-Host 'If the backend returns HTTP 400, fix PrinterAgent:WireGuard (+ SSH) on the API host.'
    Write-Host ''
    Write-Host "Restart-Service $agentSvc"
    exit 2
}
Write-Host "OK: $confPath"

Write-Host "`n=== 3) Tunnel Windows service ===" -ForegroundColor Cyan
$svc = Get-Service -Name $wgService -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Installing tunnel service from .conf ..."
    & $wgExe /installtunnelservice $confPath
    $svc = Get-Service -Name $wgService -ErrorAction SilentlyContinue
}
if (-not $svc) {
    Write-Warning "Service $wgService still missing after installtunnelservice."
    exit 3
}
Write-Host "Service: $($svc.Status)"
if ($svc.Status -ne 'Running') {
    Start-Service -Name $wgService
    Start-Sleep -Seconds 3
    $svc = Get-Service -Name $wgService
    Write-Host "After start: $($svc.Status)"
}

Write-Host "`n=== 4) Restart printer agent ===" -ForegroundColor Cyan
Restart-Service -Name $agentSvc -Force
Write-Host 'Done. Retry bill print; job status should leave Received within seconds.'
