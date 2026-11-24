using System.Data;
using System.Data.Common;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.UnitTests;

public sealed class DataSourceTests
{
    const string Settings = "Host=localhost;Database=app;Username=user;Password=private-secret;Pooling=false";

    [Fact]
    public void BuildSnapshotsConfigurationAndCreatesIndependentClosedConnections()
    {
        var builder = new KingbaseDataSourceBuilder(Settings);
        using var source = builder.Build();
        builder.ConnectionStringBuilder.Database = "changed";
        using var otherSource = builder.Build();
        Assert.DoesNotContain("private-secret", source.ConnectionString);
        Assert.DoesNotContain("private-secret", source.ToString());
        using var first = source.CreateConnection();
        using var second = source.CreateConnection();
        using var other = otherSource.CreateConnection();
        Assert.NotSame(first, second);
        Assert.Equal(ConnectionState.Closed, first.State);
        Assert.Equal("app", first.Database); Assert.Equal("changed", other.Database);
        Assert.DoesNotContain("private-secret", first.ConnectionString);
        Assert.Throws<InvalidOperationException>(() => first.ConnectionString = Settings);
        DbDataSource bcl = source;
        using var bclConnection = bcl.CreateConnection();
        Assert.IsType<KingbaseConnection>(bclConnection);
        Assert.Throws<NotSupportedException>(() => bcl.CreateCommand("SELECT 1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Host=localhost;Username=u;Database=d")]
    [InlineData("Host=localhost;Username=u;Database=d;Pooling=false;Compatibility Mode=Oracle")]
    [InlineData("Host=localhost;Username=u;Database=d;Pooling=false;Wire Protocol=MySql;Port=3307;Compatibility Mode=MySql")]
    public void BuildRejectsInvalidOrUnimplementedExecutionSettings(string settings)
    {
        if (settings.Length == 0) Assert.ThrowsAny<ArgumentException>(() => new KingbaseDataSourceBuilder(settings).Build());
        else Assert.Throws<NotSupportedException>(() => new KingbaseDataSourceBuilder(settings).Build());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceDisposalRejectsCreationAndOpeningExistingClosedConnections(bool asynchronous)
    {
        var source = new KingbaseDataSourceBuilder(Settings).Build();
        using var connection = source.CreateConnection();
        if (asynchronous) await source.DisposeAsync(); else source.Dispose();
        source.Dispose(); await source.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => source.CreateConnection());
        Assert.Throws<ObjectDisposedException>(() => source.OpenConnection());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.OpenConnectionAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.OpenAsync());
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void DisposedSourceRejectsPublicationWithoutInvokingCallback()
    {
        using var source = new KingbaseDataSourceBuilder(Settings).Build();
        source.Dispose();
        var published = false;
        Assert.Throws<ObjectDisposedException>(() => source.PublishOpen(() => published = true));
        Assert.False(published);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("application")]
    public void BuildPreflightsStartupTextBeforeProducingAnOwner(string field)
    {
        var builder = new KingbaseDataSourceBuilder(Settings);
        if (field == "password") builder.ConnectionStringBuilder.Password = "\uD800";
        else builder.ConnectionStringBuilder.ApplicationName = "\uD800";
        Assert.ThrowsAny<ArgumentException>(() => builder.Build());
    }

    [Fact]
    public void BuildPreservesAggregateStartupAndPasswordMessageBounds()
    {
        var builder = new KingbaseDataSourceBuilder(Settings);
        var part = new string('a', PgWire.MaxMessageLength / 2);
        builder.ConnectionStringBuilder.ApplicationName = part;
        builder.ConnectionStringBuilder.SearchPath = part;
        Assert.Throws<ArgumentException>(() => builder.Build());
        builder.ConnectionStringBuilder.ApplicationName = "";
        builder.ConnectionStringBuilder.SearchPath = "";
        builder.ConnectionStringBuilder.Password = new string('p', PgWire.MaxMessageLength - 4);
        Assert.Throws<ArgumentException>(() => builder.Build());
    }
}
