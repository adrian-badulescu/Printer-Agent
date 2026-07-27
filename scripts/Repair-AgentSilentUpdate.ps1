#Requires -RunAsAdministrator
# Reinstall / upgrade URS Printer Agent after a failed silent auto-update.
# Preserves %ProgramData%\URSPrinterAgent (enrollment, printers, session).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$localExe = Get-ChildItem "$env:TEMP\PrinterAgent_Update_*.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($localExe) {
    $installer = $localExe.FullName
    Write-Host "Using cached installer: $installer"
} else {
    $installer = Join-Path $env:TEMP 'URSPrinterAgentSetup_repair.exe'
    Write-Host "Downloading latest URSPrinterAgentSetup.exe..."
    Invoke-WebRequest -Uri 'https://github.com/adrian-badulescu/Printer-Agent/releases/latest/download/URSPrinterAgentSetup.exe' `
        -OutFile $installer -UseBasicParsing
}

$log = Join-Path $env:TEMP 'urs-manual-repair.log'
Write-Host "Running silent install (1-2 min). Log: $log"

$p = Start-Process -FilePath $installer -ArgumentList "/quiet /norestart /log `"$log`"" -Wait -PassThru
Write-Host "Installer exit code: $($p.ExitCode)"

Write-Host ''
Write-Host '=== Service ===' -ForegroundColor Cyan
Get-Service URSPrinterAgent -ErrorAction SilentlyContinue | Format-Table Status, Name, StartType -AutoSize

$agentJson = Join-Path ${env:ProgramFiles} 'URSPrinterAgent\agent.json'
if (Test-Path -LiteralPath $agentJson) {
    $version = (Get-Content -LiteralPath $agentJson -Raw | ConvertFrom-Json).Version
    Write-Host "Install-dir Version: $version"
} else {
    Write-Host 'WARN: agent.json missing in Program Files' -ForegroundColor Yellow
}

$session = Join-Path $env:ProgramData 'URSPrinterAgent\agent.session.json'
Write-Host "Session preserved: $(Test-Path -LiteralPath $session)"

if ($p.ExitCode -ne 0 -or -not (Get-Service URSPrinterAgent -ErrorAction SilentlyContinue)) {
    Write-Host ''
    Write-Host '=== Installer log (last 40 lines) ===' -ForegroundColor Yellow
    if (Test-Path -LiteralPath $log) {
        Get-Content -LiteralPath $log -Tail 40
    } else {
        Write-Host "Log not found: $log"
    }
    Write-Host ''
    Write-Host 'If service still missing, run installer interactively:' -ForegroundColor Yellow
    Write-Host "  Start-Process '$installer' -Verb RunAs"
    exit 1
}

Write-Host ''
Write-Host 'OK. Tail worker log:' -ForegroundColor Green
Write-Host '  Get-Content "$env:ProgramData\URSPrinterAgent\logs\worker.log" -Tail 15 -Wait'
