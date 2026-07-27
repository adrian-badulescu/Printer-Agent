param(
    [Parameter(Mandatory = $true)]
    [string] $InstallerPath,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [string] $DownloadUrl = 'https://github.com/adrian-badulescu/Printer-Agent/releases/latest/download/URSPrinterAgentSetup.exe',

    [string] $UpdateSignatureSecret = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $InstallerPath)) {
    throw "Installer not found: $InstallerPath"
}

$version = $Version.Trim().TrimStart('v', 'V')
$sha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
$payload = "$version|$DownloadUrl|$sha256"
$signature = ''

if (-not [string]::IsNullOrWhiteSpace($UpdateSignatureSecret)) {
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($UpdateSignatureSecret))
    try {
        $signature = [BitConverter]::ToString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))).Replace('-', '')
    }
    finally {
        $hmac.Dispose()
    }
}

$manifest = [ordered]@{
    version     = $version
    downloadUrl = $DownloadUrl
    sha256      = $sha256
    signature   = $signature
}

$json = ($manifest | ConvertTo-Json -Compress)
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($OutputPath, $json, $utf8NoBom)

Write-Host "Wrote release manifest version=$version sha256=$sha256 signatureLength=$($signature.Length)"
