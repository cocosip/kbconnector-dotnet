namespace KingbaseConnector.ProtocolTests;

public sealed class CommandBuilderExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task QuotedAliasedCrudUsesCatalogTypesAndOriginalNullFlags(bool columnNames) =>
        KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(commandBuilder: true, automatic: columnNames);
}
