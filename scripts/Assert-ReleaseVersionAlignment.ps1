param(
    [string] $AgentJsonPath = (Join-Path $PSScriptRoot '..\PrinterAgent.Worker\agent.json'),
    [string] $BundleWxsPath = (Join-Path $PSScriptRoot '..\PrinterAgent.Bundle\Bundle.wxs'),
    [string] $PackageWxsPath = (Join-Path $PSScriptRoot '..\PrinterAgent.Installer\Package.wxs'),
    [string] $TagVersion = $env:GITHUB_REF_NAME,
    [switch] $RequireTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# GITHUB_REF_NAME is "main" on branch pushes — only compare to tag when it looks like vX.Y.Z
if ($TagVersion -match '^v(\d+\.\d+\.\d+.*)$') {
    $TagVersion = $Matches[1]
}
elseif ($TagVersion -match '^\d+\.\d+\.\d+') {
    # explicit -TagVersion 1.5.9
}
else {
    $TagVersion = ''
}

$agentVersion = (Get-Content -LiteralPath $AgentJsonPath -Raw | ConvertFrom-Json).Version.Trim()
$bundleVersion = ([regex]::Match((Get-Content -LiteralPath $BundleWxsPath -Raw), 'Version="([0-9.]+)"')).Groups[1].Value
$packageVersion = ([regex]::Match((Get-Content -LiteralPath $PackageWxsPath -Raw), 'Version="([0-9.]+)"')).Groups[1].Value

function Normalize-WixVersion([string] $Version) {
    $parts = $Version.Split('.')
    while ($parts.Count -lt 4) { $parts += '0' }
    return ($parts[0..3] -join '.')
}

$agentWix = Normalize-WixVersion $agentVersion
$errors = @()

if ($bundleVersion -ne $agentWix) {
    $errors += "Bundle.wxs Version $bundleVersion != agent.json Version $agentVersion ($agentWix)"
}
if ($packageVersion -ne $agentWix) {
    $errors += "Package.wxs Version $packageVersion != agent.json Version $agentVersion ($agentWix)"
}

if (-not [string]::IsNullOrWhiteSpace($TagVersion)) {
    $tagWix = Normalize-WixVersion $TagVersion
    if ($tagWix -ne $agentWix) { $errors += "Git tag v$TagVersion ($tagWix) != agent.json Version $agentVersion ($agentWix)" }
    if ($bundleVersion -ne $tagWix) { $errors += "Bundle.wxs Version $bundleVersion != tag $tagWix" }
    if ($packageVersion -ne $tagWix) { $errors += "Package.wxs Version $packageVersion != tag $tagWix" }
}
elseif ($RequireTag) {
    throw 'Tag version required (set GITHUB_REF_NAME or -TagVersion).'
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    throw 'Release version alignment failed. Run: .\scripts\Bump-ReleaseVersion.ps1 -Version X.Y.Z'
}

if ([string]::IsNullOrWhiteSpace($TagVersion)) {
    Write-Host "Release files aligned: agent=$agentVersion bundle=$bundleVersion package=$packageVersion"
}
else {
    Write-Host "Release version alignment OK: tag=$TagVersion agent=$agentVersion bundle=$bundleVersion package=$packageVersion"
}
