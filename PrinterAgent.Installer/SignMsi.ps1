# Signs one MSI. Used from PrinterAgent.Installer.wixproj (batched per culture output).
# Modes:
#   SelfSigned  - creates or reuses CN=URS Printer Agent Self-Signed in CurrentUser\My (code signing).
#   Enterprise  - uses first cert in CurrentUser\My whose subject matches CN=U.R.S. (existing dev/release flow).

param(
    [Parameter(Mandatory = $true)]
    [string] $MsiPath,
    [Parameter(Mandatory = $true)]
    [ValidateSet('SelfSigned', 'Enterprise')]
    [string] $Mode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $MsiPath)) {
    throw "MSI not found: $MsiPath"
}

function Get-EnterpriseSigningCertificate {
    $cert = Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object { $_.Subject -match 'CN=U\.R\.S\.' } |
        Select-Object -First 1
    if (-not $cert) {
        throw 'Signing certificate CN=U.R.S. not found in CurrentUser\My. Use -p:SelfSignedMsiSigning=true for self-signed, or install your code-signing cert.'
    }
    return $cert
}

function Get-OrCreateSelfSignedCodeSigningCertificate {
    $subject = 'CN=URS Printer Agent Self-Signed'
    $existing = Get-ChildItem -Path Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey }
    if ($existing) {
        return $existing | Select-Object -First 1
    }

    return New-SelfSignedCertificate `
        -Subject $subject `
        -Type CodeSigningCert `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyExportPolicy Exportable `
        -KeySpec Signature `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(10)
}

$cert = if ($Mode -eq 'SelfSigned') {
    Get-OrCreateSelfSignedCodeSigningCertificate
}
else {
    Get-EnterpriseSigningCertificate
}

$timestamp = 'http://timestamp.digicert.com'
$result = $null
try {
    $result = Set-AuthenticodeSignature -FilePath $MsiPath -Certificate $cert -TimestampServer $timestamp -HashAlgorithm SHA256
}
catch {
    Write-Warning "Timestamp server failed ($timestamp); signing without timestamp. $_"
    $result = Set-AuthenticodeSignature -FilePath $MsiPath -Certificate $cert -HashAlgorithm SHA256
}

if (-not $result) {
    throw "Authenticode signing produced no result for $MsiPath"
}
if ($result.Status -in @('NotSigned', 'HashMismatch')) {
    throw "Authenticode signing failed for $MsiPath : $($result.Status) $($result.StatusMessage)"
}

Write-Host "Signed $MsiPath ($($result.Status))"
