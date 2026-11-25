# KingbaseConnector

Independent managed ADO.NET driver for KingbaseES, developed against the [design and feature matrix](docs/README.md). V9 is the first target; V8 and EF Core providers follow later.

This is an early development build. Package identity and licensing are provisional. Bounded live bootstrap and PG execution results are recorded in the [progress tracker](docs/kbconnector-development-progress.md); complete server compatibility and release readiness remain pending.

The core targets `netstandard2.0`, `netstandard2.1`, `net8.0`, `net9.0`, and `net10.0`. Install an SDK that supports `net10.0` and `.slnx`, plus the .NET 8/9/10 runtimes, then run:

```powershell
./eng/verify.ps1
```

This restores, builds, runs offline tests, packs, and runs a consumer for each explicitly selected package asset. Standard assets run on .NET 8; this does not certify .NET Framework or other platforms. Tests do not contact a database by default. The separate [private live suite](docs/live-testing.md) reads external process configuration; missing configuration fails when LIVE is explicitly enabled.

The [CI/CD workflow](docs/ci-cd.md) runs these checks on Windows and Linux for master pushes, pull requests and manual runs. A pushed version tag matching `Directory.Build.props` enables NuGet trusted publishing for core/DI and generates a GitHub Release after both OS jobs pass. `./eng/verify.ps1 -ContinuousIntegration` also produces TRX reports locally. Hosted workflow execution and publishing-policy setup remain separate acceptance evidence.

Official reference DLLs are comparison inputs only and are excluded from product references and packages.

`KingbaseNumeric` preserves high-precision numeric values without rounding:

```csharp
var value = KingbaseNumeric.Parse("123456789012345678901234567890.00100");
Console.WriteLine(value); // Preserves the coefficient and scale, including trailing zeros.
```

`ToDecimal()` converts exactly or throws `OverflowException`, including when conversion would round a nonzero digit. The value representation currently bounds coefficient precision and nonnegative scale to 16,384 each; these are client resource limits. Numeric binary formats and real-server acceptance remain in progress.

The public input model includes `KingbaseParameter`, `KingbaseDbType` and `KingbaseParameterCollection`:

```csharp
var parameters = new KingbaseParameterCollection();
parameters.AddWithValue("amount", 1.2300m).Scale = 2; // Exact input constraint; never rounds.
parameters.Add("optional_id", KingbaseDbType.Integer).Value = DBNull.Value;
```

Each parameter belongs to one collection; nonempty names match case-insensitively with an optional `@`/`:` prefix. The PG path resolves actual catalog types, requires an explicit type for NULL, snapshots mutable byte inputs, and rejects unsupported directions or lossy size/precision/scale constraints. Other-mode parameter contracts remain pending.

`KingbaseConnection`, `KingbaseCommand` and `KingbaseFactory.Instance` expose the initial public PG execution path. It currently requires PostgreSql compatibility mode. Default `Pooling=true` rents ordinary PG sessions; `Pooling=false` creates dedicated sessions. Open confirms product identity and required catalog types; text commands bind values separately from SQL. Cancellation or an interrupted exchange closes the physical session; ordinary synchronized server errors allow reuse. `KingbaseException` exposes SQLSTATE safely; its explicit server text properties may contain sensitive data.

```csharp
using var connection = new KingbaseConnection(
    "Host=localhost;Database=app;Username=user;Password=secret");
await connection.OpenAsync();
using var command = new KingbaseCommand("SELECT :amount", connection);
command.Parameters.AddWithValue("amount", 1.2300m);
var amount = await command.ExecuteScalarAsync();
```

The default TLS mode verifies the server certificate and hostname using platform trust. For a private CA, add `Root Certificate=/path/to/root-ca.crt` with `SSL Mode=VerifyFull`. The file can contain a DER CA certificate or a PEM CA bundle with self-issued roots and optional intermediates; it replaces the allowed trust anchors without changing OS trust stores. Files are read on physical connection creation, so pooled reuse keeps its existing TLS session; clear the pool when new sessions must load a changed file. Invalid/missing files reject before TCP. Root Certificate with Require/Disable rejects instead of ignoring the setting. Revocation checking remains disabled. TLS policy has simulation coverage; the initial bounded live tests do not establish real-server certificate acceptance.

The PG authentication path supports server-requested password/MD5 and SCRAM-SHA-256. SCRAM verifies the extended nonce and server signature before accepting authentication, applies the PG SASLprep/raw-password profile, and bounds server iterations to 1..1,000,000, SASL payloads to 16 KiB and salt to 1 KiB. SCRAM-SHA-256-PLUS/channel binding and SM3 algorithms remain pending; a PLUS-only offer rejects. Standard protocol vectors and loopback logins do not establish real Kingbase password-policy compatibility.

`KingbaseDataReader` buffers completed PG results with exact typed/provider numeric access, binary/text slices and basic column metadata. A connection owns its returned Reader until close. Default, CloseConnection, SingleRow and SingleResult behaviors are supported. Unknown schema facts remain unset; raw PG identities are available in `GetSchemaTable` provider columns. Streaming/SequentialAccess and system transactions remain pending.

PG DATE maps to an Unspecified midnight `DateTime`. Use `KingbaseDbType.Date`, `DbType.Date` or `DataTypeName="pg_catalog.date"` for explicit binding; DateTime input must have no time component and Kind Unspecified. `KingbaseDate` is inferred as DATE on every asset; modern assets also infer `DateOnly` and support `GetFieldValue<DateOnly>`. DateTime inference remains pending until timestamp mappings are defined. DATE results require confirmed ISO DateStyle (`ISO, MDY`, `ISO, DMY` or `ISO, YMD`); unsupported or unknown output styles reject conversion. Use application SQL to select a supported style when necessary.

`reader.GetFieldValue<KingbaseDate>(ordinal)` or `GetProviderSpecificValue` preserves BC dates, years outside the CLR range and explicit `infinity` / `-infinity`. Default DateTime/DateOnly conversion throws OverflowException for these values. For example, `KingbaseDate.Parse("0001-02-29 BC")` preserves a leap day in 1 BC; negative constructor years mean BC, with no year zero. The immutable value uses the proleptic Gregorian calendar, defaults to 0001-01-01 and accepts absolute years up to seven digits; each server still enforces its own range. No timezone conversion, date truncation or MinValue/MaxValue infinity substitution occurs.

PG TIME uses `TimeSpan` from zero through 24 hours inclusive at exact microsecond precision. Specify `DbType.Time`, `KingbaseDbType.Time` or `DataTypeName="pg_catalog.time"` for TimeSpan input; TimeSpan inference is reserved until INTERVAL is defined. Modern assets infer `TimeOnly` and support `GetFieldValue<TimeOnly>`; reading `24:00:00` as TimeOnly throws OverflowException. Extra submicrosecond ticks reject instead of rounding. Numeric TIME/TIMETZ text does not depend on DateStyle or session TimeZone.

PG TIMETZ uses immutable `KingbaseTimeTz` as both its default and provider value, inferred on every asset. For example, `KingbaseTimeTz.Parse("12:34:56.123456+05:30:01")` preserves microseconds and an offset with seconds. Its `Time` is the wall-clock TimeSpan and `Offset` is signed east of UTC, in whole seconds with magnitude below 16 hours. Default is midnight at offset zero. Equality compares both components exactly; no date, local timezone or UTC normalization is introduced. Bind explicitly using `KingbaseDbType.TimeTz` or `pg_catalog.timetz`; DbType.Time is compatible with an explicit TIMETZ selection but selects TIME alone. TimeSpan/TimeOnly/DateTimeOffset cannot silently discard or manufacture the offset. TIMESTAMP/TIMESTAMPTZ/INTERVAL, binary temporal formats and other modes' temporal types remain pending.

PG commands support `Prepare()` / `PrepareAsync(token)` and `Unprepare()` / `UnprepareAsync(token)`:

```csharp
await command.PrepareAsync();
command.Parameters[0].Value = 2.50m;
var nextAmount = await command.ExecuteScalarAsync();
await command.UnprepareAsync();
```

Preparation validates current SQL and catalog-bound input types; typed NULL is supported. Value changes reuse the statement. SQL/type/lexical changes, eviction, pool reset or reconnection detach stale handles before execution. Unprepare closes the attached shared physical handle, so another command using it also detaches. Edits/disposal detach without network I/O; cached server resources stay within the physical session's 64-statement/1 MiB limit. Controls use CommandTimeout, caller cancellation and Cancel, and reject an active Reader. Server preparation/schema errors are returned without replay; callers can explicitly prepare a replacement. The live suite records bounded PG schema-error and transaction-recovery evidence; other modes and platforms remain unaccepted.

Automatic PG preparation is opt-in: set `Max Auto Prepare=16;Auto Prepare Min Usages=5` (or the builder properties `MaxAutoPrepare` / `AutoPrepareMinUsages`). The maximum defaults to 0 (disabled) and accepts 0..64; the usage threshold defaults to 5 and accepts 1..Int32.MaxValue. Successful unnamed protocol exchanges with the same SQL, ordered actual types and lexical state count per physical session, across command objects. The threshold execution prepares first; value changes reuse its handle. Candidates retain no values/schema and are bounded at 128 entries/1 MiB of encoded signatures, separately from the global prepared limits. Automatic capacity evicts the least recently used automatic statement with acknowledged Close. Manual Prepare promotes a shared automatic entry to explicit ownership; the global physical limit can still evict it. Unprepare of an attached handle, plan-invalidating errors and pool reset restart the relevant usage counts. Unprepare before a handle is attached is a no-op and retains candidate counts. An automatic handle attaches before execution, so Unprepare can close it even after its first execution returns a synchronized SQL error. A subsequent caller request may prepare again at its threshold; a failed request is never replayed. With automatic preparation disabled, detached execution uses ordinary binding until explicitly prepared again. Managed transaction controls remain unnamed. Batch APIs remain pending.

Local `KingbaseTransaction` supports synchronous/asynchronous Begin, Commit, Rollback and savepoints on the initial PG path:

```csharp
using var transaction = await connection.BeginTransactionAsync();
await transaction.SaveAsync("before_update");
using var update = new KingbaseCommand("UPDATE items SET amount=:amount WHERE id=:id", connection)
    { Transaction = transaction };
update.Parameters.AddWithValue("amount", 12.50m);
update.Parameters.AddWithValue("id", 1);
await update.ExecuteNonQueryAsync();
await transaction.ReleaseAsync("before_update");
await transaction.CommitAsync();
```

The default/Unspecified explicitly requests ReadCommitted; RepeatableRead and Serializable send their exact PG isolation keywords. ReadUncommitted, Snapshot and Chaos reject without mapping to another level. The transaction's `IsolationLevel` records its Begin request, with Unspecified normalized to ReadCommitted; later application SQL can change server characteristics independently. A null Command.Transaction participates in the connection's current transaction; an explicit reference must be its active transaction. Close or a broken session invalidates that owner. A COMMIT acknowledged as ROLLBACK fails explicitly. `KingbaseTransactionException.OutcomeUnknown` and `KingbaseTransaction.OutcomeUnknown` identify uncertain commits; cancellation remains `OperationCanceledException` with uncertainty available on the transaction. Never automatically replay an uncertain transaction. Disposal attempts rollback within five seconds, then closes its still-owned connection if cleanup cannot synchronize. These paths have loopback evidence; server isolation semantics remain unaccepted.

For a transaction-scoped read-only request, use the explicit access-mode overload:

```csharp
using var readOnly = await connection.BeginTransactionAsync(
    System.Data.IsolationLevel.RepeatableRead, readOnly: true);
// Execute reads here. Read-only restrictions are enforced by the server.
await readOnly.CommitAsync();
```

The synchronous counterpart is `BeginTransaction(isolationLevel, readOnly)`. Explicit true/false sends READ ONLY/READ WRITE in the same BEGIN; existing overloads omit the access mode and honor the session's read-only default. The driver neither changes session defaults nor filters application SQL to simulate read-only enforcement. Real concurrent-read, serialization and write-rejection behavior still requires Kingbase acceptance.

Use `Save(name)`, `Rollback(name)` and `Release(name)` or their asynchronous counterparts for savepoints; `Rollback(name)` keeps the transaction active. Names preserve case and whitespace, escape double quotes, and reject empty/NUL/invalid Unicode or names exceeding the reported UTF-8 byte limit. The first managed Begin on each physical session reads `SHOW max_identifier_length` under the command deadline; that query and savepoint effects remain subject to real Kingbase acceptance. Repeated names and savepoints created through SQL are resolved by the server. Modern assets override the BCL savepoint APIs; Standard assets expose the same typed methods.

`KingbaseDataSourceBuilder.Build()` snapshots validated configuration. Its source creates independent connections with immutable configuration and redacted connection strings. Disposing the source rejects new opens and reopening, cancels pending opens, and lets already-open connections and Readers finish. Modern assets derive from `DbDataSource`; Standard assets expose typed creation/open methods.

Ordinary PG pooling now gives each DataSource a private pool; direct connections share pools with normalized configuration. Maximum Pool Size counts opening, leased, resetting and retiring sessions. One Open timeout covers FIFO waiting, initialization and idle validation. Close/CloseAsync revokes children, rolls back unfinished local transactions, issues DISCARD ALL, restores bound settings and verifies identity/catalog/state within Cleanup Timeout; failed cleanup retires the socket. Active interrupted commands retire their sessions. Clear with `KingbaseConnection.ClearPool(connection)`, `KingbaseConnection.ClearAllPools()` or `source.ClearPool()`; active leases finish and retire on return. Idle/total lifetime expiry and a lazy minimum are implemented. These paths have loopback evidence only; real resource cleanup, reusable profiles, transaction reservations and other backends remain unaccepted.

A DataSource can obtain fresh credentials through `builder.PasswordProvider`, a `Task<string>` delegate receiving immutable endpoint metadata and a cancellation token. Configure it without an explicit Password; Build and DI resolution remain lazy. Each new physical initialization invokes it under the Open budget, while idle reuse and ordinary reset do not. Clear the pool to retire existing sessions and fetch a new password on replacement. Concurrent callbacks must be supported; cancelled external work may finish later, but cannot open a socket or publish a lease. Provider failures use safe `KingbasePasswordProviderException` text without retaining the original exception. The existing DI builder configuration callback accepts this property too.
The separate `KingbaseConnector.DependencyInjection` package provides closed transient connections and lazy singleton DataSources:

```csharp
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddKingbaseDataSource(
    "Host=localhost;Database=app;Username=user;Password=secret");
using var provider = services.BuildServiceProvider();
var source = provider.GetRequiredService<KingbaseDataSource>();
using var connection = await source.OpenConnectionAsync();
```

Use `AddKingbaseConnection` for transient connections, or `AddKeyedKingbaseDataSource` for multiple keyed sources on modern assets. Unkeyed BCL aliases are added when absent; duplicate provider/key registrations reject atomically. The container owns resolved services. DI uses Microsoft.Extensions lower-bound ranges `[8.0.0,)` on net8.0, `[9.0.0,)` on net9.0, and `[10.0.0,)` on net10.0 and Standard assets. DataSources use private pools by default; direct DataSource commands remain future work.
