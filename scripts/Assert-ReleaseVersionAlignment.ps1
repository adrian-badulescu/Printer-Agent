param(
    [string] $AgentJsonPath = (Join-Path $PSScriptRoot '..\PrinterAgent.Worker\agent.json'),
    [string] $BundleWxsPath = (Join-Path $PSScriptRoot '..\PrinterAgent.Bundle\Bundle.wxs'),
    [string] $PackageWxsPath = (Join-Path $PSScriptRoot '..\PrinterAgent.Installer\Package.wxs'),
    [string] $TagVersion = $env:GITHUB_REF_NAME
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($TagVersion -match '^v(.+)$') {
    $TagVersion = $Matches[1]
}

if ([string]::IsNullOrWhiteSpace($TagVersion)) {
    Write-Host 'No tag version (GITHUB_REF_NAME); skipping WiX alignment check.'
    exit 0
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
$tagWix = Normalize-WixVersion $TagVersion

$errors = @()
if ($tagWix -ne $agentWix) { $errors += "Git tag v$TagVersion ($tagWix) != agent.json Version $agentVersion ($agentWix)" }
if ($bundleVersion -ne $tagWix) { $errors += "Bundle.wxs Version $bundleVersion != tag $tagWix" }
if ($packageVersion -ne $tagWix) { $errors += "Package.wxs Version $packageVersion != tag $tagWix" }

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    throw 'Release version alignment failed. Bump Package.wxs, Bundle.wxs, and agent.json together.'
}

Write-Host "Release version alignment OK: tag=$TagVersion agent=$agentVersion bundle=$bundleVersion package=$packageVersion"
