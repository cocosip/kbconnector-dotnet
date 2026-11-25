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

For PG XML, select `KingbaseDbType.Xml`, `DbType.Xml`, or `DataTypeName="pg_catalog.xml"` and supply a string or typed NULL. Ordinary strings continue to infer text. `DbType.String` can constrain the XML string representation; `DbType.Xml` requires XML. The actual XML catalog entry must have the supported base layout; missing XML rejects explicitly without making other types unavailable.

XML results use `String` through `GetString`, `GetChars` and buffered `GetTextReader`. The driver preserves returned text without parsing documents or resolving entities. Input/output use the confirmed UTF8 protocol encoding; an embedded XML declaration does not select another client encoding. Server validation, document/content settings and server serialization still apply. XML objects, `XmlReader`/`TextReader` input and binary XML are unsupported. Existing 1 MiB text/exchange limits apply; `GetTextReader` does not provide network streaming. The [live test guide](docs/live-testing.md) records the bounded PG acceptance.

`KingbaseConnection`, `KingbaseCommand` and `KingbaseFactory.Instance` expose the initial public PG execution path. It currently requires PostgreSql compatibility mode. Default `Pooling=true` rents ordinary PG sessions; `Pooling=false` creates dedicated sessions. Open confirms product identity and required catalog types; text commands bind values separately from SQL. Cancellation or an interrupted exchange closes the physical session; ordinary synchronized server errors allow reuse. `KingbaseException` exposes SQLSTATE safely; its explicit server text properties may contain sensitive data.

On an open PG connection, `GetSchema()` and `GetSchemaAsync()` expose the five standard collections (`MetaDataCollections`, `Restrictions`, `DataSourceInformation`, `DataTypes`, `ReservedWords`), five object collections (`Schemas`, `Tables`, `Views`, `Columns`, `Sequences`), eight relational collections (`PrimaryKeys`, `UniqueKeys`, `ForeignKeys`, `ForeignKeyColumns`, `Indexes`, `IndexColumns`, `Constraints`, `ConstraintColumns`), two directories (`Databases`, `Users`), three routine collections (`Functions`, `Procedures`, `ProcedureParameters`) and four type collections (`UserDefinedTypes`, `TypeAttributes`, `EnumLabels`, `DomainConstraints`). Collection names are case-insensitive; null selects the 29-entry directory, including two aliases. The standard five have zero restrictions. `Restrictions` describes the collection filters and their one-based positions. Empty/unknown collection names or excess restriction values throw `ArgumentException`. Every call returns a fresh invariant-culture table and requires the connection to be free of active commands/Readers.

`DataTypes` lists the current session's cataloged, implemented scalar mappings, with actual unsigned `TypeOid` and `ProviderSpecificDataType` extension columns. Unsupported layouts and absent optional types are omitted. Unknown size, scale, operator and generation facts remain `DBNull`. `DataSourceInformation` uses the authenticated product identity and driver parameter-name rules; its normalized product version and unconfirmed SQL capabilities remain `DBNull`. `ReservedWords` queries the trusted system function using normal command timeout/cancellation and transaction ownership.

`Schemas`, `Tables`, `Views` and `Columns` use permission-filtered `information_schema` views. Restrictions are ordered as `Schemas(Catalog,Schema)`, `Tables(Catalog,Schema,Table,TableType)`, `Views(Catalog,Schema,Table)`, and `Columns(Catalog,Schema,Table,Column)`. Omitted/null entries impose no filter; empty strings match empty names. Values match exact case-sensitive names, with literal `%`, `_` and quotes, using bound parameters. `Tables` includes the server's reported table kinds, including views; `Views` also reports nullable definitions and an `IS_UPDATABLE` Boolean. `Columns` reports ordinal, type/UDT/domain/collation identity, nullable defaults, an `IS_NULLABLE` Boolean and server-derived lengths/precision/scale. Inapplicable or hidden facts stay `DBNull`. The existing 1,024-row exchange limit applies without truncation; use restrictions for large catalogs.

Relational collections report catalog facts for visible owning tables, excluding other sessions' temporary tables. `PrimaryKeys`/`UniqueKeys` return one row per ordered constraint column; `ForeignKeys` reports actions, deferral and the actual referenced unique index, while `ForeignKeyColumns` preserves ordered source/target pairs. Restrictions are `(Catalog,Schema,Table,Constraint)`, with a fifth `Column` for `ForeignKeyColumns`. `Indexes`/`IndexColumns` use `(Catalog,Schema,Table,Index[,Column])`. Index metadata preserves expressions, predicates, INCLUDE columns, collation and operator-class identity. Expression columns have a null `COLUMN_NAME`; sort/null-order flags are known only for btree key columns and stay `DBNull` for INCLUDE columns and other methods. Referenced-table privileges are not implied by visible foreign-key metadata. `Constraints(Catalog,Schema,Table,Constraint)` reports table constraint codes, known type names, deferral/validation/NO INHERIT flags and server definitions. `ConstraintColumns` appends a Column restriction and reports the catalog column slots in numeric order, retaining repeated columns and gaps for omitted expression slots. Constant CHECK constraints have no column rows. Unknown codes keep their raw value and a null friendly type; column NOT NULL attributes never synthesize constraint rows. Definitions are preserved without parsing or execution. Domain constraints are reported separately by `DomainConstraints`.

`Sequences(Catalog,Schema,Sequence)` includes accessible ordinary and identity-owned sequences, excluding other sessions' temporary objects. It reports type/precision, signed `Int64` start/minimum/maximum/increment, cycle and cache size without reading or advancing sequence state. `Columns` also reports `IS_IDENTITY`, identity generation/options, `IS_GENERATED` and `GENERATION_EXPRESSION`. A `nextval` or other default stays a default; generation attributes come from catalog facts. Identity values use signed `Int64`; inapplicable identity cycle and unavailable generation facts remain `DBNull`. `IS_GENERATED` preserves the server's `ALWAYS`/`NEVER` text. The case-insensitive aliases `Schemata` and `PrimaryKey` return canonical `Schemas` and `PrimaryKeys` tables with the same restrictions; both aliases are discoverable in `MetaDataCollections` and `Restrictions` with canonical restriction order. Reader metadata enrichment and other compatibility modes remain pending.

`Databases(Database)` lists readable cluster catalog rows, including templates and databases with connections disabled. It reports owner, encoding, collation/ctype, template/availability flags, signed `Int32` connection limit and a separate `HAS_CONNECT_PRIVILEGE` flag; a CONNECT grant does not establish login success. `Users(User)` lists login roles from the public role view with declared superuser/inheritance/create-role/create-database/replication/RLS-bypass flags and connection limits. Passwords, hashes, ACLs, expiration and role configuration are excluded. These collections use the same exact bound filters and exchange limits. Negative server limit sentinels are preserved. The directory contains 27 canonical collections plus two aliases, with 87 restriction descriptors.

`Functions` and `Procedures` use `(Catalog,Schema,Routine,SpecificName)` restrictions; `ProcedureParameters` adds `Parameter` and covers both kinds. Ordinary functions/procedures retain overloads through opaque `SPECIFIC_NAME` identities; aggregates/window functions are excluded. Return type/UDT, language, set-returning/strictness/security attributes and input/default counts come from server catalog facts. Parameter rows preserve numeric declared slots, unnamed names, IN/OUT/INOUT modes and distinct VARIADIC/TABLE flags. Scalar returns have no synthetic parameter row. Unknown type limits and hidden default text stay `DBNull`; a null default does not establish that no default exists. These collections use permission-filtered views, exclude other sessions' temporary objects, and share exact filters and the 1,024-row exchange limit. Restrict large catalogs. Discovery does not execute routines or enable `CommandType.StoredProcedure`/`DeriveParameters`; those APIs and package semantics remain pending.

`UserDefinedTypes(Catalog,Schema,Type)` lists visible defined enums, domains and standalone composites, excluding automatic table/view row types and generated arrays. Domains report their base type, default, nullability and available length/precision/collation facts; other kinds leave those fields null. `TypeAttributes` appends an `Attribute` restriction and preserves declared member types and numeric positions, including gaps left by dropped attributes. `EnumLabels` appends `Label`; empty labels are valid, and numeric ordinals preserve server enum order after BEFORE/AFTER insertions. A label restriction retains its full-type ordinal. `DomainConstraints` appends `Constraint` and reports actual constraint flags and unparsed definitions; definitions require the owning role, while type USAGE alone can expose the remaining metadata. Domain NOT NULL does not create a synthetic constraint row. These collections exclude other sessions' temporary schemas and share exact bound filters and exchange limits. Type discovery does not enable enum/domain/composite value codecs or CLR mappings.

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

PG DATE maps to an Unspecified midnight `DateTime`. Use `KingbaseDbType.Date`, `DbType.Date` or `DataTypeName="pg_catalog.date"` for explicit binding; DateTime input must have no time component and Kind Unspecified. `KingbaseDate` is inferred as DATE on every asset; modern assets also infer `DateOnly` and support `GetFieldValue<DateOnly>`. Unspecified DateTime infers TIMESTAMP and UTC DateTime infers TIMESTAMPTZ; select DATE explicitly when needed. DATE results require confirmed ISO DateStyle (`ISO, MDY`, `ISO, DMY` or `ISO, YMD`); unsupported or unknown output styles reject conversion. Use application SQL to select a supported style when necessary.

`reader.GetFieldValue<KingbaseDate>(ordinal)` or `GetProviderSpecificValue` preserves BC dates, years outside the CLR range and explicit `infinity` / `-infinity`. Default DateTime/DateOnly conversion throws OverflowException for these values. For example, `KingbaseDate.Parse("0001-02-29 BC")` preserves a leap day in 1 BC; negative constructor years mean BC, with no year zero. The immutable value uses the proleptic Gregorian calendar, defaults to 0001-01-01 and accepts absolute years up to seven digits; each server still enforces its own range. No timezone conversion, date truncation or MinValue/MaxValue infinity substitution occurs.

PG TIME uses `TimeSpan` from zero through 24 hours inclusive at exact microsecond precision. Specify `DbType.Time`, `KingbaseDbType.Time` or `DataTypeName="pg_catalog.time"` for TimeSpan TIME input; otherwise TimeSpan infers INTERVAL. Modern assets infer `TimeOnly` and support `GetFieldValue<TimeOnly>`; reading `24:00:00` as TimeOnly throws OverflowException. Extra submicrosecond ticks reject instead of rounding. Numeric TIME/TIMETZ text does not depend on DateStyle or session TimeZone.

PG TIMETZ uses immutable `KingbaseTimeTz` as both its default and provider value, inferred on every asset. For example, `KingbaseTimeTz.Parse("12:34:56.123456+05:30:01")` preserves microseconds and an offset with seconds. Its `Time` is the wall-clock TimeSpan and `Offset` is signed east of UTC, in whole seconds with magnitude below 16 hours. Default is midnight at offset zero. Equality compares both components exactly; no date, local timezone or UTC normalization is introduced. Bind explicitly using `KingbaseDbType.TimeTz` or `pg_catalog.timetz`; DbType.Time is compatible with an explicit TIMETZ selection but selects TIME alone. TimeSpan/TimeOnly/DateTimeOffset cannot silently discard or manufacture the offset. Binary temporal formats and other modes' temporal types remain pending.

PG TIMESTAMP uses Unspecified `DateTime`; TIMESTAMPTZ uses UTC `DateTime`. Input inference follows Kind; Local DateTime rejects instead of applying the machine timezone or DST. DateTimeOffset infers TIMESTAMPTZ and normalizes its explicit offset to UTC. Select types explicitly using `KingbaseDbType.Timestamp`/`TimestampTz`, the corresponding `pg_catalog` type name, or `DbType.DateTime`/`DateTime2` (TIMESTAMP) and `DbType.DateTimeOffset` (TIMESTAMPTZ). All inputs require exact microseconds. TIMESTAMP finite clock values must be below 24 hours.

Use `GetFieldValue<KingbaseTimestamp>` or `GetFieldValue<KingbaseTimestampTz>` for lossless BC/out-of-CLR-year/infinity access. KingbaseTimestamp stores a finite `KingbaseDate` plus `TimeSpan`, or explicit infinity; KingbaseTimestampTz stores its UTC calendar timestamp in `UtcValue`. For example, `KingbaseTimestampTz.Parse("2024-03-01 00:00:00+01")` formats as `2024-02-29 23:00:00+00`. Both defaults represent 0001-01-01 midnight; the zoned value is UTC. Bounded proleptic Gregorian parsing preserves the BC/AD transition without year zero. Infinity and non-CLR conversions throw OverflowException. `GetFieldValue<DateTimeOffset>` applies only to TIMESTAMPTZ and returns offset zero. Strict ISO DateStyle confirmation is required for non-NULL timestamp results; TIMESTAMPTZ additionally requires numeric offset text and normalizes each value to UTC, independently of later session timezone changes. Non-ISO styles reject conversion until the application selects ISO.

PG INTERVAL uses `KingbaseInterval` as both its stable default and provider value. `new KingbaseInterval(14, -3, -1)` preserves independent months, days and clock microseconds and formats as `P14M-3DT-0.000001S`. It infers INTERVAL, as does exact-microsecond `TimeSpan`; select `KingbaseDbType.Interval` or `pg_catalog.interval` explicitly for typed NULL. Inferred or provider-enum selection reports `DbType.Object`; a NULL selected solely by DataTypeName retains the existing `DbType.String` fallback. Setting DbType.Object does not select a provider-specific interval type. `GetFieldValue<TimeSpan>` requires zero months and a combined day/clock duration within the CLR range; months are never guessed as a number of days. Provider access survives conversion failures.

INTERVAL results require a confirmed per-result IntervalStyle: postgres/kingbase, postgres_verbose/kingbase_verbose, sql_standard or iso_8601. Mixed signs and verbose `ago` are preserved independently of DateStyle, TimeZone and later style changes. Public Parse/TryParse accepts bounded ISO duration fields; extra fractional precision rejects. Client field bounds do not promise server text bounds: the tested server rejects full Int64 clock extremes (22015) and emits noncanonical minimum-day verbose output, which rejects safely; select ISO output for that boundary. Binary/full-range interval acceptance, Oracle YM/DS and other-mode aliases remain open.

Use `connection.CreateBatch()` or `new KingbaseBatch(connection, transaction)` for sequential PG statements. Modern assets derive from `DbBatch`; Standard assets expose the same typed API without that unavailable base class. Each `KingbaseBatchCommand` has its own parameters. One `Timeout` covers the entire operation (connection command timeout by default; zero disables it). `Prepare`/`PrepareAsync` prepares the statements sequentially; execution also honors opt-in automatic preparation. No implicit transaction or retry is introduced.

```csharp
using var batch = connection.CreateBatch();
var first = new KingbaseBatchCommand("SELECT @p");
first.Parameters.AddWithValue("p", 41);
batch.BatchCommands.Add(first);
batch.BatchCommands.Add(new KingbaseBatchCommand("SELECT 42"));
using var reader = await batch.ExecuteReaderAsync();
// Read each result in order with Read/NextResult.
```

Execution stops at the first error. `FailedCommandIndex` identifies that zero-based command; `KingbaseException.BatchCommand` identifies a server-rejected command. Inspect each command's `ExecutionStatus` and `RecordsAffected`: NotExecuted means it was never reached, Succeeded means its exchange was acknowledged, Failed means local validation/preparation or conclusive server rejection, and OutcomeUnknown means an Execute/Query frame was attempted without a conclusive outcome, including a synchronized SQLSTATE 40003. Successful execution does not imply transaction commit; caller-managed rollback remains explicit. An explicitly assigned transaction is checked before every statement; ending it stops subsequent execution. Earlier statements can persist when no explicit transaction is used. Each non-reentrant Execute resets counts/statuses before preflight validation, and acknowledged successes/counts remain visible if later metadata, buffering or CLR conversion fails. Preparation does not mark statements executed. Parameters are snapshotted when each statement is reached; concurrent mutation is unsupported.

A batch accepts at most 256 statements and buffers at most 256 results, 1024 rows, 1024 columns and 1 MiB of text/metadata across all statements. A post-acknowledgment capacity failure stops before the next statement. The Reader retains connection ownership until closed. ExecuteScalar validates the first scalar when reached, executes remaining statements if conversion succeeds, and returns that scalar; conversion failure identifies the acknowledged command and stops before later SQL. Reader getter conversion remains lazy. SingleResult/SingleRow restrict Reader visibility without skipping later execution. Streaming, other compatibility modes and pipelined execution remain pending.

PG commands support `Prepare()` / `PrepareAsync(token)` and `Unprepare()` / `UnprepareAsync(token)`:

```csharp
await command.PrepareAsync();
command.Parameters[0].Value = 2.50m;
var nextAmount = await command.ExecuteScalarAsync();
await command.UnprepareAsync();
```

Preparation validates current SQL and catalog-bound input types; typed NULL is supported. Value changes reuse the statement. SQL/type/lexical changes, eviction, pool reset or reconnection detach stale handles before execution. Unprepare closes the attached shared physical handle, so another command using it also detaches. Edits/disposal detach without network I/O; cached server resources stay within the physical session's 64-statement/1 MiB limit. Controls use CommandTimeout, caller cancellation and Cancel, and reject an active Reader. Server preparation/schema errors are returned without replay; callers can explicitly prepare a replacement. The live suite records bounded PG schema-error and transaction-recovery evidence; other modes and platforms remain unaccepted.

Automatic PG preparation is opt-in: set `Max Auto Prepare=16;Auto Prepare Min Usages=5` (or the builder properties `MaxAutoPrepare` / `AutoPrepareMinUsages`). The maximum defaults to 0 (disabled) and accepts 0..64; the usage threshold defaults to 5 and accepts 1..Int32.MaxValue. Successful unnamed protocol exchanges with the same SQL, ordered actual types and lexical state count per physical session, across command objects. The threshold execution prepares first; value changes reuse its handle. Candidates retain no values/schema and are bounded at 128 entries/1 MiB of encoded signatures, separately from the global prepared limits. Automatic capacity evicts the least recently used automatic statement with acknowledged Close. Manual Prepare promotes a shared automatic entry to explicit ownership; the global physical limit can still evict it. Unprepare of an attached handle, plan-invalidating errors and pool reset restart the relevant usage counts. Unprepare before a handle is attached is a no-op and retains candidate counts. An automatic handle attaches before execution, so Unprepare can close it even after its first execution returns a synchronized SQL error. A subsequent caller request may prepare again at its threshold; a failed request is never replayed. With automatic preparation disabled, detached execution uses ordinary binding until explicitly prepared again. Managed transaction controls remain unnamed. Sequential batch execution uses the same preparation cache.

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
