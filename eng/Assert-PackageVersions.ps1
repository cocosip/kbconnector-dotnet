#Requires -Version 7.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Version,
    [string]$Tag = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -cnotmatch '\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z' -or
    ($Tag -and $Tag -cne "v$Version" -and $Tag -cne $Version)) {
    throw 'The release tag and package version must match the standard configured version, with an optional v prefix.'
}
foreach ($id in @('KingbaseConnector', 'KingbaseConnector.DependencyInjection')) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $PackageDirectory "$id.$Version.nupkg"))
    try {
        $manifests = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
        if ($manifests.Count -ne 1) { throw "The $id archive must have exactly one nuspec." }
        $reader = [IO.StreamReader]::new($manifests[0].Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ([string]$nuspec.package.metadata.id -cne $id -or [string]$nuspec.package.metadata.version -cne $Version) {
            throw "The $id nuspec identity/version does not match the configured release version."
        }
    }
    finally { $archive.Dispose() }
}
Write-Host "PASS: core and DI package versions match $Version."
