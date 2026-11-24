#Requires -Version 7.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$RestorePackagesPath,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
foreach ($id in @('KingbaseConnector', 'KingbaseConnector.DependencyInjection')) {
    $lower = $id.ToLowerInvariant()
    $packed = Join-Path $PackageDirectory "$id.$Version.nupkg"
    $restored = Join-Path $RestorePackagesPath "$lower/$Version/$lower.$Version.nupkg"
    # An equal version may exist on another feed; consumers must execute this exact artifact.
    if ((Get-FileHash -LiteralPath $packed -Algorithm SHA512).Hash -cne
        (Get-FileHash -LiteralPath $restored -Algorithm SHA512).Hash) {
        throw "The restored $id archive differs from the package built in this run."
    }
}
