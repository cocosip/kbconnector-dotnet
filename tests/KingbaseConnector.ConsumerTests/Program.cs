using System.Reflection;
using System.Runtime.Versioning;
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

Console.WriteLine($"PASS: {actual} on .NET {Environment.Version}; {assembly.Location}");
