$ErrorActionPreference = "SilentlyContinue"

$logPath = Join-Path (Get-Location) "debug-00a8ae.log"

function Write-Ndjson {
  param(
    [Parameter(Mandatory=$true)][string]$hypothesisId,
    [Parameter(Mandatory=$true)][string]$message,
    [Parameter(Mandatory=$false)][hashtable]$data
  )

  $obj = @{
    sessionId    = "00a8ae"
    runId        = "repro"
    hypothesisId = $hypothesisId
    location     = "scripts/Collect-InstallerEvidence.ps1"
    message      = $message
    data         = $data
    timestamp    = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
  }

  $line = ($obj | ConvertTo-Json -Compress)
  Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

Write-Ndjson -hypothesisId "H0" -message "collect evidence start" -data @{ cwd = (Get-Location).Path; user = $env:USERNAME }

$installDir = "C:\Program Files\URSPrinterAgent"
$configExe = Join-Path $installDir "PrinterAgent.Configurator.exe"
Write-Ndjson -hypothesisId "H3" -message "check installed configurator exe" -data @{ path = $configExe; exists = (Test-Path -LiteralPath $configExe) }

$startMenuCommon = "C:\ProgramData\Microsoft\Windows\Start Menu\Programs"
$startMenuUser = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"

$expectedLinkNames = @(
  "Configure URS Printer Agent.lnk",
  "URS Printer Agent Configurator.lnk"
)

foreach ($root in @($startMenuCommon, $startMenuUser)) {
  foreach ($name in $expectedLinkNames) {
    $p = Join-Path $root $name
    Write-Ndjson -hypothesisId "H3" -message "check direct start menu link" -data @{ root = $root; link = $name; path = $p; exists = (Test-Path -LiteralPath $p) }
  }
}

# Broader check: search a few levels deep for anything containing "URS" and "Configurator"
foreach ($root in @($startMenuCommon, $startMenuUser)) {
  $matches = Get-ChildItem -LiteralPath $root -Recurse -Depth 4 -Filter "*.lnk" |
    Where-Object { $_.Name -match "URS" -or $_.Name -match "Configurator" } |
    Select-Object -First 30 -ExpandProperty FullName
  Write-Ndjson -hypothesisId "H3" -message "start menu scan (limited)" -data @{ root = $root; sample = $matches }
}

Write-Ndjson -hypothesisId "H0" -message "collect evidence end" -data @{ }

