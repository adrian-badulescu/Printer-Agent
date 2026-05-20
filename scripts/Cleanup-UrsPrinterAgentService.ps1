# Removes a zombie URSPrinterAgent service (DISABLED + DeleteFlag=1 after failed upgrade).
# sc stop may return 1062 (not started) - that is OK. Run as Administrator.
#
# Usage: .\Cleanup-UrsPrinterAgentService.ps1
# Then: reinstall URSPrinterAgentSetup.exe from GitHub CI, OR if EXE already exists:
#       .\Install-UrsPrinterAgent.ps1 -BinaryPath "C:\Program Files\URSPrinterAgent\PrinterAgent.Worker.exe"

#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$ServiceName = 'URSPrinterAgent'
$RegPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$ExePath = Join-Path ${env:ProgramFiles} 'URSPrinterAgent\PrinterAgent.Worker.exe'

Write-Host '=== URS Printer Agent service cleanup ==='

$stopOut = (& sc.exe stop $ServiceName 2>&1 | Out-String).Trim()
if ($stopOut -match '1062') {
    Write-Host 'sc stop: service not running (1062) - OK.'
}
elseif ($LASTEXITCODE -eq 0) {
    Write-Host 'sc stop: OK.'
}
else {
    Write-Host "sc stop: $stopOut"
}

Start-Sleep -Seconds 2
$delOut = (& sc.exe delete $ServiceName 2>&1 | Out-String).Trim()
Write-Host "sc delete: $delOut"
Start-Sleep -Seconds 2

if (Test-Path -LiteralPath $RegPath) {
    $props = Get-ItemProperty -LiteralPath $RegPath -ErrorAction SilentlyContinue
    if ($null -ne $props.DeleteFlag) {
        Write-Host "DeleteFlag=$($props.DeleteFlag) - removing registry key (reboot not required)."
    }
    Remove-Item -LiteralPath $RegPath -Force -Recurse
    Write-Host "Registry key removed: $RegPath"
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service $ServiceName still visible. Reboot once, then run this script again."
}

Write-Host 'Service registration cleared.'

if (Test-Path -LiteralPath $ExePath) {
    Write-Host ''
    Write-Host 'EXE is present. Recreate the service without full MSI:'
    Write-Host "  .\Install-UrsPrinterAgent.ps1 -BinaryPath `"$ExePath`""
    Write-Host ''
    Write-Host 'Or run URSPrinterAgentSetup.exe from GitHub Actions (recommended for upgrades).'
}
else {
    Write-Host 'EXE missing. Install URSPrinterAgentSetup.exe from GitHub Actions artifact.'
}
