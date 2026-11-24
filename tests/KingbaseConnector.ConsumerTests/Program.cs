using System.Reflection;
using System.Runtime.Versioning;
using System.Data;
using System.Data.Common;
using KingbaseConnector;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 1)
    throw new ArgumentException("Supply the expected package asset framework name.");

var assembly = Assembly.Load("KingbaseConnector");
if (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0] != "0.1.0")
    throw new InvalidOperationException("The package must use the standard 0.1.0 version without a prerelease suffix.");
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
parameter.DataTypeName = "sys_catalog.numeric";
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

Console.WriteLine($"PASS: {actual} on .NET {Environment.Version}; {assembly.Location}");
