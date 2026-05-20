# Repairs URSPrinterAgent after a failed setup (service DISABLED, orphaned, or missing binary).
# Run elevated. If binary is missing, removes broken service registration so reinstall can succeed.
#
# Usage: .\Repair-UrsPrinterAgentService.ps1

#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$ServiceName = 'URSPrinterAgent'
$InstallDir = Join-Path ${env:ProgramFiles} 'URSPrinterAgent'
$ExePath = Join-Path $InstallDir 'PrinterAgent.Worker.exe'

function Remove-BrokenService {
    param([string]$Name)
    $null = & sc.exe stop $Name 2>&1
    Start-Sleep -Seconds 2
    $del = & sc.exe delete $Name 2>&1 | Out-String
    Start-Sleep -Seconds 2
    if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
        throw "sc.exe delete failed (run this script as Administrator). Output: $del"
    }
    Write-Host "Removed service registration: $Name"
}

Write-Host "Install dir: $InstallDir"
$exeExists = Test-Path -LiteralPath $ExePath
Write-Host "Worker exe:  $(if ($exeExists) { 'OK' } else { 'MISSING' }) $ExePath"

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if (-not $exeExists) {
    Write-Warning @"
Binary missing — MSI install did not complete or files were removed.
Removing broken service (if any), then reinstall from GitHub Actions artifact (URSPrinterAgentSetup.exe).
"@
    if ($svc) { Remove-BrokenService -Name $ServiceName }
    Write-Host "Next: uninstall 'URS Printer Agent' from Settings -> Apps if listed, then run URSPrinterAgentSetup.exe from CI."
    exit 1
}

if (-not $svc) {
    Write-Error "Service $ServiceName not registered but EXE exists. Reinstall setup or run Install-UrsPrinterAgent.ps1."
}

if ($svc.Status -eq 'Running') {
    Stop-Service -Name $ServiceName -Force
}

& sc.exe config $ServiceName start= auto | Out-Null
$qc = & sc.exe qc $ServiceName
Write-Host ($qc -join [Environment]::NewLine)

if ($qc -match 'START_TYPE\s+:\s+4\s+DISABLED') {
    Write-Warning 'Service still DISABLED — removing and reinstalling setup is recommended.'
    Remove-BrokenService -Name $ServiceName
    Write-Host 'Re-run URSPrinterAgentSetup.exe from GitHub CI build.'
    exit 1
}

try {
    Start-Service -Name $ServiceName
    Write-Host "Service started OK."
} catch {
    Write-Error "Start failed: $_. Check %ProgramData%\URSPrinterAgent\logs\fatal-startup.txt"
}

Write-Host "Config: $env:ProgramData\URSPrinterAgent\agent.json"
Write-Host "Logs:   $env:ProgramData\URSPrinterAgent\logs\"
