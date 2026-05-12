# Signs one MSI. Delegates to SignAuthenticode.ps1 (same cert / modes as CI uses for MSIs).

param(
    [Parameter(Mandatory = $true)]
    [string] $MsiPath,
    [Parameter(Mandatory = $true)]
    [ValidateSet('SelfSigned', 'Enterprise')]
    [string] $Mode
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $scriptDir 'SignAuthenticode.ps1') -Path $MsiPath -Mode $Mode
