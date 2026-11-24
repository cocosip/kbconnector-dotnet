using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>Builds an immutable configuration owner for unpooled PG connections.</summary>
public sealed class KingbaseDataSourceBuilder
{
    public KingbaseDataSourceBuilder(string connectionString = "") => ConnectionStringBuilder = new(connectionString);
    public KingbaseConnectionStringBuilder ConnectionStringBuilder { get; }
    public KingbaseDataSource Build()
    {
        var snapshot = new KingbaseConnectionStringBuilder(ConnectionStringBuilder.ConnectionString);
        ValidateExecutionSettings(snapshot);
        PgPhysicalSession.CreateStartupMessage(snapshot);
        return new KingbaseDataSource(snapshot.ConnectionString);
    }

    internal static void ValidateExecutionSettings(KingbaseConnectionStringBuilder settings)
    {
        settings.Validate();
        if (settings.Pooling) throw new NotSupportedException("This execution slice requires Pooling=false; built-in pooling is not yet implemented.");
        if (settings.WireProtocol != KingbaseWireProtocol.PostgreSql)
            throw new NotSupportedException("The public execution slice requires PostgreSql wire protocol.");
        if (settings.CompatibilityMode is not KingbaseCompatibilityMode.Auto and not KingbaseCompatibilityMode.PostgreSql)
            throw new NotSupportedException("The public execution slice requires PostgreSql compatibility mode.");
        if (settings.RootCertificate.Length != 0)
            throw new NotSupportedException("Custom root certificates are not yet supported by the PG transport.");
    }
}
