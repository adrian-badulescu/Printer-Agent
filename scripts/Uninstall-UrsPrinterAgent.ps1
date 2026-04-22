# Removes the URS Printer Agent Windows service (does not remove %ProgramData%\URSPrinterAgent).
# Run elevated. Example:
#   .\Uninstall-UrsPrinterAgent.ps1
#
# Reinstall: .\Install-UrsPrinterAgent.ps1 -BinaryPath "...\PrinterAgent.Worker.exe"

param(
    [string] $ServiceName = 'URSPrinterAgent'
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw 'Run PowerShell as Administrator (service install/remove requires elevation).'
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' is not installed."
    exit 0
}

if ($existing.Status -eq 'Running') {
    Write-Host "Stopping $ServiceName..."
    Stop-Service -Name $ServiceName -Force
}

Write-Host "Removing $ServiceName..."
# sc.exe is reliable on all supported Windows; Remove-Service exists on newer builds only.
$null = sc.exe delete $ServiceName
Start-Sleep -Seconds 2

$still = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($still) {
    throw "Service still present after delete. Try: sc.exe query $ServiceName"
}

Write-Host "Removed. Config/data left at: $env:ProgramData\URSPrinterAgent\"
