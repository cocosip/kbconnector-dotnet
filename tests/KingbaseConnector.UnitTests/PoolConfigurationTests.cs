namespace KingbaseConnector.UnitTests;

public sealed class PoolConfigurationTests
{
    const string Configuration = "Host=LOCALHOST;Database=db;Username=user;Password=private-key";

    [Fact]
    public void PoolIdentityNormalizesAliasesOrderDefaultsEnumsAndDnsCasing()
    {
        var first = new KingbaseConnectionStringBuilder(Configuration);
        var second = new KingbaseConnectionStringBuilder("Pwd=private-key;User ID=user;Database=db;Server=localhost;Pooling=true;" +
            "Port=54321;Timeout=15;Client Encoding=utf-8;Wire Protocol=postgresql;SSL Mode=verifyfull");
        Assert.Equal(first.GetPoolKey(), second.GetPoolKey());
    }

    [Theory]
    [InlineData("Database", "DB")]
    [InlineData("Username", "USER")]
    [InlineData("Password", "changed-key")]
    [InlineData("CompatibilityMode", "PostgreSql")]
    [InlineData("SSL Mode", "Require")]
    [InlineData("Port", "54322")]
    [InlineData("Maximum Pool Size", "2")]
    [InlineData("Minimum Pool Size", "1")]
    [InlineData("Cleanup Timeout", "1")]
    [InlineData("Timeout", "1")]
    [InlineData("Search Path", "private_schema")]
    [InlineData("Timezone", "UTC")]
    public void MeaningfulIdentityAndPolicyDifferencesCannotMergePools(string keyword, string value)
    {
        var first = new KingbaseConnectionStringBuilder(Configuration);
        var different = new KingbaseConnectionStringBuilder(Configuration) { [keyword] = value };
        Assert.NotEqual(first.GetPoolKey(), different.GetPoolKey());
    }

    [Fact]
    public void ClearingNullRejectsAndUnpooledConfigurationIsANoop()
    {
        Assert.Throws<ArgumentNullException>(() => KingbaseConnection.ClearPool(null!));
        using var connection = new KingbaseConnection(Configuration + ";Pooling=false");
        KingbaseConnection.ClearPool(connection);
        using var source = new KingbaseDataSourceBuilder(Configuration + ";Pooling=false").Build();
        source.Dispose(); Assert.Throws<ObjectDisposedException>(() => source.ClearPool());
    }
}
