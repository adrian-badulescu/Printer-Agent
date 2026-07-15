# Full local cleanup: URS Printer Agent service + WireGuard tunnel + ProgramData.
# Does NOT remove files under Program Files (use MSI/bundle uninstall for that).
#
# Run PowerShell as Administrator. Example:
#   .\Uninstall-UrsPrinterAgent-Full.ps1
#   .\Uninstall-UrsPrinterAgent-Full.ps1 -KeepProgramData
#
# Complete removal (binaries + this cleanup):
#   Start-Process .\URSPrinterAgentSetup.exe -ArgumentList '/uninstall /quiet /norestart' -Wait
#   .\Uninstall-UrsPrinterAgent-Full.ps1
#
# Server-side peer removal (hub WireGuard): Manager UI → remove agent installation.

#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [switch] $KeepProgramData
)

if (-not [Environment]::Is64BitProcess) {
    Write-Host 'Relaunching in 64-bit PowerShell...' -ForegroundColor Yellow
    $ps64 = Join-Path $env:WINDIR 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $ps64)) {
        $ps64 = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    }
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($KeepProgramData) { $args += '-KeepProgramData' }
    & $ps64 @args
    exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'

$agentSvc = 'URSPrinterAgent'
$wgService = 'WireGuardTunnel$urs-printer-agent'
$wgTunnelName = 'urs-printer-agent'
$dataDir = Join-Path $env:ProgramData 'URSPrinterAgent'
$wgConf = Join-Path $dataDir 'wireguard\urs-printer-agent.conf'
$wgDir = Join-Path $dataDir 'wireguard'

function Resolve-WireGuardExe {
    @(
        "${env:ProgramW6432}\WireGuard\wireguard.exe",
        "${env:ProgramFiles}\WireGuard\wireguard.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Stop-ServiceSafe {
    param([string] $Name)
    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Host "  Service '$Name' not installed."
        return $false
    }
    if ($svc.Status -eq 'Running') {
        Write-Host "  Stopping $Name..."
        Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    return $true
}

function Remove-WindowsService {
    param(
        [string] $Name,
        [string] $RegistrySubKey = $Name
    )
    if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
        Write-Host "  Service '$Name' already absent."
        return
    }

    Write-Host "  Removing service $Name..."
    $null = sc.exe stop $Name 2>$null
    $null = sc.exe delete $Name 2>$null
    $null = reg.exe delete "HKLM\SYSTEM\CurrentControlSet\Services\$RegistrySubKey" /f 2>$null
    Start-Sleep -Seconds 2

    if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
        Write-Warning "  Service '$Name' still present. Reboot may be required."
    } else {
        Write-Host "  Removed $Name."
    }
}

Write-Host '=== URS Printer Agent — full local cleanup ===' -ForegroundColor Cyan

Write-Host ''
Write-Host '=== 1) Stop and remove URSPrinterAgent ===' -ForegroundColor Cyan
Stop-ServiceSafe -Name $agentSvc | Out-Null
Remove-WindowsService -Name $agentSvc

Write-Host ''
Write-Host '=== 2) Stop and remove WireGuard tunnel ===' -ForegroundColor Cyan
$wgExe = Resolve-WireGuardExe
if ($wgExe) {
    Write-Host "  wireguard.exe: $wgExe"
} else {
    Write-Warning '  wireguard.exe not found; will try sc.exe delete on tunnel service only.'
}

if (Stop-ServiceSafe -Name $wgService) {
    if ($wgExe) {
        try {
            & $wgExe /uninstalltunnelservice $wgTunnelName
            Write-Host "  Uninstalled tunnel via: wireguard.exe /uninstalltunnelservice $wgTunnelName"
        } catch {
            Write-Warning "  wireguard.exe uninstall failed: $($_.Exception.Message)"
        }
    }
    Remove-WindowsService -Name $wgService
}

if (Test-Path -LiteralPath $wgConf) {
    Remove-Item -LiteralPath $wgConf -Force
    Write-Host "  Deleted $wgConf"
}

if ((Test-Path -LiteralPath $wgDir) -and -not (Get-ChildItem -LiteralPath $wgDir -Force -ErrorAction SilentlyContinue)) {
    Remove-Item -LiteralPath $wgDir -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host '=== 3) ProgramData ===' -ForegroundColor Cyan
if ($KeepProgramData) {
    Write-Host "  Kept $dataDir ( -KeepProgramData )."
} elseif (Test-Path -LiteralPath $dataDir) {
    Remove-Item -LiteralPath $dataDir -Recurse -Force
    Write-Host "  Deleted $dataDir"
} else {
    Write-Host "  (no $dataDir)"
}

Write-Host ''
Write-Host '=== Done ===' -ForegroundColor Green
Write-Host 'Local agent service and WireGuard tunnel removed.'
Write-Host 'To remove binaries from Program Files, uninstall URS Printer Agent from Windows Settings'
Write-Host 'or run URSPrinterAgentSetup.exe /uninstall /quiet /norestart.'
Write-Host 'To revoke the agent on the server (WireGuard hub peer), use Manager → Settings → remove installation.'
