using System.Data.Common;

namespace KingbaseConnector;

/// <summary>Provides a password for one new physical connection. Implementations must support concurrent calls.</summary>
public delegate Task<string> KingbasePasswordProvider(KingbasePasswordContext context, CancellationToken cancellationToken);

/// <summary>Immutable, non-secret endpoint metadata supplied to a password provider.</summary>
public sealed class KingbasePasswordContext
{
    internal KingbasePasswordContext(KingbaseConnectionStringBuilder settings)
    { Host = settings.Host; Port = settings.Port; Database = settings.Database; Username = settings.Username; WireProtocol = settings.WireProtocol; }
    public string Host { get; }
    public int Port { get; }
    public string Database { get; }
    public string Username { get; }
    public KingbaseWireProtocol WireProtocol { get; }
}

/// <summary>A password provider failed or returned unusable credentials. Sensitive provider errors are not retained.</summary>
public sealed class KingbasePasswordProviderException : DbException
{
    internal KingbasePasswordProviderException() : base("The Kingbase password provider failed or returned invalid credentials.") { }
}
