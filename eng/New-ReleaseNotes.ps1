#Requires -Version 7.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Tag, [string]$OutputPath = 'artifacts/packages/RELEASE_NOTES.md')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Invoke-Git([string[]]$Arguments) {
    $result = & git @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { throw "git failed ($LASTEXITCODE): $($Arguments -join ' ')" }
    return $result
}

$root = Invoke-Git @('rev-parse', '--show-toplevel')
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
$versionPattern = '(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)'
if ($version -cnotmatch "\A$versionPattern\z" -or ($Tag -cne "v$version" -and $Tag -cne $version)) {
    throw 'The release tag must match the standard major.minor.patch project version, with an optional v prefix.'
}
$commit = Invoke-Git @('rev-parse', '--verify', "refs/tags/$Tag^{commit}")
if ($commit -cne (Invoke-Git @('rev-parse', 'HEAD'))) { throw 'The release tag must point to HEAD.' }
& git diff --quiet HEAD --
if ($LASTEXITCODE -ne 0) { throw 'Release notes require a clean tracked working tree.' }

$previous = $null
$parents = (Invoke-Git @('rev-list', '--parents', '-n', '1', $commit)).Split(' ')
if ($parents.Count -gt 1) {
    # Exact patterns exclude checkpoint/prerelease tags, including numeric-looking names.
    $tags = @(Invoke-Git @('tag', '--list') | Where-Object { $_ -cmatch "\Av?$versionPattern\z" })
    if ($tags.Count -gt 0) {
        $describe = @('describe', '--tags', '--abbrev=0')
        foreach ($candidate in $tags) { $describe += @('--match', $candidate) }
        $describe += $parents[1]
        $previous = & git @describe 2>$null
        if ($LASTEXITCODE -ne 0) { $previous = $null }
    }
}
$range = if ($previous) { "$previous..$Tag" } else { $Tag }
$commits = @(Invoke-Git @('log', $range, '--no-merges', '--format=- %h %s (%an)'))
$url = 'https://github.com/cocosip/kbconnector-dotnet'
$comparison = if ($previous) {
    "Changes since [$previous]($url/releases/tag/$previous). [Full changelog]($url/compare/$previous...$Tag)."
} else {
    "First release. [Commit history]($url/commits/$Tag)."
}
$versionNotesPath = Join-Path $root "docs/release-notes/$version.md"
$versionNotes = if (Test-Path -LiteralPath $versionNotesPath) { Get-Content -LiteralPath $versionNotesPath -Raw }
$notes = @(
    "# KingbaseConnector $version"
    ''
    '## Packages'
    ''
    '```shell'
    "dotnet add package KingbaseConnector --version $version"
    "dotnet add package KingbaseConnector.DependencyInjection --version $version"
    '```'
    ''
    if ($versionNotes) { $versionNotes.TrimEnd(); '' }
    '## Changes'
    ''
    $comparison
    ''
    "$($commits.Count) commits (excluding merges)."
    ''
    $commits
    ''
    "See [implementation and acceptance status]($url/blob/$Tag/docs/kbconnector-development-progress.md) and [compatibility scope]($url/blob/$Tag/README.md)."
    'Offline tests and CI results do not establish real Kingbase server acceptance.'
)
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$null = New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force
[IO.File]::WriteAllText($destination, ($notes -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Validated release $version; wrote $destination"
