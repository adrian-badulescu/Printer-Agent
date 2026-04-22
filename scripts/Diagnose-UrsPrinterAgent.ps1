# Quick checks for URSPrinterAgent: service, binary path, config, recent errors.
# Run in PowerShell (admin optional; some queries work without).

$ErrorActionPreference = 'Continue'
$svcName = 'URSPrinterAgent'
$dataDir = Join-Path $env:ProgramData 'URSPrinterAgent'
$agentJson = Join-Path $dataDir 'agent.json'
$sessionJson = Join-Path $dataDir 'agent.session.json'

Write-Host '=== Service ===' -ForegroundColor Cyan
Get-Service -Name $svcName -ErrorAction SilentlyContinue | Format-List Name, Status, StartType

Write-Host '=== SCM binary path (sc qc) ===' -ForegroundColor Cyan
& sc.exe qc $svcName 2>$null

Write-Host '=== Config files ===' -ForegroundColor Cyan
Write-Host "agent.json:     $(if (Test-Path $agentJson) { 'OK ' + $agentJson } else { 'MISSING - host will fail at startup (required, optional:false)' })"
Write-Host "agent.session:  $(if (Test-Path $sessionJson) { 'OK ' + $sessionJson } else { 'absent (normal until enroll succeeds)' })"

$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if ($svc) {
    $binLine = (& sc.exe qc $svcName) | Where-Object { $_ -match 'BINARY_PATH_NAME' }
    if ($binLine -match 'BINARY_PATH_NAME\s*:\s*(.+)') {
        $raw = $Matches[1].Trim()
        $exePath = $raw -replace '^"|"$', ''
        Write-Host "Exe resolved: $exePath"
        Write-Host "Exe exists:   $(Test-Path -LiteralPath $exePath)"
        if ($exePath -match '\\net10\.0\\PrinterAgent\.Worker\.exe' -and $exePath -notmatch 'win-x64') {
            Write-Warning 'This project builds to ...\net10.0\win-x64\ (or publish\). Path above may be wrong; use publish\PrinterAgent.Worker.exe for single-file.'
        }
    }
}

Write-Host "`n=== Application log (last 40 errors, Printer / .NET / Service) ===" -ForegroundColor Cyan
try {
    Get-WinEvent -LogName Application -MaxEvents 400 -ErrorAction Stop |
        Where-Object {
            $_.LevelDisplayName -eq 'Error' -and (
                $_.ProviderName -match 'PrinterAgent|\.NET Runtime|Application Error|Windows Error Reporting' -or
                $_.Message -match 'PrinterAgent|URSPrinterAgent'
            )
        } |
        Select-Object -First 40 |
        Format-Table TimeCreated, ProviderName, Id -AutoSize
} catch {
    Write-Warning "Could not read Application log: $_"
}

Write-Host '=== System log (Service Control Manager, URSPrinterAgent) ===' -ForegroundColor Cyan
try {
    Get-WinEvent -LogName System -MaxEvents 200 -ErrorAction Stop |
        Where-Object {
            $_.ProviderName -eq 'Service Control Manager' -and $_.Message -match 'URSPrinterAgent'
        } |
        Select-Object -First 10 |
        Format-Table TimeCreated, Id -AutoSize
} catch {
    Write-Warning "Could not read System log: $_"
}

Write-Host "`nTip: run the worker once as console to see the first exception:" -ForegroundColor Yellow
Write-Host "  cd `"$dataDir`""
Write-Host "  & `"C:\path\to\PrinterAgent.Worker.exe`""
