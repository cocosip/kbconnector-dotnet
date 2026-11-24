using System.Reflection;
using System.Runtime.Versioning;
using System.Data;
using System.Data.Common;
using KingbaseConnector;

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

Console.WriteLine($"PASS: {actual} on .NET {Environment.Version}; {assembly.Location}");
