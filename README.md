# KingbaseConnector

Independent managed ADO.NET driver for KingbaseES, developed against the [design and feature matrix](docs/README.md). V9 is the first target; V8 and EF Core providers follow later.

This is an early development build. Package identity and licensing are provisional; no public release or live-server compatibility is claimed. The [progress tracker](docs/kbconnector-development-progress.md) records implemented subsets and acceptance evidence.

The core targets `netstandard2.0`, `netstandard2.1`, `net8.0`, `net9.0`, and `net10.0`. Install an SDK that supports `net10.0` and `.slnx`, plus the .NET 8/9/10 runtimes, then run:

```powershell
./eng/verify.ps1
```

This restores, builds, runs offline tests, packs, and runs a consumer for each explicitly selected package asset. Standard assets run on .NET 8; this does not certify .NET Framework or other platforms. Tests do not contact a database by default. `KINGBASE_TEST_LIVE=1` enables the live-test gate, which rejects execution until a real profile and acceptance tests are registered.

Official reference DLLs are comparison inputs only and are excluded from product references and packages.

`KingbaseNumeric` preserves high-precision numeric values without rounding:

```csharp
var value = KingbaseNumeric.Parse("123456789012345678901234567890.00100");
Console.WriteLine(value); // Preserves the coefficient and scale, including trailing zeros.
```

`ToDecimal()` converts exactly or throws `OverflowException`, including when conversion would round a nonzero digit. The value representation currently bounds coefficient precision and nonnegative scale to 16,384 each; these are client resource limits. Numeric binary formats, public command/Reader access and real-server acceptance remain in progress.

The public input model includes `KingbaseParameter`, `KingbaseDbType` and `KingbaseParameterCollection`:

```csharp
var parameters = new KingbaseParameterCollection();
parameters.AddWithValue("amount", 1.2300m).Scale = 2; // Exact input constraint; never rounds.
parameters.Add("optional_id", KingbaseDbType.Integer).Value = DBNull.Value;
```

Each parameter belongs to one collection; nonempty names match case-insensitively with an optional `@`/`:` prefix. The internal PG path resolves actual catalog types, requires an explicit type for NULL, snapshots mutable byte inputs, and rejects unsupported directions or lossy size/precision/scale constraints. Public Connection/Command execution and other-mode parameter contracts are still pending.

`KingbaseDataReader` implements the buffered scalar Reader contract over completed internal PG results, including multiple-result traversal, exact typed/provider numeric access, binary/text slices and basic column metadata. Construction remains internal until Command integration. It does not yet provide streaming, CommandBehavior or Connection ownership. Unknown schema facts remain unset; raw PG identities are available in `GetSchemaTable` provider columns.
