using System.Data.Common;
using KingbaseConnector.Protocol;
namespace KingbaseConnector;
public sealed class KingbaseException : DbException
{
    readonly PgServerError _error;
    internal KingbaseException(PgServerError error) : base($"Kingbase server error (SQLSTATE {error.SqlState}).") => _error = error;
#if NET8_0_OR_GREATER
    public override string SqlState => _error.SqlState;
#else
    public string SqlState => _error.SqlState;
#endif
    /// <summary>Raw server text. Treat as sensitive and avoid including it in logs.</summary>
    public string MessageText => _error.MessageText;
    /// <summary>Raw server detail, when supplied.</summary>
    public string? Detail => _error.Detail;
    /// <summary>Raw server hint, when supplied.</summary>
    public string? Hint => _error.Hint;
}
