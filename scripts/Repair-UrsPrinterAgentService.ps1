# Repairs URSPrinterAgent when service is DISABLED / DeleteFlag zombie but files may exist.
# Run as Administrator. For full cleanup use Cleanup-UrsPrinterAgentService.ps1 first.
#
# Usage: .\Repair-UrsPrinterAgentService.ps1

#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
& (Join-Path $here 'Cleanup-UrsPrinterAgentService.ps1')

$ExePath = Join-Path ${env:ProgramFiles} 'URSPrinterAgent\PrinterAgent.Worker.exe'
if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Host "Install URSPrinterAgentSetup.exe from GitHub CI, then re-run if needed."
    exit 1
}

& (Join-Path $here 'Install-UrsPrinterAgent.ps1') -BinaryPath $ExePath
Write-Host "Done."
