using System.Reflection;
using System.Runtime.Versioning;

if (args.Length != 1)
    throw new ArgumentException("Supply the expected package asset framework name.");

var assembly = Assembly.Load("KingbaseConnector");
var actual = assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
if (actual != args[0])
    throw new InvalidOperationException($"Expected {args[0]}, loaded {actual} from {assembly.Location}.");

Console.WriteLine($"PASS: {actual} on .NET {Environment.Version}; {assembly.Location}");
