# GET /api/agents/{agentId}/wireguard-conf using URSPrinterAgent session.
# Run PowerShell as Administrator.

$ErrorActionPreference = 'Stop'
$dataDir = Join-Path $env:ProgramData 'URSPrinterAgent'
$sessionPath = Join-Path $dataDir 'agent.session.json'
$agentJsonPath = Join-Path $dataDir 'agent.json'

if (-not (Test-Path -LiteralPath $sessionPath)) {
    Write-Error ('Missing {0}. Enroll via Configurator and URSPrinterAgent service.' -f $sessionPath)
}

$session = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
$agentId = [string]$session.agentId
if ([string]::IsNullOrWhiteSpace($agentId)) {
    Write-Error 'agentId missing in session.'
}

$token = [string]$session.accessToken
if ([string]::IsNullOrWhiteSpace($token) -and $session.accessTokenProtected) {
    Add-Type -AssemblyName System.Security
    $encBytes = [Convert]::FromBase64String([string]$session.accessTokenProtected)
    $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $encBytes,
        $null,
        [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
    $token = [Text.Encoding]::UTF8.GetString($plain)
}
if ([string]::IsNullOrWhiteSpace($token)) {
    Write-Error 'No access token. Restart-Service URSPrinterAgent or re-enroll.'
}

$backendUrl = 'http://192.168.43.142'
if (Test-Path -LiteralPath $agentJsonPath) {
    $cfg = Get-Content -LiteralPath $agentJsonPath -Raw | ConvertFrom-Json
    if ($cfg.BackendUrl) {
        $backendUrl = ([string]$cfg.BackendUrl).Trim().TrimEnd('/')
    }
}

$uri = '{0}/api/agents/{1}/wireguard-conf' -f $backendUrl, $agentId
Write-Host ('GET {0}' -f $uri) -ForegroundColor Cyan
Write-Host ('agentId: {0}' -f $agentId)
Write-Host ('session expiresAtUtc: {0}' -f $session.expiresAtUtc)

try {
    $resp = Invoke-WebRequest -Uri $uri -Headers @{ Authorization = ('Bearer ' + $token) } -UseBasicParsing
    Write-Host ('HTTP {0} OK' -f $resp.StatusCode) -ForegroundColor Green
    $out = Join-Path $dataDir 'wireguard\urs-printer-agent.conf'
    $outDir = Split-Path -Parent $out
    if (-not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    }
    Set-Content -LiteralPath $out -Value $resp.Content -Encoding UTF8
    $byteCount = ([string]$resp.Content).Length
    Write-Host ('Wrote {0} ({1} bytes). Next: Repair-UrsPrinterAgentWireGuard.ps1' -f $out, $byteCount)
}
catch {
    $ex = $_.Exception
    $webResp = $ex.Response
    if ($null -ne $webResp) {
        $code = [int]$webResp.StatusCode
        $stream = $webResp.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($stream)
        $body = $reader.ReadToEnd()
        $reader.Close()
        Write-Host ('HTTP {0}' -f $code) -ForegroundColor Red
        Write-Host $body
        if ($code -eq 401) {
            Write-Host 'Hint: token expired. Restart-Service URSPrinterAgent or re-enroll.' -ForegroundColor Yellow
        }
        elseif ($code -eq 403) {
            Write-Host 'Hint: agentId mismatch. Re-enroll with new code.' -ForegroundColor Yellow
        }
        elseif ($code -eq 400) {
            Write-Host 'Hint: see body above. Check API journal for wireguard.' -ForegroundColor Yellow
        }
    }
    else {
        Write-Host $ex.Message -ForegroundColor Red
    }
    exit 1
}
