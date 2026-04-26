# Dezinstalează URS Printer Agent prin msiexec și scrie log verbose (Windows Installer).
# Rulează PowerShell ca Administrator.
#
# Exemplu (același MSI ca la instalare sau MSI din build curent):
#   .\Uninstall-UrsPrinterAgent-WithMsiLog.ps1 -MsiPath "..\PrinterAgent.Installer\bin\Release\PrinterAgent.msi"
#
# Dacă ai ProductCode (GUID din „Uninstall” în registru):
#   .\Uninstall-UrsPrinterAgent-WithMsiLog.ps1 -ProductCode "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}"
#
# Dacă vezi exit 1605: „This action is only valid for products that are currently installed” — MSI-ul
# folosit are alt ProductCode decât instanța instalată (alt build). Folosește MSI-ul de la instalare
# sau -ProductCode din registru. Serviciu orfan: .\Uninstall-UrsPrinterAgent.ps1
#
# Folosește msiexec x64 (System32 sau Sysnative) ca dezinstalarea unui MSI x64 din PowerShell x86
# să nu lase serviciul în SCM (log vechi: Calling process SysWOW64\msiexec.exe).

param(
    [string] $MsiPath = "",
    [string] $ProductCode = "",
    [string] $LogPath = ""
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw 'Rulează PowerShell ca Administrator (dezinstalarea MSI necesită elevation).'
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $LogPath = Join-Path $env:TEMP "URSPrinterAgent-uninstall-$stamp.log"
}

# Pachetul e x64: de pe proces PowerShell x86, „msiexec” pornește SysWOW64 și dezinstalarea poate lăsa serviciul agățat.
function Get-MsiexecPath {
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
        $sysnative = Join-Path $env:Windir 'Sysnative\msiexec.exe'
        if (Test-Path -LiteralPath $sysnative) { return $sysnative }
    }
    return Join-Path $env:Windir 'System32\msiexec.exe'
}

$script:MsiexecPath = Get-MsiexecPath
if (-not (Test-Path -LiteralPath $script:MsiexecPath)) {
    $script:MsiexecPath = 'msiexec.exe'
}

function Invoke-MsiexecUninstall {
    param([string[]] $ArgumentList)
    Write-Host ("Using: " + $script:MsiexecPath)
    Write-Host ("msiexec " + ($ArgumentList -join ' '))
    $p = Start-Process -FilePath $script:MsiexecPath -ArgumentList $ArgumentList -Wait -PassThru -NoNewWindow
    $code = $p.ExitCode
    Write-Host "Exit code: $code"
    Write-Host "Log: $LogPath"
    if ($code -eq 1605) {
        Write-Warning (
            'MSI 1605: acest pachet nu e înregistrat ca instalat (ProductCode diferit față de instalarea veche). ' +
            'Folosește MSI-ul cu care s-a instalat, sau -ProductCode din HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\. ' +
            'Dacă a rămas doar serviciul: .\Uninstall-UrsPrinterAgent.ps1')
    }
    exit $code
}

if (-not [string]::IsNullOrWhiteSpace($ProductCode)) {
    $pc = $ProductCode.Trim()
    if ($pc[0] -ne '{') {
        $pc = '{' + $pc.TrimStart('{').TrimEnd('}') + '}'
    }
    Invoke-MsiexecUninstall -ArgumentList @('/x', $pc, '/l*v', $LogPath)
}

if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    throw 'Specifică -MsiPath (cale la .msi) sau -ProductCode {GUID}.'
}

if (-not (Test-Path -LiteralPath $MsiPath)) {
    throw "Nu există fișierul: $MsiPath"
}

$resolved = (Resolve-Path -LiteralPath $MsiPath).Path
Invoke-MsiexecUninstall -ArgumentList @('/x', $resolved, '/l*v', $LogPath)
