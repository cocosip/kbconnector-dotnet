namespace KingbaseConnector.ProtocolTests;

public sealed class LargeObjectTests
{
    [Theory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ExactAssetLargeObjectProbe(KingbaseCompatibilityMode mode) => KingbaseConnector.Tests.LargeObjectAssetProbe.RunAsync(mode);

    [Fact]
    public void TransactionBoundManagerAndStreamExposeOidLargeObjectOperations()
    {
        var assembly = typeof(KingbaseConnection).Assembly; var manager = assembly.GetType("KingbaseConnector.KingbaseLargeObjectManager"); Assert.NotNull(manager);
        Assert.NotNull(manager.GetConstructor([typeof(KingbaseConnection)]));
        foreach (var name in new[] { "Create", "CreateAsync", "OpenRead", "OpenReadAsync", "OpenReadWrite", "OpenReadWriteAsync", "Unlink", "UnlinkAsync" }) Assert.Contains(manager.GetMethods(), method => method.Name == name);
        var stream = assembly.GetType("KingbaseConnector.KingbaseLargeObjectStream"); Assert.NotNull(stream); Assert.True(typeof(Stream).IsAssignableFrom(stream)); Assert.NotNull(stream.GetProperty("Oid"));
    }
}
