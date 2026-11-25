namespace KingbaseConnector.ProtocolTests;

public sealed class DataAdapterExecutionTests
{
    [Fact]
    public Task AdapterFillReturnedUpdatesAndConcurrencyUsePublicCommands() =>
        KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(adapter: true);
}
