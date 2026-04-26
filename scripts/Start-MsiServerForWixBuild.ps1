# WiX (wix.exe) folosește API-ul Windows Installer; dacă serviciul e oprit, build-ul MSI dă 1631.
# Rulează PowerShell ca Administrator, apoi reîncearcă: dotnet build PrinterAgent.Installer\PrinterAgent.Installer.wixproj

$ErrorActionPreference = 'Stop'
$svc = Get-Service -Name msiserver -ErrorAction Stop
if ($svc.Status -ne 'Running') {
    Start-Service -Name msiserver
    Write-Host 'Started msiserver (Windows Installer).'
} else {
    Write-Host 'msiserver already Running.'
}
