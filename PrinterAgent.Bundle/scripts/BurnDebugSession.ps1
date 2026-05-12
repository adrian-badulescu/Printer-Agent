#requires -Version 5.1
<#
 Captures WiX Burn runtime evidence: runs the bundle with /log, then parses the log into NDJSON for debug-f84d7c analysis.
 Usage: .\BurnDebugSession.ps1 -BundlePath "C:\path\URSPrinterAgentSetup.exe"
#>
param(
    [Parameter(Mandatory = $true)][string]$BundlePath,
    [string]$NdjsonLogPath = 'C:\W\QRFE\debug-f84d7c.log',
    [string]$RunId = 'pre',
    [int]$MaxWaitMs = 120000
)

#region agent log
function Write-AgentNdjson([hashtable]$payload) {
    $payload['timestamp'] = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $payload['sessionId'] = 'f84d7c'
    $payload['runId'] = $RunId
    $line = ($payload | ConvertTo-Json -Compress -Depth 8)
    Add-Content -LiteralPath $NdjsonLogPath -Value $line -Encoding utf8
}
#endregion

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $BundlePath)) {
    throw "BundlePath not found: $BundlePath"
}

$burnLog = Join-Path $env:TEMP ("urs-burn-{0}.log" -f [Guid]::NewGuid().ToString('n'))
$fullBundle = (Resolve-Path -LiteralPath $BundlePath).Path

#region agent log
$sha = (Get-FileHash -LiteralPath $fullBundle -Algorithm SHA256).Hash
$len = (Get-Item -LiteralPath $fullBundle).Length
Write-AgentNdjson @{
    hypothesisId = 'H1'
    location       = 'BurnDebugSession.ps1:bundle-meta'
    message        = 'Bundle file identity (detect stale/wrong binary)'
    data           = @{ path = $fullBundle; sha256 = $sha; lengthBytes = $len }
}
#endregion

$p = Start-Process -FilePath $fullBundle -ArgumentList @('/log', $burnLog) -PassThru
$null = $p.WaitForExit($MaxWaitMs)
if (-not $p.HasExited) {
    #region agent log
    Write-AgentNdjson @{
        hypothesisId = 'H0'
        location       = 'BurnDebugSession.ps1:timeout'
        message        = 'Burn still running after MaxWaitMs; terminating for log capture'
        data           = @{ maxWaitMs = $MaxWaitMs; pid = $p.Id }
    }
    #endregion
    try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch {}
    $exit = -1
} else {
    $exit = $p.ExitCode
}

#region agent log
Write-AgentNdjson @{
    hypothesisId = 'H0'
    location       = 'BurnDebugSession.ps1:exit'
    message        = 'Burn process exit'
    data           = @{ exitCode = $exit; burnLogPath = $burnLog }
}
#endregion

if (-not (Test-Path -LiteralPath $burnLog)) {
    #region agent log
    Write-AgentNdjson @{
        hypothesisId = 'H2'
        location       = 'BurnDebugSession.ps1:no-burn-log'
        message        = 'Burn did not create log file (launch blocked or wrong exe?)'
        data           = @{ expected = $burnLog }
    }
    #endregion
    exit $exit
}

$raw = Get-Content -LiteralPath $burnLog -Raw -ErrorAction SilentlyContinue
if (-not $raw) { $raw = '' }

$patterns = @(
    @{ id = 'H2'; re = '(?i)(WireGuardInstallerExe|wireguard-installer|ExePackage|Failed to (?:acquire|resolve).*payload|payload.*failed)' }
    @{ id = 'H3'; re = '(?i)(PrinterAgent\.msi|cab\d\.cab|UrsPrinterAgentMsi|MSI|source.*media|sfx|1632|1610|Could not find|missing.*file)' }
    @{ id = 'H4'; re = '(?i)(access is denied|cannot access|locked|0x80070005|antivirus|SmartScreen)' }
    @{ id = 'H5'; re = '(?i)(thm\.xml|thm\.wxl|BootstrapperApplication|LicenseUrl|Payload.*failed|Failed to load)' }
)

foreach ($patt in $patterns) {
    $m = [regex]::Matches($raw, $patt.re)
    if ($m.Count -gt 0) {
        $sample = @()
        foreach ($x in $m | Select-Object -First 8) { $sample += $x.Value.Trim() }
        #region agent log
        Write-AgentNdjson @{
            hypothesisId = $patt.id
            location       = 'BurnDebugSession.ps1:regex-scan'
            message        = 'Burn log pattern hits'
            data           = @{ pattern = $patt.re; hitCount = $m.Count; samples = $sample }
        }
        #endregion
    }
}

# Last 40 lines often contain the actionable error
$tail = (Get-Content -LiteralPath $burnLog -Tail 40 -ErrorAction SilentlyContinue) -join "`n"
#region agent log
Write-AgentNdjson @{
    hypothesisId = 'H0'
    location       = 'BurnDebugSession.ps1:burn-log-tail'
    message        = 'Burn log tail (verbatim)'
    data           = @{ tail = $tail }
}
#endregion

Write-Host "Burn log: $burnLog"
Write-Host "NDJSON:   $NdjsonLogPath"
exit $exit
