# Post-install checks: MSI (Program Files), Configurator, ProgramData, service.
# Does not replace manual wizard testing or print E2E.
#
# Examples:
#   .\Verify-UrsPrinterAgentInstall.ps1
#   .\Verify-UrsPrinterAgentInstall.ps1 -Strict
#   .\Verify-UrsPrinterAgentInstall.ps1 -RepoRoot ..
#   .\Verify-UrsPrinterAgentInstall.ps1 -ExpectServiceRunning

[CmdletBinding()]
param(
    [string] $InstallFolder = (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'URSPrinterAgent'),
    [string] $ProgramDataFolder = (Join-Path $env:ProgramData 'URSPrinterAgent'),
    [string] $RepoRoot = '',
    [switch] $Strict,
    [switch] $ExpectServiceRunning
)

$ErrorActionPreference = 'Stop'
$issues = [System.Collections.Generic.List[string]]::new()
$svcName = 'URSPrinterAgent'

function Add-Issue([string] $msg) {
    $script:issues.Add($msg) | Out-Null
    Write-Warning $msg
}

Write-Host '=== URS Printer Agent - install verification ===' -ForegroundColor Cyan

$workerExe = Join-Path $InstallFolder 'PrinterAgent.Worker.exe'
$configExe = Join-Path $InstallFolder 'PrinterAgent.Configurator.exe'
$shortcut = Join-Path $InstallFolder 'Configure URS Printer Agent.lnk'

Write-Host "`n[Program Files] $InstallFolder" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $InstallFolder)) {
    Add-Issue "Install folder missing: $InstallFolder (run MSI or Install-UrsPrinterAgent.ps1)."
} else {
    Write-Host '  OK folder exists'
    if (Test-Path -LiteralPath $workerExe) { Write-Host '  OK PrinterAgent.Worker.exe' }
    else { Add-Issue "Missing PrinterAgent.Worker.exe under $InstallFolder" }
    if (Test-Path -LiteralPath $configExe) { Write-Host '  OK PrinterAgent.Configurator.exe' }
    else { Add-Issue "Missing PrinterAgent.Configurator.exe under $InstallFolder" }
    if (Test-Path -LiteralPath $shortcut) { Write-Host "  OK shortcut: $(Split-Path -Leaf $shortcut)" }
    else { Add-Issue 'Missing Configurator shortcut (expected: Configure URS Printer Agent.lnk next to EXE).' }
}

$agentJson = Join-Path $ProgramDataFolder 'agent.json'
Write-Host "`n[ProgramData] $ProgramDataFolder" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $ProgramDataFolder)) {
    Add-Issue "Missing $ProgramDataFolder - run Setup-ProgramData.ps1 or let service create it."
} elseif (-not (Test-Path -LiteralPath $agentJson)) {
    Add-Issue 'Missing agent.json in ProgramData - copy template or run Configurator.'
} else {
    Write-Host '  OK agent.json'
    try {
        $cfg = Get-Content -LiteralPath $agentJson -Raw | ConvertFrom-Json
        $sessionPath = Join-Path $ProgramDataFolder 'agent.session.json'
        if (-not $cfg.EnrollmentCode -and -not (Test-Path -LiteralPath $sessionPath)) {
            Add-Issue 'EnrollmentCode empty and no agent.session.json - enroll cannot succeed yet.'
        } elseif ($cfg.EnrollmentCode) {
            Write-Host '  OK EnrollmentCode set (provisioning)'
        } else {
            Write-Host '  EnrollmentCode empty - OK if session exists (refresh).'
        }
        if (-not $cfg.Printers -or $cfg.Printers.Count -lt 1) {
            Add-Issue 'Printers[] empty - add printer (Configurator or manual edit).'
        } else {
            Write-Host "  OK Printers count: $($cfg.Printers.Count)"
        }
    } catch {
        Add-Issue "agent.json is not valid JSON: $_"
    }
}

Write-Host "`n[Service] $svcName" -ForegroundColor Cyan
$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if (-not $svc) {
    Add-Issue "Service $svcName not registered (install MSI or Install-UrsPrinterAgent.ps1)."
} else {
    Write-Host "  Status: $($svc.Status); StartType: $($svc.StartType)"
    if ($ExpectServiceRunning -and $svc.Status -ne 'Running') {
        Add-Issue "Expected Running, but status is $($svc.Status)."
    }
}

if ($RepoRoot) {
    $msiDir = Join-Path $RepoRoot 'PrinterAgent.Installer\bin\Release'
    Write-Host "`n[Build artifacts] $msiDir" -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath $msiDir)) {
        Add-Issue "MSI output folder missing: $msiDir (dotnet build PrinterAgent.Installer\PrinterAgent.Installer.wixproj -c Release)."
    } else {
        $msi = Join-Path $msiDir 'PrinterAgent.msi'
        $cab1 = Join-Path $msiDir 'cab1.cab'
        $cab2 = Join-Path $msiDir 'cab2.cab'
        foreach ($f in @($msi, $cab1, $cab2)) {
            if (Test-Path -LiteralPath $f) { Write-Host "  OK $(Split-Path -Leaf $f)" }
            else { Add-Issue "Missing $(Split-Path -Leaf $f) - build installer project." }
        }
    }
}

Write-Host ''
if ($issues.Count -eq 0) {
    Write-Host 'Summary: no issues detected.' -ForegroundColor Green
    exit 0
}

Write-Host ('Summary: {0} issue(s).' -f $issues.Count) -ForegroundColor Yellow
if ($Strict) { exit 1 }
exit 0
