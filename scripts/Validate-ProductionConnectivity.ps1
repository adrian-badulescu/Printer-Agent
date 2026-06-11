# Smoke tests for production backend reachability from this machine.
# Does not enroll or print; use E2E_AGENT_DEPLOYMENT_CHECKLIST.md for full pilot.

$ErrorActionPreference = 'Stop'
$prodUrl = 'https://universalrestaurant.systems'
$failures = 0

function Test-Step([string] $Name, [scriptblock] $Action) {
    Write-Host "`n[$Name]" -ForegroundColor Cyan
    try {
        & $Action
        Write-Host '  OK' -ForegroundColor Green
    } catch {
        Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red
        $script:failures++
    }
}

Test-Step 'ping-lite' {
    $r = Invoke-WebRequest -Uri "$prodUrl/api/ping-lite" -UseBasicParsing -TimeoutSec 15
    if ($r.StatusCode -ne 200) { throw "HTTP $($r.StatusCode)" }
}

Test-Step 'enroll endpoint (expect 401 for invalid code)' {
    try {
        Invoke-RestMethod -Uri "$prodUrl/api/agents/enroll" -Method POST -ContentType 'application/json' `
            -Body '{"enrollmentCode":"INVALID00","clientInstanceId":"00000000-0000-0000-0000-000000000001"}'
        throw 'Expected 401'
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 401) { throw $_ }
    }
}

$installJson = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'URSPrinterAgent\agent.json'
if (Test-Path -LiteralPath $installJson) {
    Test-Step 'install-dir BackendUrl' {
        $cfg = Get-Content -LiteralPath $installJson -Raw | ConvertFrom-Json
        Write-Host "  BackendUrl=$($cfg.BackendUrl)"
        Write-Host "  Redis.Host=$($cfg.Redis.Host)"
        if ($cfg.BackendUrl -notmatch 'universalrestaurant\.systems') {
            Write-Warning '  Install-dir agent.json still points at non-prod BackendUrl — reinstall MSI from v1.2.7+ release.'
        }
        if ($cfg.Redis.Host -ne '10.60.0.2') {
            Write-Warning '  Install-dir Redis.Host is not 10.60.0.2 — reinstall MSI from v1.2.7+ release.'
        }
    }
}

if ($failures -gt 0) {
    Write-Host "`n$failures check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "`nProduction connectivity smoke passed." -ForegroundColor Green
