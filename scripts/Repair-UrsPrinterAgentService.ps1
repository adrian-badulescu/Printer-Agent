# Repairs URSPrinterAgent after a failed setup (service DISABLED or orphaned registration).
# Run elevated. Prefer a clean reinstall of URSPrinterAgentSetup.exe if files under Program Files are missing.
#
# Usage: .\Repair-UrsPrinterAgentService.ps1

$ErrorActionPreference = 'Stop'
$ServiceName = 'URSPrinterAgent'
$InstallDir = Join-Path ${env:ProgramFiles} 'URSPrinterAgent'
$ExePath = Join-Path $InstallDir 'PrinterAgent.Worker.exe'

Write-Host "Install dir: $InstallDir"
if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Error @"
Worker binary missing: $ExePath
Run URSPrinterAgentSetup.exe again (or uninstall from Settings -> Apps, then reinstall).
"@
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq 'Running') {
        Stop-Service -Name $ServiceName -Force
    }
    & sc.exe config $ServiceName start= auto
    Write-Host "Set $ServiceName start type to automatic."
} else {
    Write-Warning "Service $ServiceName not registered. Reinstall the MSI/setup."
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
