using KingbaseConnector;
using System.Data.Common;

namespace Microsoft.Extensions.DependencyInjection;

public static class KingbaseServiceCollectionExtensions
{
    /// <summary>Registers closed transient connections owned by the resolving scope.</summary>
    public static IServiceCollection AddKingbaseConnection(this IServiceCollection services, string connectionString)
    {
        var configuration = Validate(services, connectionString);
        if (Has(services, typeof(KingbaseConnection), null)) throw Conflict();
        var addAlias = !Has(services, typeof(DbConnection), null);
        services.AddTransient(_ => new KingbaseConnection(configuration));
        if (addAlias) services.AddTransient<DbConnection>(sp => sp.GetRequiredService<KingbaseConnection>());
        return services;
    }

    /// <summary>Registers one lazily built DataSource owned by the container.</summary>
    public static IServiceCollection AddKingbaseDataSource(this IServiceCollection services, string connectionString,
        Action<IServiceProvider, KingbaseDataSourceBuilder>? configure = null)
    {
        var configuration = Validate(services, connectionString);
        if (Has(services, typeof(KingbaseDataSource), null)) throw Conflict();
#if NET8_0_OR_GREATER
        var addAlias = !Has(services, typeof(DbDataSource), null);
#endif
        services.AddSingleton(sp => Build(sp, configuration, configure));
#if NET8_0_OR_GREATER
        if (addAlias) services.AddSingleton<DbDataSource>(sp => sp.GetRequiredService<KingbaseDataSource>());
#endif
        return services;
    }

#if NET8_0_OR_GREATER
    /// <summary>Registers a private singleton DataSource and its BCL alias for a concrete key.</summary>
    public static IServiceCollection AddKeyedKingbaseDataSource(this IServiceCollection services, object serviceKey, string connectionString,
        Action<IServiceProvider, KingbaseDataSourceBuilder>? configure = null)
    {
        var configuration = Validate(services, connectionString);
        if (serviceKey is null) throw new ArgumentNullException(nameof(serviceKey));
        if (Equals(serviceKey, KeyedService.AnyKey)) throw new ArgumentException("A concrete service key is required.", nameof(serviceKey));
        if (Has(services, typeof(KingbaseDataSource), serviceKey) || Has(services, typeof(DbDataSource), serviceKey)) throw Conflict();
        services.AddKeyedSingleton<KingbaseDataSource>(serviceKey, (sp, _) => Build(sp, configuration, configure));
        services.AddKeyedSingleton<DbDataSource>(serviceKey, (sp, key) => sp.GetRequiredKeyedService<KingbaseDataSource>(key));
        return services;
    }
#endif

    static KingbaseDataSource Build(IServiceProvider services, string configuration,
        Action<IServiceProvider, KingbaseDataSourceBuilder>? configure)
    {
        var builder = new KingbaseDataSourceBuilder(configuration);
        configure?.Invoke(services, builder);
        return builder.Build();
    }

    static bool Has(IServiceCollection services, Type type, object? key) => services.Any(d =>
        d.ServiceType == type && (key is null ? !d.IsKeyedService : d.IsKeyedService && Equals(d.ServiceKey, key)));
    static InvalidOperationException Conflict() => new("A Kingbase service is already registered for this provider/key.");
    static string Validate(IServiceCollection services, string connectionString)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A connection string is required.", nameof(connectionString));
        return new KingbaseConnectionStringBuilder(connectionString).ConnectionString;
    }
}
