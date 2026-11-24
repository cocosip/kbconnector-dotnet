# KingbaseConnector

Independent managed ADO.NET driver for KingbaseES, developed against the [design and feature matrix](docs/README.md). V9 is the first target; V8 and EF Core providers follow later.

This is an early development build. Package identity and licensing are provisional; no public release or live-server compatibility is claimed. The [progress tracker](docs/kbconnector-development-progress.md) records implemented subsets and acceptance evidence.

The core targets `netstandard2.0`, `netstandard2.1`, `net8.0`, `net9.0`, and `net10.0`. Install the SDK pinned in `global.json` and the .NET 8/9/10 runtimes, then run:

```powershell
./eng/verify.ps1
```

This restores, builds, runs offline tests, packs, and runs a consumer for each explicitly selected package asset. Standard assets run on .NET 8; this does not certify .NET Framework or other platforms. Tests do not contact a database by default. `KINGBASE_TEST_LIVE=1` enables the live-test gate, which rejects execution until a real profile and acceptance tests are registered.

Official reference DLLs are comparison inputs only and are excluded from product references and packages.
