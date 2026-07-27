#Requires -RunAsAdministrator
# Restores URSPrinterAgent Windows service without re-enrollment.
# Use when auto-update left the service missing but ProgramData session/printers remain.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workerExe = Join-Path ${env:ProgramFiles} 'URSPrinterAgent\PrinterAgent.Worker.exe'
$updatesDir = Join-Path $env:ProgramData 'URSPrinterAgent\updates'
$sessionPath = Join-Path $env:ProgramData 'URSPrinterAgent\agent.session.json'

Write-Host '=== Repair URS Printer Agent service ===' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $workerExe)) {
    throw "Missing $workerExe — run URSPrinterAgentSetup.exe first."
}

foreach ($lockFile in @(
        (Join-Path $updatesDir '.update-in-progress'),
        (Join-Path $updatesDir 'update-state.json'))) {
    if (Test-Path -LiteralPath $lockFile) {
        Remove-Item -LiteralPath $lockFile -Force
        Write-Host "Removed $lockFile"
    }
}

& (Join-Path $PSScriptRoot 'Install-UrsPrinterAgent.ps1') -BinaryPath $workerExe

Write-Host ''
Write-Host "Session file: $(Test-Path -LiteralPath $sessionPath)" -ForegroundColor Cyan
if (Test-Path -LiteralPath $sessionPath) {
    $session = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
    Write-Host "  agentId:      $($session.agentId)"
    Write-Host "  restaurantId: $($session.restaurantId)"
}

Get-Service URSPrinterAgent | Format-Table Status, Name, StartType -AutoSize
Write-Host 'If manager UI still shows offline, wait for heartbeat (~30s) or restart service once.' -ForegroundColor Green
