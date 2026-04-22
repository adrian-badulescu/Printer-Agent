# Creates %ProgramData%\URSPrinterAgent and grants modify rights to common service accounts.
# Run elevated (Administrator) once per machine before first agent start.
# Optional: -CopyTemplateFrom "path\to\agent.json"

param(
    [string] $CopyTemplateFrom = ""
)

$ErrorActionPreference = 'Stop'
$dataDir = Join-Path $env:ProgramData 'URSPrinterAgent'

if (-not (Test-Path $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir | Out-Null
    Write-Host "Created $dataDir"
} else {
    Write-Host "Exists $dataDir"
}

# Interactive Configurator + typical service identities
$accounts = @(
    'BUILTIN\Users',
    'NT SERVICE\LOCAL SERVICE',
    'NT AUTHORITY\LOCAL SERVICE',
    'NT AUTHORITY\NETWORK SERVICE'
)

foreach ($acct in $accounts) {
    try {
        icacls $dataDir /grant:r "${acct}:(OI)(CI)M" /T | Out-Null
        Write-Host "Granted Modify to $acct"
    } catch {
        Write-Warning "Could not grant to ${acct}: $_"
    }
}

if ($CopyTemplateFrom -and (Test-Path $CopyTemplateFrom)) {
    $dest = Join-Path $dataDir 'agent.json'
    if (-not (Test-Path $dest)) {
        Copy-Item $CopyTemplateFrom $dest
        Write-Host "Copied template to $dest"
    } else {
        Write-Host "Skip copy: $dest already exists"
    }
}

$destAgent = Join-Path $dataDir 'agent.json'
if (-not (Test-Path $destAgent)) {
    $hint = 'Run: .\Setup-ProgramData.ps1 -CopyTemplateFrom "..\PrinterAgent.Worker\agent.json"'
    Write-Warning "Still missing: $destAgent. $hint"
} else {
    Write-Host "agent.json present: $destAgent"
}
Write-Host ('Done. Edit agent.json in: {0}' -f $dataDir)
