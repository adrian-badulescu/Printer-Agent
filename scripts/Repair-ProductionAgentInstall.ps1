# Repairs a pilot/dev install upgraded to production MSI (v1.2.7+).
# Run PowerShell as Administrator on the restaurant PC.
#
# Symptoms:
#   - ProgramData\agent.json still shows 192.168.x / Version 1.2.3 (stale; install-dir wins at runtime)
#   - WireGuard .conf still points at dev hub 192.168.43.142
#   - Redis NOAUTH on 10.60.0.2 (wrong password baked into MSI - rebuild with correct REDIS_PASSWORD secret)
#
# Usage:
#   .\Repair-ProductionAgentInstall.ps1
#   .\Repair-ProductionAgentInstall.ps1 -SetRedisPassword 'prod-redis-password'  # temporary until new MSI

#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string] $SetRedisPassword
)

# 32-bit PowerShell (SysWOW64) maps ProgramFiles to (x86) even when elevated.
if (-not [Environment]::Is64BitProcess) {
    Write-Host 'Relaunching in 64-bit PowerShell...' -ForegroundColor Yellow
    $ps64 = Join-Path $env:WINDIR 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $ps64)) {
        $ps64 = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    }
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($SetRedisPassword) {
        $args += '-SetRedisPassword'
        $args += $SetRedisPassword
    }
    & $ps64 @args
    exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
$dataDir = Join-Path $env:ProgramData 'URSPrinterAgent'
$pdJson = Join-Path $dataDir 'agent.json'
$wgConf = Join-Path $dataDir 'wireguard\urs-printer-agent.conf'
$wgService = 'WireGuardTunnel$urs-printer-agent'
$agentSvc = 'URSPrinterAgent'

function Resolve-UrsPrinterAgentInstallJson {
    $candidates = [System.Collections.Generic.List[string]]::new()

    $binLine = (& sc.exe qc $agentSvc 2>$null) | Where-Object { $_ -match 'BINARY_PATH_NAME' }
    if ($binLine -match 'BINARY_PATH_NAME\s*:\s*(.+)') {
        $exePath = $Matches[1].Trim() -replace '^"|"$', ''
        if (Test-Path -LiteralPath $exePath) {
            $candidates.Add((Join-Path (Split-Path -LiteralPath $exePath) 'agent.json'))
        }
    }

    foreach ($root in @(${env:ProgramW6432}, [Environment]::GetFolderPath('ProgramFiles'), ${env:ProgramFiles})) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $candidates.Add((Join-Path $root 'URSPrinterAgent\agent.json'))
    }

    foreach ($path in ($candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $path) { return $path }
    }

    return $null
}

$installJson = Resolve-UrsPrinterAgentInstallJson

Write-Host '=== Install-dir (effective for BackendUrl + Redis) ===' -ForegroundColor Cyan
if (-not $installJson) {
    throw 'Missing URSPrinterAgent install-dir agent.json (checked service binary path, Program Files, Program Files (x86)). Reinstall URSPrinterAgentSetup.exe from GitHub release.'
}
Write-Host "  Path:        $installJson"
$install = Get-Content -LiteralPath $installJson -Raw | ConvertFrom-Json
Write-Host "  Version:     $($install.Version)"
Write-Host "  BackendUrl:  $($install.BackendUrl)"
Write-Host "  Redis.Host:  $($install.Redis.Host)"
if ($install.Redis.Password) {
    Write-Host "  Redis.Pw:    (set, length=$($install.Redis.Password.Length))"
} else {
    Write-Host '  Redis.Pw:    (empty - MSI built without REDIS_PASSWORD inject)'
}

Write-Host ''
Write-Host '=== ProgramData (operator overrides; may be stale after upgrade) ===' -ForegroundColor Cyan
if (Test-Path -LiteralPath $pdJson) {
    $pd = Get-Content -LiteralPath $pdJson -Raw | ConvertFrom-Json
    Write-Host "  Version:     $($pd.Version)"
    Write-Host "  BackendUrl:  $($pd.BackendUrl)"
    Write-Host "  Redis.Host:  $($pd.Redis.Host)"
    if ($pd.BackendUrl -ne $install.BackendUrl -or $pd.Redis.Host -ne $install.Redis.Host) {
        Write-Warning 'ProgramData differs from install-dir. Runtime uses install-dir for BackendUrl/Redis (BundledFirstKeys).'
    }
} else {
    Write-Host '  (no ProgramData agent.json)'
}

if ($SetRedisPassword) {
    Write-Host ''
    Write-Host '=== Patching install-dir Redis.Password (temporary) ===' -ForegroundColor Cyan
    $agentRunning = $false
    $svcState = Get-Service -Name $agentSvc -ErrorAction SilentlyContinue
    if ($svcState -and $svcState.Status -eq 'Running') {
        $agentRunning = $true
        Write-Host '  Stopping URSPrinterAgent (releases lock on agent.json)...'
        Stop-Service -Name $agentSvc -Force
    }
    $install.Redis.Password = $SetRedisPassword
    $content = $install | ConvertTo-Json -Depth 20
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    try {
        [System.IO.File]::WriteAllText($installJson, $content, $utf8NoBom)
        Write-Host '  Updated install-dir agent.json Redis.Password'
    } catch {
        throw "Could not write $installJson : $($_.Exception.Message). Ensure this window is Administrator and 64-bit PowerShell."
    }
    if (-not $agentRunning) {
        # will Start/Restart below after WireGuard cleanup
    }
}

Write-Host ''
Write-Host '=== Removing stale WireGuard (dev LAN conf) ===' -ForegroundColor Cyan
$wgExe = @(
    "${env:ProgramW6432}\WireGuard\wireguard.exe",
    "${env:ProgramFiles}\WireGuard\wireguard.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (Get-Service -Name $wgService -ErrorAction SilentlyContinue) {
    Stop-Service -Name $wgService -Force -ErrorAction SilentlyContinue
    if ($wgExe) {
        & $wgExe /uninstalltunnelservice 'urs-printer-agent'
        Write-Host '  Uninstalled tunnel service via wireguard.exe'
    }
}

if (Test-Path -LiteralPath $wgConf) {
    Remove-Item -LiteralPath $wgConf -Force
    Write-Host "  Deleted $wgConf"
}

Write-Host ''
Write-Host '=== Restart agent (re-download WireGuard .conf from production backend) ===' -ForegroundColor Cyan
$svcState = Get-Service -Name $agentSvc -ErrorAction SilentlyContinue
if ($svcState) {
    if ($svcState.Status -eq 'Running') {
        Restart-Service -Name $agentSvc -Force
    } else {
        Start-Service -Name $agentSvc
    }
} else {
    Write-Warning "Service $agentSvc not found."
}
Write-Host 'Done. Watch %ProgramData%\URSPrinterAgent\logs\worker.log for:'
Write-Host '  - WireGuard provisioning: wrote config'
Write-Host '  - Redis: opening connection (10.60.0.2:6379'
Write-Host ''
Write-Host 'If wireguard-conf returns HTTP 400, fix PrinterAgent:WireGuard SSH on production API host.'
Write-Host 'If Redis NOAUTH persists, set GitHub secret REDIS_PASSWORD to prod value and rebuild MSI (tag v1.2.8+).'
