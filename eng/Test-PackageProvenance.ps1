#Requires -Version 7.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$check = Join-Path $PSScriptRoot 'Assert-PackageProvenance.ps1'
$versionCheck = Join-Path $PSScriptRoot 'Assert-PackageVersions.ps1'
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/package-provenance-tests'))
$fixture = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N'))
$feed = Join-Path $fixture 'feed'
$cache = Join-Path $fixture 'cache'
$null = New-Item -ItemType Directory -Path $feed -Force

function New-PackageFixture([string]$Id, [string]$Version) {
    $stream = [IO.File]::Open((Join-Path $feed "$Id.0.1.0.nupkg"), [IO.FileMode]::Create)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($archive.CreateEntry("$Id.nuspec").Open())
        try { $writer.Write("<package><metadata><id>$Id</id><version>$Version</version></metadata></package>") }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose(); $stream.Dispose() }
}

try {
    foreach ($id in @('KingbaseConnector', 'KingbaseConnector.DependencyInjection')) {
        $lower = $id.ToLowerInvariant()
        $cachedDirectory = Join-Path $cache "$lower/0.1.0"
        $null = New-Item -ItemType Directory -Path $cachedDirectory -Force
        New-PackageFixture $id 0.1.0
        Copy-Item -LiteralPath (Join-Path $feed "$id.0.1.0.nupkg") -Destination (Join-Path $cachedDirectory "$lower.0.1.0.nupkg")
    }
    & $check -PackageDirectory $feed -RestorePackagesPath $cache -Version 0.1.0
    foreach ($id in @('KingbaseConnector', 'KingbaseConnector.DependencyInjection')) {
        $lower = $id.ToLowerInvariant()
        $cached = Join-Path $cache "$lower/0.1.0/$lower.0.1.0.nupkg"
        Set-Content -LiteralPath $cached -Value 'different archive from another feed'
        $rejected = $false
        try { & $check -PackageDirectory $feed -RestorePackagesPath $cache -Version 0.1.0 } catch { $rejected = $true }
        if (!$rejected) { throw "A different $id archive must not pass provenance validation." }
        Copy-Item -LiteralPath (Join-Path $feed "$id.0.1.0.nupkg") -Destination $cached -Force
    }
    & $check -PackageDirectory $feed -RestorePackagesPath $cache -Version 0.1.0
    & $versionCheck -PackageDirectory $feed -Version 0.1.0
    & $versionCheck -PackageDirectory $feed -Version 0.1.0 -Tag v0.1.0
    & $versionCheck -PackageDirectory $feed -Version 0.1.0 -Tag 0.1.0
    foreach ($tag in @('v0.2.0', 'V0.1.0', 'v0.1.0-dev')) {
        $rejected = $false
        try { & $versionCheck -PackageDirectory $feed -Version 0.1.0 -Tag $tag } catch { $rejected = $true }
        if (!$rejected) { throw 'A tag must equal the configured version, with only an optional lowercase v prefix.' }
    }
    foreach ($id in @('KingbaseConnector', 'KingbaseConnector.DependencyInjection')) {
        New-PackageFixture $id 0.2.0
        $rejected = $false
        try { & $versionCheck -PackageDirectory $feed -Version 0.1.0 -Tag v0.1.0 } catch { $rejected = $true }
        if (!$rejected) { throw "A mismatched internal $id package version must be rejected even with a matching filename/tag." }
        New-PackageFixture $id 0.1.0
    }
    Write-Host 'PASS: package provenance/versions (exact bytes; core/DI nuspec versions; matching optional-v tags; mismatches rejected).'
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if (!$resolvedFixture.StartsWith($fixtureRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a fixture outside the package-provenance test directory.'
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
