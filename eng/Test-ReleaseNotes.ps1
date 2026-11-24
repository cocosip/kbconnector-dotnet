#Requires -Version 7.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$generator = Join-Path $PSScriptRoot 'New-ReleaseNotes.ps1'
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/release-notes-tests'))
$fixture = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture -Force

function Invoke-Git([string[]]$Arguments) {
    & git @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Fixture git command failed: $($Arguments -join ' ')" }
}
function Assert-Contains([string]$Text, [string]$Value) {
    if (!$Text.Contains($Value)) { throw "Release notes are missing: $Value" }
}
function Assert-Rejected([string]$Tag, [string]$OutputPath) {
    $rejected = $false
    try { & $generator -Tag $Tag -OutputPath $OutputPath } catch { $rejected = $true }
    if (!$rejected -or (Test-Path -LiteralPath $OutputPath)) { throw 'Invalid release input must fail before writing notes.' }
}

Push-Location $fixture
try {
    Invoke-Git @('init', '--quiet', '--initial-branch=master')
    Invoke-Git @('config', 'user.name', 'Release test')
    Invoke-Git @('config', 'user.email', 'release-test@example.invalid')
    Invoke-Git @('config', 'commit.gpgsign', 'false')
    Invoke-Git @('config', 'tag.gpgsign', 'false')
    Set-Content Directory.Build.props '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>'
    Invoke-Git @('add', 'Directory.Build.props')
    Invoke-Git @('commit', '--quiet', '-m', 'Initial package')
    Invoke-Git @('tag', '-a', 'v0.1.0', '-m', 'First release')
    & $generator -Tag v0.1.0 -OutputPath first.md
    $first = Get-Content first.md -Raw
    Assert-Contains $first 'First release'
    Assert-Contains $first 'Initial package'
    Assert-Contains $first 'dotnet add package KingbaseConnector --version 0.1.0'
    Assert-Contains $first 'dotnet add package KingbaseConnector.DependencyInjection --version 0.1.0'
    Assert-Contains $first 'kbconnector-dotnet/blob/v0.1.0/docs/kbconnector-development-progress.md'

    Invoke-Git @('commit', '--quiet', '--allow-empty', '-m', 'Unreleased checkpoint')
    Invoke-Git @('tag', 'checkpoint')
    Invoke-Git @('tag', 'v0.1.999-archive')
    Set-Content Directory.Build.props '<Project><PropertyGroup><Version>0.2.0</Version></PropertyGroup></Project>'
    $null = New-Item -ItemType Directory -Path docs/release-notes -Force
    Set-Content docs/release-notes/0.2.0.md '## Migration notes: review the updated connection contract.'
    Set-Content docs/release-notes/0.1.0.md 'Previous version migration must not leak.'
    Invoke-Git @('add', 'Directory.Build.props', 'docs/release-notes')
    Invoke-Git @('commit', '--quiet', '-m', 'Fix protocol handling')
    Invoke-Git @('tag', '0.2.0')
    & $generator -Tag 0.2.0 -OutputPath next.md
    $next = Get-Content next.md -Raw
    Assert-Contains $next 'Fix protocol handling'
    Assert-Contains $next 'Unreleased checkpoint'
    Assert-Contains $next 'compare/v0.1.0...0.2.0'
    Assert-Contains $next '## Migration notes: review the updated connection contract.'
    if ($next.Contains('Previous version migration must not leak.') -or $next.Contains('Initial package')) {
        throw 'Previous release content leaked into the new release.'
    }
    Assert-Rejected v9.9.9 invalid-version.md
    Assert-Rejected v0.2.0 missing-tag.md
    Invoke-Git @('commit', '--quiet', '--allow-empty', '-m', 'Unreleased change')
    Assert-Rejected 0.2.0 wrong-commit.md
    Set-Content Directory.Build.props '<Project><PropertyGroup><Version>0.3.0-dev</Version></PropertyGroup></Project>'
    Invoke-Git @('add', 'Directory.Build.props')
    Invoke-Git @('commit', '--quiet', '-m', 'Invalid prerelease version')
    Invoke-Git @('tag', 'v0.3.0-dev')
    Assert-Rejected v0.3.0-dev invalid-prerelease.md
    Set-Content Directory.Build.props '<Project><PropertyGroup><Version>0.3.0</Version></PropertyGroup></Project>'
    Invoke-Git @('add', 'Directory.Build.props')
    Invoke-Git @('commit', '--quiet', '-m', 'Prepare standard release')
    Invoke-Git @('tag', 'v0.3.0')
    & $generator -Tag v0.3.0 -OutputPath stable.md
    Assert-Contains (Get-Content stable.md -Raw) 'compare/0.2.0...v0.3.0'
    Set-Content Directory.Build.props '<Project><PropertyGroup><Version>0.4.0</Version></PropertyGroup></Project>'
    Invoke-Git @('tag', 'v0.4.0')
    Assert-Rejected v0.4.0 dirty-version.md
    Write-Host 'PASS: release notes (first/subsequent, annotated/lightweight, standard versions, history, migration notes, invalid tag/commit/dirty source).'
}
finally {
    Pop-Location
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if (!$resolvedFixture.StartsWith($fixtureRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a fixture outside the release-notes test directory.'
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
# Expected rejection cases may leave git's nonzero native exit code behind.
$global:LASTEXITCODE = 0
