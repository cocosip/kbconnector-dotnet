using System.Reflection;
using System.Runtime.Versioning;
using System.Data;
using System.Data.Common;
using KingbaseConnector;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 2)
    throw new ArgumentException("Supply the expected package asset framework name and project version.");

var assembly = Assembly.Load("KingbaseConnector");
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(adapterBatch: true);
Console.WriteLine("PASS: DataAdapter batching snapshots, partial acknowledgments, unsent rows and continued groups");
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(commandBuilder: true);
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(commandBuilder: true, automatic: true);
Console.WriteLine("PASS: PG CommandBuilder quoted CRUD, catalog types, NULL flags and base-typed schema refresh");
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(adapter: true);
Console.WriteLine("PASS: DataAdapter Fill, typed row events, returned updates and optimistic concurrency");
if (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0] != args[1])
    throw new InvalidOperationException("The package must match the centrally managed project version.");
var actual = assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
if (actual != args[0])
    throw new InvalidOperationException($"Expected {args[0]}, loaded {actual} from {assembly.Location}.");

var builder = new KingbaseConnectionStringBuilder("Server=localhost;Database=app;User ID=user;Pwd=\"secret;value\"");
builder.Validate();
if (builder.WireProtocol != KingbaseWireProtocol.PostgreSql || builder.Port != 54321 ||
    builder.SslMode != KingbaseSslMode.VerifyFull || builder.Host != "localhost" ||
    builder.Password != "secret;value" || builder.ToString().Contains("secret"))
    throw new InvalidOperationException("The selected asset failed its public configuration contract.");
builder.WireProtocol = KingbaseWireProtocol.MySql;
try
{
    builder.Validate();
    throw new InvalidOperationException("MySQL wire accepted an implicit port.");
}
catch (ArgumentException) { }
builder.Port = 3307;
builder.Validate();

var numeric = KingbaseNumeric.Parse("1234567890123456789012345678901234567890.00100");
if (numeric.ToString() != "1234567890123456789012345678901234567890.00100" || numeric.Scale != 5 ||
    KingbaseNumeric.FromDecimal(1.2300M).ToDecimal() != 1.2300M ||
    KingbaseNumeric.Parse("1.0") != KingbaseNumeric.Parse("1.00"))
    throw new InvalidOperationException("The selected asset failed its exact numeric contract.");
try
{
    KingbaseNumeric.Parse("1.00000000000000000000000000001").ToDecimal();
    throw new InvalidOperationException("The selected asset silently rounded an exact numeric.");
}
catch (OverflowException) { }

var parameters = new KingbaseParameterCollection();
var parameter = parameters.AddWithValue("@amount", 1.2300m);
parameter.KingbaseDbType = KingbaseDbType.Numeric;
parameter.DbType = DbType.Decimal;
parameter.DataTypeName = "pg_catalog.numeric";
parameter.Scale = 2;
DbParameterCollection bclParameters = parameters;
if (!ReferenceEquals(bclParameters[":AMOUNT"], parameter) || parameter.DbType != DbType.Decimal ||
    parameter.SourceVersion != DataRowVersion.Current || parameters.Count != 1)
    throw new InvalidOperationException("The selected asset failed its public parameter contract.");
var otherParameters = new KingbaseParameterCollection();
try
{
    otherParameters.Add(parameter);
    throw new InvalidOperationException("A parameter was accepted by two collections.");
}
catch (ArgumentException) { }
parameters.Clear();
otherParameters.Add(parameter);
parameter.ResetDbType();
if (parameter.DataTypeName is not null || parameter.DbType != DbType.Decimal || parameter.Scale != 2 ||
    !Equals(parameter.Value, 1.2300m))
    throw new InvalidOperationException("The selected asset failed to preserve parameter metadata on reset.");

if (!typeof(DbDataReader).IsAssignableFrom(typeof(KingbaseDataReader)) ||
    !typeof(IDbColumnSchemaGenerator).IsAssignableFrom(typeof(KingbaseDataReader)) ||
    typeof(KingbaseDataReader).GetConstructors().Length != 0)
    throw new InvalidOperationException("The selected asset failed its buffered reader API shape contract.");

DbProviderFactory factory = KingbaseFactory.Instance;
var modernBatchAsset = actual.StartsWith(".NETCoreApp", StringComparison.Ordinal);
if (typeof(DbBatch).IsAssignableFrom(typeof(KingbaseBatch)) != modernBatchAsset ||
    typeof(DbBatchCommand).IsAssignableFrom(typeof(KingbaseBatchCommand)) != modernBatchAsset ||
    typeof(DbBatchCommandCollection).IsAssignableFrom(typeof(KingbaseBatchCommandCollection)) != modernBatchAsset)
    throw new InvalidOperationException("The selected asset failed its batch framework API shape contract.");
var passwordProviderCalls = 0;
var passwordBuilder = new KingbaseDataSourceBuilder("Host=localhost;Database=app;Username=user")
{
    PasswordProvider = (KingbasePasswordContext context, CancellationToken token) =>
    { passwordProviderCalls++; return Task.FromResult("runtime-password"); }
};
using (var passwordSource = passwordBuilder.Build())
{
    using var passwordConnection = passwordSource.CreateConnection();
    if (passwordProviderCalls != 0 || passwordConnection.State != ConnectionState.Closed)
        throw new Exception("Password providers must remain lazy until physical creation.");
}
using (var closedPoolConnection = new KingbaseConnection())
{
    await closedPoolConnection.CloseAsync();
    KingbaseConnection.ClearPool(closedPoolConnection);
    await closedPoolConnection.DisposeAsync();
}
using (var pooledSource = new KingbaseDataSourceBuilder("Host=localhost;Database=app;Username=user").Build())
{
    using var pooledConnection = pooledSource.CreateConnection();
    pooledSource.ClearPool(); KingbaseConnection.ClearPool(pooledConnection); KingbaseConnection.ClearAllPools();
    if (pooledConnection.State != ConnectionState.Closed) throw new Exception("Pool creation must be lazy.");
}
if (!typeof(DbTransaction).IsAssignableFrom(typeof(KingbaseTransaction)) || typeof(KingbaseTransaction).GetConstructors().Length != 0)
    throw new InvalidOperationException("The selected asset failed its local transaction API shape contract.");
using (var closedTransactionConnection = new KingbaseConnection())
{
    try { closedTransactionConnection.BeginTransaction(); throw new Exception("A closed connection began a transaction."); }
    catch (InvalidOperationException) { }
    try { await closedTransactionConnection.BeginTransactionAsync(); throw new Exception("A closed connection began an async transaction."); }
    catch (InvalidOperationException) { }
    try { await closedTransactionConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted, default); throw new Exception("A default-token call began a closed transaction."); }
    catch (InvalidOperationException) { }
    try { closedTransactionConnection.BeginTransaction(IsolationLevel.Snapshot); throw new Exception("An unsupported isolation was accepted."); }
    catch (NotSupportedException) { }
    foreach (var isolation in new[] { IsolationLevel.Unspecified, IsolationLevel.ReadCommitted, IsolationLevel.RepeatableRead, IsolationLevel.Serializable })
    {
        foreach (var readOnly in new[] { true, false })
        {
            try { closedTransactionConnection.BeginTransaction(isolation, readOnly); throw new Exception("A closed connection began an explicit access-mode transaction."); }
            catch (InvalidOperationException) { }
            try { await closedTransactionConnection.BeginTransactionAsync(isolation, readOnly); throw new Exception("A closed connection began an async access-mode transaction."); }
            catch (InvalidOperationException) { }
        }
    }
    using var transactionCommand = closedTransactionConnection.CreateCommand();
    transactionCommand.Transaction = null;
    if (transactionCommand.Transaction is not null) throw new Exception("A null transaction was not retained.");
    // Exercise the typed Standard counterparts too; full wire behavior is covered by protocol tests.
    using var inactive = (KingbaseTransaction)Activator.CreateInstance(typeof(KingbaseTransaction),
        BindingFlags.Instance | BindingFlags.NonPublic, null, [closedTransactionConnection, IsolationLevel.ReadCommitted], null)!;
    if (!inactive.SupportsSavepoints) throw new Exception("Savepoint capability is missing.");
    foreach (Action action in new Action[] { () => inactive.Save("s"), () => inactive.Rollback("s"), () => inactive.Release("s") })
    {
        try { action(); throw new Exception("An inactive transaction executed a savepoint."); }
        catch (InvalidOperationException) { }
    }
    foreach (Func<Task> action in new Func<Task>[] { () => inactive.SaveAsync("s"), () => inactive.RollbackAsync("s"), () => inactive.ReleaseAsync("s") })
    {
        try { await action(); throw new Exception("An inactive transaction executed an async savepoint."); }
        catch (InvalidOperationException) { }
    }
}
using DbConnection connection = factory.CreateConnection()!;
connection.ConnectionString = "Host=localhost;Database=app;Username=user;Password=private;Pooling=false;Command Timeout=7";
using DbCommand command = connection.CreateCommand();
command.CommandText = "SELECT :n";
command.Parameters.Add(command.CreateParameter());
if (connection is not KingbaseConnection || command is not KingbaseCommand ||
    !ReferenceEquals(DbProviderFactories.GetFactory(connection), factory) ||
    connection.State != ConnectionState.Closed || connection.Database != "app" ||
    command.CommandTimeout != 7 || command.Parameters[0] is not KingbaseParameter ||
    factory.CreateCommand() is not KingbaseCommand || factory.CreateConnectionStringBuilder() is not KingbaseConnectionStringBuilder)
    throw new InvalidOperationException("The selected asset failed its public execution/factory contract.");
command.CommandTimeout = int.MaxValue;
try
{
    command.ExecuteScalar();
    throw new InvalidOperationException("The selected asset executed a closed connection.");
}
catch (InvalidOperationException error) when (error.Message.Contains("not open")) { }
connection.Dispose();
try
{
    connection.Open();
    throw new InvalidOperationException("The selected asset reopened a disposed connection.");
}
catch (ObjectDisposedException) { }

var diAssembly = Assembly.Load("KingbaseConnector.DependencyInjection");
if (diAssembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName != args[0])
    throw new InvalidOperationException("The DI asset does not match the explicitly selected core asset.");
var sourceBuilder = new KingbaseDataSourceBuilder("Host=localhost;Database=app;Username=user;Password=private;Pooling=false");
using var source = sourceBuilder.Build();
sourceBuilder.ConnectionStringBuilder.Database = "changed";
using var owned = source.CreateConnection();
if (owned.Database != "app" || source.ConnectionString.Contains("private") || owned.ConnectionString.Contains("private"))
    throw new InvalidOperationException("The selected asset failed immutable/redacted DataSource configuration.");
var modern = args[0].StartsWith(".NETCoreApp", StringComparison.Ordinal);
if (((object)source is DbDataSource) != modern)
    throw new InvalidOperationException("The selected asset has an incorrect DataSource BCL base.");
var services = new ServiceCollection();
services.AddKingbaseConnection("Host=localhost;Database=app;Username=user;Pooling=false");
services.AddKingbaseDataSource("Host=localhost;Database=app;Username=user;Pooling=false");
var container = services.BuildServiceProvider();
using var scope = container.CreateScope();
var scopedConnection = scope.ServiceProvider.GetRequiredService<KingbaseConnection>();
var aliasConnection = scope.ServiceProvider.GetRequiredService<DbConnection>();
var singleton = container.GetRequiredService<KingbaseDataSource>();
if (ReferenceEquals(scopedConnection, aliasConnection) || aliasConnection is not KingbaseConnection ||
    scopedConnection.State != ConnectionState.Closed || !ReferenceEquals(singleton, scope.ServiceProvider.GetRequiredService<KingbaseDataSource>()))
    throw new InvalidOperationException("The selected asset failed DI connection/source lifetimes.");
if (modern && !ReferenceEquals(singleton, container.GetRequiredService<DbDataSource>()))
    throw new InvalidOperationException("The selected asset failed its BCL DataSource alias.");
var keyed = typeof(KingbaseServiceCollectionExtensions).GetMethod("AddKeyedKingbaseDataSource");
if ((keyed is not null) != modern)
    throw new InvalidOperationException("The selected asset has an incorrect keyed DI API surface.");
scope.Dispose(); container.Dispose(); source.Dispose();
try
{
    scopedConnection.Open();
    throw new InvalidOperationException("A container-owned connection survived scope disposal.");
}
catch (ObjectDisposedException) { }
try
{
    singleton.CreateConnection();
    throw new InvalidOperationException("A container-owned source survived container disposal.");
}
catch (ObjectDisposedException) { }
try
{
    await owned.OpenAsync();
    throw new InvalidOperationException("A disposed source allowed a new connection open.");
}
catch (ObjectDisposedException) { }

await KingbaseConnector.Tests.TlsAssetProbe.RunAsync();
Console.WriteLine($"PASS: {actual} custom CA/name/expiry/EKU/intermediate/IP TLS policy on .NET {Environment.Version}");
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync();
foreach (var mode in new[] { KingbaseCompatibilityMode.Oracle, KingbaseCompatibilityMode.MySql, KingbaseCompatibilityMode.SqlServer })
{
    await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(mode: mode);
    await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(mode: mode, automatic: true);
}
Console.WriteLine("PASS: Oracle/MySQL/SQL Server mode PG-wire unpooled explicit/automatic preparation");
foreach (var mode in new[] { KingbaseCompatibilityMode.PostgreSql, KingbaseCompatibilityMode.Oracle, KingbaseCompatibilityMode.MySql, KingbaseCompatibilityMode.SqlServer })
    await KingbaseConnector.Tests.PoolAssetProbe.RunAsync(mode);
Console.WriteLine("PASS: four-mode private pool reset, bound baseline, fresh catalog and stale preparation invalidation");
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(automatic: true);
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(date: true);
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(time: true);
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(timestamp: true);
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(interval: true);
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(batch: true);
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(xml: true);
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(schema: true);
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(readerSchema: true);
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(schemaOnly: true);
await KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(keyInfo: true);
Console.WriteLine($"PASS: {actual} KeyInfo bound catalog origins, base names, keys, identity/nullability and SchemaOnly/prepared ownership on .NET {Environment.Version}");
Console.WriteLine($"PASS: {actual} command/batch SchemaOnly without Bind/Execute, affected counts or executed statuses on .NET {Environment.Version}");
Console.WriteLine($"PASS: {actual} Reader character lengths and numeric precision/signed scale, NULL/empty results and BCL schema agreement on .NET {Environment.Version}");
Console.WriteLine($"PASS: {actual} twenty-seven canonical schema collections plus two aliases, 87 restrictions, UDT/routine/database/user/constraint/generation/sequence/relational facts and actual type OIDs on .NET {Environment.Version}");
Console.WriteLine($"PASS: {actual} XML OID binding, preparation, NULL, buffered text and server-error recovery on .NET {Environment.Version}");
Console.WriteLine($"PASS: {actual} sequential batch preparation, parameter/result ordering and partial-success reporting on .NET {Environment.Version}");
Console.WriteLine($"PASS: {actual} INTERVAL calendar/clock fields, four styles, typed NULL and exact TimeSpan access on .NET {Environment.Version}");
Console.WriteLine($"PASS: {actual} TIMESTAMP/TIMESTAMPTZ microseconds, Kind, UTC, BC and infinity on .NET {Environment.Version}");
Console.WriteLine($"PASS: {actual} TIME/TIMETZ binding, microseconds, end-of-day and second offsets on .NET {Environment.Version}");
Console.WriteLine($"PASS: {actual} DATE binding, lossless BC/infinity Reader values and CLR conversion on .NET {Environment.Version}");
Console.WriteLine($"PASS: {actual} explicit/automatic preparation, Unprepare and named execution on .NET {Environment.Version}");
Console.WriteLine($"PASS: {actual} on .NET {Environment.Version}; {assembly.Location}");
