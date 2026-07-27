param(
    [string] $ManifestUrl = 'https://github.com/adrian-badulescu/Printer-Agent/releases/latest/download/release-manifest.json',

    [string] $UpdateSignatureSecret = $env:UPDATE_SIGNATURE_SECRET,

    [string] $InstallDirAgentJson = "${env:ProgramFiles}\URSPrinterAgent\agent.json",

    [string] $ProgramDataAgentJson = "$env:ProgramData\URSPrinterAgent\agent.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-AgentSecret([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($null -eq $json.UpdateSignatureSecret) { return '' }
    return [string]$json.UpdateSignatureSecret
}

function Get-ExpectedManifestSignature(
    [string] $Secret,
    [string] $Version,
    [string] $DownloadUrl,
    [string] $Sha256
) {
    $payload = "$Version|$DownloadUrl|$Sha256"
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Secret))
    try {
        return [BitConverter]::ToString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))).Replace('-', '')
    }
    finally {
        $hmac.Dispose()
    }
}

Write-Host "`n=== Release manifest ===" -ForegroundColor Cyan
$manifest = Invoke-RestMethod -Uri $ManifestUrl
$manifest | Format-List version, downloadUrl, sha256, signature

Write-Host "`n=== Agent config sources ===" -ForegroundColor Cyan
$installSecret = Get-AgentSecret $InstallDirAgentJson
$programDataSecret = Get-AgentSecret $ProgramDataAgentJson

Write-Host "Install-dir:  $InstallDirAgentJson"
Write-Host "  exists:     $(Test-Path -LiteralPath $InstallDirAgentJson)"
Write-Host "  secret len: $($installSecret.Length)"
Write-Host "  placeholder:$(if ($installSecret -eq 'change-me-same-as-backend-PrinterAgent') { ' YES (agent uses this, not ProgramData!)' } else { ' no' })"

Write-Host "ProgramData:  $ProgramDataAgentJson"
Write-Host "  exists:     $(Test-Path -LiteralPath $ProgramDataAgentJson)"
Write-Host "  secret len: $($programDataSecret.Length)"

if (-not $UpdateSignatureSecret) {
    Write-Host "`nPass -UpdateSignatureSecret or set `$env:UPDATE_SIGNATURE_SECRET for HMAC check." -ForegroundColor Yellow
}
else {
    Write-Host "`n=== HMAC check (GitHub / param secret) ===" -ForegroundColor Cyan
    $expected = Get-ExpectedManifestSignature `
        -Secret $UpdateSignatureSecret.Trim() `
        -Version $manifest.version `
        -DownloadUrl $manifest.downloadUrl `
        -Sha256 $manifest.sha256
    Write-Host "Expected: $($expected.Substring(0, [Math]::Min(16, $expected.Length)))..."
    Write-Host "Manifest: $($manifest.signature.Substring(0, [Math]::Min(16, $manifest.signature.Length)))..."
    Write-Host "Match:    $($expected -eq $manifest.signature)"

    if ($installSecret) {
        $fromInstall = Get-ExpectedManifestSignature `
            -Secret $installSecret.Trim() `
            -Version $manifest.version `
            -DownloadUrl $manifest.downloadUrl `
            -Sha256 $manifest.sha256
        Write-Host "`n=== HMAC check (install-dir secret — what the service uses) ===" -ForegroundColor Cyan
        Write-Host "Match: $($fromInstall -eq $manifest.signature)"
    }
}

Write-Host "`nNote: UpdateSignatureSecret is read from install-dir first (BundledFirstKeys)." -ForegroundColor DarkGray
Write-Host "Edit: $InstallDirAgentJson  then Restart-Service URSPrinterAgent`n" -ForegroundColor DarkGray
