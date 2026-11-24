using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>Builds an immutable configuration owner for PG connections.</summary>
public sealed class KingbaseDataSourceBuilder
{
    public KingbaseDataSourceBuilder(string connectionString = "") => ConnectionStringBuilder = new(connectionString);
    public KingbaseConnectionStringBuilder ConnectionStringBuilder { get; }
    public KingbasePasswordProvider? PasswordProvider { get; set; }
    public KingbaseDataSource Build()
    {
        var passwordProvider = PasswordProvider;
        PgPasswordProvider.Validate(ConnectionStringBuilder, passwordProvider);
        var snapshot = new KingbaseConnectionStringBuilder(ConnectionStringBuilder.ConnectionString);
        ValidateExecutionSettings(snapshot);
        PgPhysicalSession.CreateStartupMessage(snapshot);
        return new KingbaseDataSource(snapshot.ConnectionString, passwordProvider);
    }

    internal static void ValidateExecutionSettings(KingbaseConnectionStringBuilder settings)
    {
        settings.Validate();
        if (settings.WireProtocol != KingbaseWireProtocol.PostgreSql)
            throw new NotSupportedException("The public execution slice requires PostgreSql wire protocol.");
        if (settings.CompatibilityMode is not KingbaseCompatibilityMode.Auto and not KingbaseCompatibilityMode.PostgreSql)
            throw new NotSupportedException("The public execution slice requires PostgreSql compatibility mode.");
        if (settings.RootCertificate.Length != 0)
            throw new NotSupportedException("Custom root certificates are not yet supported by the PG transport.");
    }
}
