# Debug-AutoUpdate.ps1 v2.1 - auto-update diagnostics (NDJSON to debug-25e5dc.log)
# Run as Administrator when possible.

param(
    [string] $DebugLogPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'debug-25e5dc.log'),
    [string] $SessionId = '25e5dc',
    [string] $RunId = 'diag1'
)

$ErrorActionPreference = 'Stop'
Write-Host 'Debug-AutoUpdate.ps1 v2.1' -ForegroundColor DarkGray

function Write-DebugNdjson(
    [string] $HypothesisId,
    [string] $Location,
    [string] $Message,
    [hashtable] $Data
) {
    $entry = [ordered]@{
        sessionId    = $SessionId
        runId        = $RunId
        hypothesisId = $HypothesisId
        location     = $Location
        message      = $Message
        data         = $Data
        timestamp    = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    }
    $line = ($entry | ConvertTo-Json -Compress -Depth 6)
    Add-Content -LiteralPath $DebugLogPath -Value $line -Encoding utf8
}

function Get-JsonProp {
    param($Object, [string] $Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $Default }
    $value = $prop.Value
    if ($null -eq $value -or ($value -is [string] -and [string]::IsNullOrWhiteSpace($value))) { return $Default }
    return $value
}

function Read-AgentJson([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return ($raw | ConvertFrom-Json)
    }
    catch {
        Write-Warning "Could not parse JSON: $Path - $($_.Exception.Message)"
        return $null
    }
}

function Get-InstallRoot {
    $candidates = @(
        ${env:ProgramW6432},
        ${env:ProgramFiles},
        'C:\Program Files'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    foreach ($root in $candidates) {
        $dir = Join-Path $root 'URSPrinterAgent'
        if (Test-Path -LiteralPath (Join-Path $dir 'agent.json')) { return $dir }
    }
    return Join-Path (${env:ProgramFiles}) 'URSPrinterAgent'
}

$installRoot = Get-InstallRoot
$installJson = Join-Path $installRoot 'agent.json'
$programDataJson = Join-Path $env:ProgramData 'URSPrinterAgent\agent.json'
$workerLog = Join-Path $env:ProgramData 'URSPrinterAgent\logs\worker.log'
$updatesDir = Join-Path $env:ProgramData 'URSPrinterAgent\updates'
$manifestUrl = 'https://github.com/adrian-badulescu/Printer-Agent/releases/latest/download/release-manifest.json'

# H-A
$install = Read-AgentJson $installJson
$installVersion = [string](Get-JsonProp $install 'Version' 'unknown')
$installManifestUrl = [string](Get-JsonProp $install 'UpdateManifestUrl' '')
$installSecret = [string](Get-JsonProp $install 'UpdateSignatureSecret' '')

Write-DebugNdjson -HypothesisId 'H-A' -Location 'Debug-AutoUpdate.ps1:install' -Message 'Install-dir config' -Data @{
    installRoot     = $installRoot
    installJsonPath = $installJson
    exists          = (Test-Path -LiteralPath $installJson)
    jsonParsed      = ($null -ne $install)
    version         = $installVersion
    hasManifestUrl  = (-not [string]::IsNullOrWhiteSpace($installManifestUrl))
    manifestUrl     = $installManifestUrl
    secretLength    = $(if ($installSecret) { $installSecret.Length } else { 0 })
}

# H-B
$pd = Read-AgentJson $programDataJson
$pdVersion = [string](Get-JsonProp $pd 'Version' 'unknown')
$pdManifestUrl = [string](Get-JsonProp $pd 'UpdateManifestUrl' '')
$pdSecret = [string](Get-JsonProp $pd 'UpdateSignatureSecret' '')

Write-DebugNdjson -HypothesisId 'H-B' -Location 'Debug-AutoUpdate.ps1:programdata' -Message 'ProgramData config' -Data @{
    exists       = (Test-Path -LiteralPath $programDataJson)
    jsonParsed   = ($null -ne $pd)
    version      = $pdVersion
    hasManifest  = (-not [string]::IsNullOrWhiteSpace($pdManifestUrl))
    secretLength = $(if ($pdSecret) { $pdSecret.Length } else { 0 })
}

# H-C
$workerExe = Join-Path $installRoot 'PrinterAgent.Worker.exe'
$infraDll = Join-Path $installRoot 'PrinterAgent.Infrastructure.dll'
$lastLogLines = @()
if (Test-Path -LiteralPath $workerLog) {
    $lastLogLines = @(Get-Content -LiteralPath $workerLog -Tail 80 -ErrorAction SilentlyContinue)
}
$hasOldExitMsg = ($lastLogLines | Where-Object { $_ -match 'Starting installer and exiting\.' }).Count -gt 0
$hasNewDelayMsg = ($lastLogLines | Where-Object { $_ -match 'Scheduling silent installer' }).Count -gt 0

Write-DebugNdjson -HypothesisId 'H-C' -Location 'Debug-AutoUpdate.ps1:binary' -Message 'Installed binary age' -Data @{
    workerExists      = (Test-Path -LiteralPath $workerExe)
    workerLastWrite   = $(if (Test-Path -LiteralPath $workerExe) { (Get-Item -LiteralPath $workerExe).LastWriteTimeUtc.ToString('o') } else { $null })
    infraLastWrite    = $(if (Test-Path -LiteralPath $infraDll) { (Get-Item -LiteralPath $infraDll).LastWriteTimeUtc.ToString('o') } else { $null })
    logHasOldExitMsg  = $hasOldExitMsg
    logHasNewDelayMsg = $hasNewDelayMsg
    reportedVersion   = $installVersion
}

# H-D
$manifest = Invoke-RestMethod -Uri $manifestUrl -ErrorAction Stop
function Get-Hmac([string] $Secret, [string] $Version, [string] $Url, [string] $Sha) {
    $payload = '{0}|{1}|{2}' -f $Version, $Url, $Sha
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Secret))
    try { return [BitConverter]::ToString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))).Replace('-', '') }
    finally { $hmac.Dispose() }
}
$sigMatch = $false
if (-not [string]::IsNullOrWhiteSpace($installSecret)) {
    $expected = Get-Hmac $installSecret.Trim() $manifest.version $manifest.downloadUrl $manifest.sha256
    $sigMatch = ($expected -eq $manifest.signature)
}
try { $localVer = [version]$installVersion } catch { $localVer = [version]'0.0.0' }

Write-DebugNdjson -HypothesisId 'H-D' -Location 'Debug-AutoUpdate.ps1:manifest' -Message 'Remote manifest vs local secret' -Data @{
    remoteVersion = [string]$manifest.version
    localVersion  = $installVersion
    isNewer       = ([version]$manifest.version -gt $localVer)
    signatureOk   = $sigMatch
}

# H-E
$svc = Get-Service -Name 'URSPrinterAgent' -ErrorAction SilentlyContinue
$updateLogs = Get-ChildItem -Path $env:TEMP -Filter 'urs-agent-update.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

Write-DebugNdjson -HypothesisId 'H-E' -Location 'Debug-AutoUpdate.ps1:service' -Message 'Service and installer state' -Data @{
    serviceExists    = ($null -ne $svc)
    serviceStatus    = $(if ($svc) { $svc.Status.ToString() } else { 'MISSING_1060' })
    updateLogExists  = ($null -ne $updateLogs)
    updateLogPath    = $(if ($updateLogs) { $updateLogs.FullName } else { $null })
    updateLogTail    = $(if ($updateLogs) { @(Get-Content -LiteralPath $updateLogs.FullName -Tail 5) } else { @() })
    cachedInstallers = @(
        Get-ChildItem -Path $updatesDir -Filter 'PrinterAgent_Update_*.exe' -ErrorAction SilentlyContinue |
            ForEach-Object { @{ name = $_.Name; bytes = $_.Length; mtime = $_.LastWriteTimeUtc.ToString('o') } }
    )
    inProgressLock = (Test-Path -LiteralPath (Join-Path $updatesDir '.update-in-progress'))
}

Write-Host "Diagnostics written to $DebugLogPath" -ForegroundColor Green
Write-Host "Install root: $installRoot" -ForegroundColor Cyan
Write-Host "Service: $(if ($svc) { $svc.Status } else { 'MISSING (1060)' })" -ForegroundColor Cyan
Write-Host ('Local version: {0} | Remote: {1} | Signature OK: {2}' -f $installVersion, $manifest.version, $sigMatch) -ForegroundColor Cyan
