using System.Reflection;
using System.Runtime.Versioning;

namespace KingbaseConnector.UnitTests;

public class EngineeringTests
{
    [Fact]
    public void CoreAssemblyTargetsTheRunningHost()
    {
        var assembly = Assembly.Load("KingbaseConnector");
        Assert.Equal($".NETCoreApp,Version=v{Environment.Version.Major}.0",
            assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        Assert.Equal(new Version(0, 1, 0, 0), assembly.GetName().Version);
    }

    [Fact]
    public void CoreHasNoOfficialDriverOrEfRuntimeReference()
    {
        var references = Assembly.Load("KingbaseConnector").GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference =>
            reference.Name!.Contains("Kdbndp", StringComparison.OrdinalIgnoreCase) ||
            reference.Name.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase) ||
            reference.Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            reference.Name.Contains("MySqlConnector", StringComparison.OrdinalIgnoreCase));
    }
}
