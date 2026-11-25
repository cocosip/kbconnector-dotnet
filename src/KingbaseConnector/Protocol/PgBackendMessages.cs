using System.Collections.ObjectModel;

namespace KingbaseConnector.Protocol;

internal enum PgTransactionStatus { Idle, InTransaction, FailedTransaction }

internal readonly struct PgBackendKey(int processId, int secretKey)
{
    internal int ProcessId { get; } = processId;
    internal int SecretKey { get; } = secretKey;
}

internal sealed class PgServerError(IReadOnlyDictionary<byte, string> fields)
{
    internal IReadOnlyDictionary<byte, string> Fields { get; } = fields;
    internal string SqlState => Fields[(byte)'C'];
    internal string MessageText => Fields[(byte)'M'];
    internal string? Detail => Fields.TryGetValue((byte)'D', out var value) ? value : null;
    internal string? Hint => Fields.TryGetValue((byte)'H', out var value) ? value : null;
    public override string ToString() => $"PG server error (SQLSTATE {SqlState}).";
}

internal static class PgBackendMessages
{
    internal static PgTransactionStatus ParseReadyForQuery(byte[] payload)
    {
        if (payload.Length != 1) throw InvalidMessage();
        return payload[0] switch
        {
            (byte)'I' => PgTransactionStatus.Idle,
            (byte)'T' => PgTransactionStatus.InTransaction,
            (byte)'E' => PgTransactionStatus.FailedTransaction,
            _ => throw InvalidMessage()
        };
    }

    internal static KeyValuePair<string, string> ParseParameterStatus(byte[] payload)
    {
        var offset = 0;
        var name = PgBinary.ReadCString(payload, ref offset);
        var value = PgBinary.ReadCString(payload, ref offset);
        if (name.Length == 0 || offset != payload.Length) throw InvalidMessage();
        return new KeyValuePair<string, string>(name, value);
    }

    internal static PgBackendKey ParseBackendKeyData(byte[] payload)
    {
        if (payload.Length != 8) throw InvalidMessage();
        return new PgBackendKey(PgBinary.ReadInt32(payload, 0), PgBinary.ReadInt32(payload, 4));
    }

    internal static KingbaseNotificationEventArgs ParseNotification(byte[] payload)
    {
        if (payload.Length < 7) throw InvalidMessage();
        var processId = PgBinary.ReadInt32(payload, 0); var offset = 4;
        var channel = PgBinary.ReadCString(payload, ref offset); var text = PgBinary.ReadCString(payload, ref offset);
        if (processId <= 0 || channel.Length == 0 || offset != payload.Length) throw InvalidMessage();
        return new(processId, channel, text);
    }

    internal static PgServerError ParseError(byte[] payload)
    {
        var offset = 0;
        var fields = new Dictionary<byte, string>();
        while (offset < payload.Length && payload[offset] != 0)
        {
            var code = payload[offset++];
            var value = PgBinary.ReadCString(payload, ref offset);
            if (fields.ContainsKey(code)) throw InvalidMessage();
            fields.Add(code, value);
        }
        if (offset != payload.Length - 1 || payload[offset] != 0 ||
            !fields.TryGetValue((byte)'C', out var sqlState) || !IsSqlState(sqlState) ||
            !fields.ContainsKey((byte)'M') ||
            (!fields.ContainsKey((byte)'S') && !fields.ContainsKey((byte)'V')))
            throw InvalidMessage();
        return new PgServerError(new ReadOnlyDictionary<byte, string>(fields));
    }

    static bool IsSqlState(string state)
    {
        if (state.Length != 5) return false;
        foreach (var c in state)
            if (!(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'Z')) return false;
        return true;
    }

    static IOException InvalidMessage() => new("Malformed PG backend message.");
}
