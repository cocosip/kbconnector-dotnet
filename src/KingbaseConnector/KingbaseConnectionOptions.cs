namespace KingbaseConnector;

/// <summary>Selects the wire protocol independently of database compatibility mode.</summary>
public enum KingbaseWireProtocol
{
    PostgreSql,
    MySql
}

/// <summary>Selects client semantics; Auto requires verified server-mode discovery.</summary>
public enum KingbaseCompatibilityMode
{
    Auto,
    PostgreSql,
    Oracle,
    MySql,
    SqlServer
}

/// <summary>Controls encryption and server certificate verification.</summary>
public enum KingbaseSslMode
{
    /// <summary>Explicitly permits unencrypted transport.</summary>
    Disable,
    /// <summary>Requires encryption without verifying the server certificate.</summary>
    Require,
    /// <summary>Requires encryption, a trusted certificate chain and matching hostname.</summary>
    VerifyFull
}
