# Installs or updates the URS Printer Agent Windows service.
# Run elevated. Example:
#   .\Install-UrsPrinterAgent.ps1 -BinaryPath "C:\Program Files\URSPrinterAgent\PrinterAgent.Worker.exe"
# Remove service only: .\Uninstall-UrsPrinterAgent.ps1
#
# Before running: execute Setup-ProgramData.ps1 and edit %ProgramData%\URSPrinterAgent\agent.json
#
# Default service logon: Local System (sufficient for ProgramData access in most cases).
# If you use a custom account, grant it Modify on %ProgramData%\URSPrinterAgent.
#
# After install: .\Show-AgentSessionSummary.ps1 — confirm agentId, restaurantId, refreshTokenProtected.
# Docs: docs\E2E_AGENT_DEPLOYMENT_CHECKLIST.md

param(
    [Parameter(Mandatory = $true)]
    [string] $BinaryPath,

    [string] $ServiceName = 'URSPrinterAgent',

    [string] $DisplayName = 'URS Printer Agent'
)

$ErrorActionPreference = 'Stop'

$agentJson = Join-Path $env:ProgramData 'URSPrinterAgent\agent.json'
if (-not (Test-Path -LiteralPath $agentJson)) {
    throw @"
Missing $agentJson
The worker loads ONLY this file (not the repo copy). Create it first, e.g.:
  .\Setup-ProgramData.ps1 -CopyTemplateFrom `"$PSScriptRoot\..\PrinterAgent.Worker\agent.json`"
Then edit BackendUrl, Redis, EnrollmentCode in ProgramData, then run this install again.
"@
}

if (-not (Test-Path $BinaryPath)) {
    throw "Binary not found: $BinaryPath"
}

if ($BinaryPath -match '\\net10\.0\\PrinterAgent\.Worker\.exe$' -and $BinaryPath -notmatch 'win-x64') {
    Write-Warning "Expected path usually contains ``net10.0\win-x64\`` or ``...\win-x64\publish\`` (RID win-x64 / single-file). Verify you are not pointing at an old or empty folder."
}

$fullPath = (Resolve-Path $BinaryPath).Path
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existing) {
    if ($existing.Status -eq 'Running') {
        Stop-Service -Name $ServiceName -Force
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

New-Service -Name $ServiceName `
    -BinaryPathName "`"$fullPath`"" `
    -DisplayName $DisplayName `
    -StartupType Automatic `
    -Description 'URS restaurant printer agent (Redis streams, heartbeat, ESC/POS).' | Out-Null

Set-Service -Name $ServiceName -StartupType Automatic
Start-Service -Name $ServiceName

Write-Host "Service $ServiceName installed and started. Config: $env:ProgramData\URSPrinterAgent\agent.json"
