# CI/CD and package releases

The [workflow](../.github/workflows/ci-cd.yml) follows the local OceanBase connector's master/tag convention. Version comes only from `Directory.Build.props`; use a standard `major.minor.patch` version without a prerelease suffix. The repository has no `global.json` SDK pin.

| Trigger | Validation | Publication |
| --- | --- | --- |
| Push to master | Full Windows/Linux matrix | None |
| Pull request targeting master | Full Windows/Linux matrix | None |
| Manual workflow dispatch | Full Windows/Linux matrix | None, including dispatch on a tag |
| Push `v<Version>` or `<Version>` tag | Validate tag/version/HEAD, generate notes, then full matrix | NuGet core and DI packages, then GitHub Release |

Each matrix job installs the current .NET 8/9/10 SDK lines and runs `./eng/verify.ps1 -ContinuousIntegration`. This builds all five product assets, runs the full offline suites, packs both packages, checks both nuspec identities/versions against the central version, verifies DI dependency floors and executes five explicit package consumers with fresh caches. SHA-512 comparisons require the restored core and DI archives to match the packages built in that run. Standard assets run on .NET 8; modern assets use their matching runtimes. `KINGBASE_TEST_LIVE=0` keeps the live gate visibly skipped. No database credentials are used.

TRX results are uploaded for each OS, including failed test runs. The Windows job uploads the verified package artifact. Publication waits for the entire OS matrix, downloads that artifact, and never checks out or rebuilds source. Before obtaining NuGet credentials, it runs the tested checker included in the artifact to compare the downloaded packages' actual nuspec versions, the central version exported by the build job, and the tag. For example, configuration `1.0.0` accepts only tag `v1.0.0` or `1.0.0`, and both package internals must be `1.0.0`; matching filenames alone are insufficient. Pushes to other branches do not trigger this workflow; concurrent pull-request runs replace older runs for that pull request.

## Trusted publishing setup

Configure a nuget.org trusted publishing policy for GitHub owner `cocosip`, repository `kbconnector-dotnet`, and workflow filename `ci-cd.yml`, with permission to publish `KingbaseConnector` and `KingbaseConnector.DependencyInjection`. The workflow does not select a GitHub environment. Its NuGet profile defaults to `virtual`, matching the reference repository; override the repository variable `NUGET_USER` if the publisher profile differs. No persistent NuGet API-key secret is required.

Only the tag-push publish job requests `id-token: write` and `contents: write`. `NuGet/login@v1` exchanges the GitHub OIDC token for a temporary NuGet key. Master, pull-request and manual runs cannot execute publication. External policy/ownership setup and an actual hosted workflow run must be verified separately from local script tests.

## Preparing a release

1. Update `<Version>` in `Directory.Build.props`. Add optional migration/scope notes at `docs/release-notes/<Version>.md`.
2. Run `./eng/verify.ps1 -ContinuousIntegration`, review the change, and commit on master.
3. Create and push an annotated or lightweight tag equal to the version, optionally prefixed with lowercase `v`, at that commit. Publishing the tag starts the release workflow.

`eng/New-ReleaseNotes.ps1` rejects a mismatched/nonstandard version, a missing tag, a tag not pointing to HEAD, or uncommitted tracked changes. It selects an earlier reachable standard-version tag, excludes merge commits, adds only the current version's migration notes, and links to the tagged progress/compatibility scope. Without an earlier release, it includes the initial history. Numeric-looking checkpoint and prerelease tags are excluded from release history boundaries.

Generated notes live at `artifacts/packages/RELEASE_NOTES.md`. The workflow artifact contains those notes, the two packages and `Assert-PackageVersions.ps1`; GitHub Release assets contain the notes/packages. NuGet pushes use `--skip-duplicate` for a retry after partial publication; GitHub Release creation reports an error if the release already exists. Recover an existing release explicitly rather than replacing its notes/assets automatically.

## Local checks and evidence limits

```powershell
./eng/Test-ReleaseNotes.ps1
./eng/Test-PackageProvenance.ps1
./eng/verify.ps1 -ContinuousIntegration
```

The first two commands use disposable fixtures under ignored `artifacts/` directories; they do not create release tags in this repository or publish anything. The aggregate verifier runs both checks. CI reporting adds TRX files under `artifacts/test-results/` and enables `ContinuousIntegrationBuild`; the default local verifier remains available without that switch.

These workflows automate engineering checks and publication mechanics. They do not replace the [V9 release gates](kbconnector-design.md#112-v9-release-gates), decide licensing, or establish real Kingbase compatibility. Current hosted Windows/Linux, OIDC and publication evidence is recorded separately in the [progress tracker](kbconnector-development-progress.md).
