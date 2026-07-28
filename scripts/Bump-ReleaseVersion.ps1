param(
    [Parameter(Mandatory = $true)]
    [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -match '^v(.+)$') { $Version = $Matches[1] }

$wixVersion = if ($Version -match '\.\d+$') { "$Version.0" } else { $Version }

$repoRoot = Split-Path $PSScriptRoot -Parent
$agentJson = Join-Path $repoRoot 'PrinterAgent.Worker\agent.json'
$bundleWxs = Join-Path $repoRoot 'PrinterAgent.Bundle\Bundle.wxs'
$packageWxs = Join-Path $repoRoot 'PrinterAgent.Installer\Package.wxs'

foreach ($path in @($agentJson, $bundleWxs, $packageWxs)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing: $path" }
}

$json = Get-Content -LiteralPath $agentJson -Raw | ConvertFrom-Json
$json.Version = $Version
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($agentJson, ($json | ConvertTo-Json -Depth 20), $utf8NoBom)

function Set-WixVersion([string] $Path, [string] $NewVersion) {
    $content = Get-Content -LiteralPath $Path -Raw
    $updated = [regex]::Replace($content, 'Version="[0-9.]+"', "Version=`"$NewVersion`"", 1)
    if ($updated -eq $content) { throw "Could not update Version= in $Path" }
    [System.IO.File]::WriteAllText($Path, $updated, $utf8NoBom)
}

Set-WixVersion $bundleWxs $wixVersion
Set-WixVersion $packageWxs $wixVersion

$env:GITHUB_REF_NAME = "v$Version"
& (Join-Path $PSScriptRoot 'Assert-ReleaseVersionAlignment.ps1')

Write-Host "Release version bumped to $Version ($wixVersion in WiX)." -ForegroundColor Green
Write-Host 'Next: git add/commit, push main, git tag v{0}, git push origin v{0}' -f $Version -ForegroundColor Cyan
