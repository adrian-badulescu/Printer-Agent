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
Write-Host "agent.json:     $(if (Test-Path $agentJson) { 'OK ' + $agentJson } else { 'MISSING in ProgramData — install-dir agent.json is used; seed ProgramData via MSI or copy template' })"
Write-Host "agent.session:  $(if (Test-Path $sessionJson) { 'OK ' + $sessionJson } else { 'absent (normal until enroll succeeds)' })"

$installAgentJson = $null
$programDataCfg = $null
$installCfg = $null

$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if ($svc) {
    $binLine = (& sc.exe qc $svcName) | Where-Object { $_ -match 'BINARY_PATH_NAME' }
    if ($binLine -match 'BINARY_PATH_NAME\s*:\s*(.+)') {
        $raw = $Matches[1].Trim()
        $exePath = $raw -replace '^"|"$', ''
        Write-Host "Exe resolved: $exePath"
        Write-Host "Exe exists:   $(Test-Path -LiteralPath $exePath)"
        $installAgentJson = Join-Path (Split-Path -LiteralPath $exePath) 'agent.json'
        if ($exePath -match '\\net10\.0\\PrinterAgent\.Worker\.exe' -and $exePath -notmatch 'win-x64') {
            Write-Warning 'This project builds to ...\net10.0\win-x64\ (or publish\). Path above may be wrong; use publish\PrinterAgent.Worker.exe for single-file.'
        }
    }
}

Write-Host "`n=== BackendUrl (effective = install-dir wins if set) ===" -ForegroundColor Cyan
try {
    if (Test-Path $agentJson) {
        $programDataCfg = Get-Content $agentJson -Raw | ConvertFrom-Json
        Write-Host "ProgramData BackendUrl: $($programDataCfg.BackendUrl)"
        if ($programDataCfg.Printers) {
            $ids = @($programDataCfg.Printers | ForEach-Object { $_.id })
            Write-Host "ProgramData printer ids: [$($ids -join ', ')]"
        }
    }
    if ($installAgentJson -and (Test-Path -LiteralPath $installAgentJson)) {
        $installCfg = Get-Content -LiteralPath $installAgentJson -Raw | ConvertFrom-Json
        Write-Host "Install-dir BackendUrl: $($installCfg.BackendUrl)  ($installAgentJson)"
        if ($installCfg.BackendUrl -and $programDataCfg.BackendUrl -and
            $installCfg.BackendUrl -ne $programDataCfg.BackendUrl) {
            Write-Warning 'BackendUrl differs between install-dir and ProgramData. The worker uses install-dir BackendUrl when non-empty (BundledFirstKeys). Edit C:\Program Files\URSPrinterAgent\agent.json or clear BackendUrl there so ProgramData applies.'
        }
    }
} catch {
    Write-Warning "Could not parse agent.json: $_"
}

Write-Host "`n=== Session / agent identity ===" -ForegroundColor Cyan
$clientInstance = Join-Path $dataDir 'client.instance'
if (Test-Path $clientInstance) {
    Write-Host "client.instance: $((Get-Content $clientInstance -Raw).Trim())"
}
if (Test-Path $sessionJson) {
    try {
        $session = Get-Content $sessionJson -Raw | ConvertFrom-Json
        Write-Host "agentId:       $($session.agentId)"
        Write-Host "restaurantId:  $($session.restaurantId)"
        Write-Host "expiresAtUtc:  $($session.expiresAtUtc)"
    } catch {
        Write-Warning "Could not parse agent.session.json: $_"
    }
}

Write-Host "`n=== worker.log (printer / enroll / print failures) ===" -ForegroundColor Cyan
$workerLog = Join-Path $dataDir 'logs\worker.log'
if (Test-Path $workerLog) {
    $tail = Get-Content $workerLog -Tail 80
    $tail | Select-String -Pattern 'Enrollment|Heartbeat|Print job|printerId|BackendUrl|Printers loaded|no printer with Id|401|429|WireGuard' |
        Select-Object -Last 25 |
        ForEach-Object { Write-Host $_.Line }

    $requestedIds = @($tail | Select-String -Pattern 'requested printerId=([^\s.]+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -Unique)
    $configuredLine = $tail | Select-String -Pattern 'Configured printer ids: \[(.*?)\]' | Select-Object -Last 1
    if ($requestedIds.Count -gt 0 -and $configuredLine) {
        $configuredIds = $configuredLine.Matches[0].Groups[1].Value
        foreach ($req in $requestedIds) {
            if ($configuredIds -notmatch [regex]::Escape($req)) {
                Write-Host ''
                Write-Warning "PRINTER ID MISMATCH: backend jobs use printerId=$req but agent.json has [$configuredIds]."
                Write-Host 'Fix: Manager Settings -> select the agent printer and Save (updates Restaurants.DefaultBillPrinterId), OR rename the printer id in Configurator to match the backend.' -ForegroundColor Yellow
            }
        }
    }
} else {
    Write-Host "No worker.log at $workerLog"
}

Write-Host "`n=== DB fields (what Manager UI reads) ===" -ForegroundColor Cyan
Write-Host @'
  PrinterAgentHeartbeats.PrintersJson  -> dropdown list (updated only on agent heartbeat)
  Restaurants.DefaultBillPrinterId     -> which printerId is sent on bill print jobs (Manager Save)
  Re-enroll / revoke code does NOT change either field automatically.
'@

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
