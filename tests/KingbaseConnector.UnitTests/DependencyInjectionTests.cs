using System.Data;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;

namespace KingbaseConnector.UnitTests;

public sealed class DependencyInjectionTests
{
    const string Settings = "Host=localhost;Database=app;Username=user;Password=private-secret;Pooling=false";

    [Fact]
    public void TransientConnectionsAndBclAliasesAreScopeOwned()
    {
        var services = new ServiceCollection(); services.AddKingbaseConnection(Settings);
        using var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var first = scope.ServiceProvider.GetRequiredService<KingbaseConnection>();
        var second = scope.ServiceProvider.GetRequiredService<DbConnection>();
        Assert.NotSame(first, second); Assert.IsType<KingbaseConnection>(second);
        Assert.Equal(ConnectionState.Closed, first.State);
        Assert.Equal("app", first.Database);
        scope.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Open());
        Assert.Throws<ObjectDisposedException>(() => second.Open());
    }

    [Fact]
    public void SourcesAreLazySingleInstancesWithBclAliasesAndContainerDisposal()
    {
        var configureCalls = 0;
        var services = new ServiceCollection();
        services.AddKingbaseDataSource(Settings, (sp, builder) =>
        {
            Assert.NotNull(sp.GetService<Marker>());
            configureCalls++; builder.ConnectionStringBuilder.Database = "configured";
        });
        services.AddSingleton<Marker>();
        var provider = services.BuildServiceProvider();
        Assert.Equal(0, configureCalls);
        var source = provider.GetRequiredService<KingbaseDataSource>();
        using (var scope = provider.CreateScope())
            Assert.Same(source, scope.ServiceProvider.GetRequiredService<KingbaseDataSource>());
        Assert.Same(source, provider.GetRequiredService<DbDataSource>());
        Assert.Equal(1, configureCalls);
        using var connection = source.CreateConnection(); Assert.Equal("configured", connection.Database);
        provider.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.CreateConnection());
    }

    [Fact]
    public void KeyedSourcesHaveIndependentSingletonLifetimesAndAliases()
    {
        var services = new ServiceCollection();
        services.AddKeyedKingbaseDataSource("one", Settings);
        services.AddKeyedKingbaseDataSource("two", Settings, (_, b) => b.ConnectionStringBuilder.Database = "two");
        var provider = services.BuildServiceProvider();
        var one = provider.GetRequiredKeyedService<KingbaseDataSource>("one");
        var two = provider.GetRequiredKeyedService<KingbaseDataSource>("two");
        Assert.Same(one, provider.GetRequiredKeyedService<DbDataSource>("one"));
        Assert.NotSame(one, two);
        using var connection = two.CreateConnection(); Assert.Equal("two", connection.Database);
        provider.Dispose();
        Assert.Throws<ObjectDisposedException>(() => one.CreateConnection());
        Assert.Throws<ObjectDisposedException>(() => two.CreateConnection());
    }

    [Fact]
    public void ExistingUnkeyedBclAliasesRemainIntact()
    {
        using var existingSource = new KingbaseDataSourceBuilder(Settings).Build();
        using var existingConnection = new KingbaseConnection();
        var services = new ServiceCollection();
        services.AddSingleton<DbConnection>(existingConnection);
        services.AddSingleton<DbDataSource>(existingSource);
        services.AddKingbaseConnection(Settings); services.AddKingbaseDataSource(Settings);
        using var provider = services.BuildServiceProvider();
        Assert.Same(existingConnection, provider.GetRequiredService<DbConnection>());
        Assert.Same(existingSource, provider.GetRequiredService<DbDataSource>());
        Assert.NotSame(existingSource, provider.GetRequiredService<KingbaseDataSource>());
    }

    [Fact]
    public void DuplicateRegistrationsAndInvalidKeysRejectAtomically()
    {
        var services = new ServiceCollection(); services.AddKingbaseConnection(Settings);
        var count = services.Count;
        Assert.Throws<InvalidOperationException>(() => services.AddKingbaseConnection(Settings));
        Assert.Equal(count, services.Count);
        services.AddKingbaseDataSource(Settings); count = services.Count;
        Assert.Throws<InvalidOperationException>(() => services.AddKingbaseDataSource(Settings)); Assert.Equal(count, services.Count);
        services.AddKeyedKingbaseDataSource("one", Settings); count = services.Count;
        Assert.Throws<InvalidOperationException>(() => services.AddKeyedKingbaseDataSource("one", Settings)); Assert.Equal(count, services.Count);
        services.AddKeyedSingleton<DbDataSource>("bcl", (_, _) => new KingbaseDataSourceBuilder(Settings).Build()); count = services.Count;
        Assert.Throws<InvalidOperationException>(() => services.AddKeyedKingbaseDataSource("bcl", Settings)); Assert.Equal(count, services.Count);
        Assert.Throws<ArgumentNullException>(() => services.AddKeyedKingbaseDataSource(null!, Settings));
        Assert.Throws<ArgumentException>(() => services.AddKeyedKingbaseDataSource(KeyedService.AnyKey, Settings)); Assert.Equal(count, services.Count);
        Assert.Throws<ArgumentException>(() => services.AddKingbaseConnection(" ")); Assert.Equal(count, services.Count);
        Assert.Throws<ArgumentNullException>(() => KingbaseServiceCollectionExtensions.AddKingbaseConnection(null!, Settings));
    }

    sealed class Marker { }
}
