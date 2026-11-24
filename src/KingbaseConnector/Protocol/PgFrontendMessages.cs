using System.Text;

namespace KingbaseConnector.Protocol;

internal static class PgFrontendMessages
{
    internal static byte[] SslRequest()
    {
        var message = new byte[8];
        PgBinary.WriteInt32(message, 0, message.Length);
        PgBinary.WriteInt32(message, 4, 80877103);
        return message;
    }

    internal static byte[] CancelRequest(int processId, int secretKey)
    {
        var message = new byte[16];
        PgBinary.WriteInt32(message, 0, message.Length);
        PgBinary.WriteInt32(message, 4, 80877102);
        PgBinary.WriteInt32(message, 8, processId);
        PgBinary.WriteInt32(message, 12, secretKey);
        return message;
    }

    internal static byte[] Startup(string username, string database, string applicationName = "", string searchPath = "", string timezone = "")
    {
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("user", username), new("database", database), new("client_encoding", "UTF8")
        };
        if (applicationName.Length != 0) pairs.Add(new("application_name", applicationName));
        if (searchPath.Length != 0) pairs.Add(new("search_path", searchPath));
        if (timezone.Length != 0) pairs.Add(new("TimeZone", timezone));
        var length = 9L;
        foreach (var pair in pairs) length += CStringLength(pair.Key) + CStringLength(pair.Value);
        if (length > PgWire.MaxMessageLength) throw new ArgumentException("Oversized PG startup message.");
        var message = new byte[(int)length];
        PgBinary.WriteInt32(message, 0, message.Length);
        PgBinary.WriteInt32(message, 4, 196608);
        var offset = 8;
        foreach (var pair in pairs)
        {
            offset += PgBinary.Utf8.GetBytes(pair.Key, 0, pair.Key.Length, message, offset) + 1;
            offset += PgBinary.Utf8.GetBytes(pair.Value, 0, pair.Value.Length, message, offset) + 1;
        }
        return message;
    }

    internal static byte[] Query(string sql)
    {
        var textLength = CStringLength(sql);
        if (textLength > PgWire.MaxMessageLength - 4) throw new ArgumentException("Oversized PG query message.");
        var message = new byte[5 + textLength];
        message[0] = (byte)'Q';
        PgBinary.WriteInt32(message, 1, message.Length - 1);
        PgBinary.Utf8.GetBytes(sql, 0, sql.Length, message, 5);
        return message;
    }

    internal static byte[] Sync() => new byte[] { (byte)'S', 0, 0, 0, 4 };
    internal static byte[] Password(string password)
    {
        var textLength = PasswordTextLength(password);
        var message = new byte[5 + textLength];
        message[0] = (byte)'p';
        PgBinary.WriteInt32(message, 1, message.Length - 1);
        PgBinary.Utf8.GetBytes(password, 0, password.Length, message, 5);
        return message;
    }
    internal static int PasswordTextLength(string password)
    {
        var textLength = CStringLength(password);
        if (textLength > PgWire.MaxMessageLength - 4) throw new ArgumentException("Oversized PG password message.");
        return textLength;
    }
    internal static byte[] Terminate() => new byte[] { (byte)'X', 0, 0, 0, 4 };

    internal static int CStringLength(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text.IndexOf('\0') >= 0) throw new ArgumentException("PG text cannot contain NUL.");
        int length;
        try { length = PgBinary.Utf8.GetByteCount(text); }
        catch (EncoderFallbackException) { throw new ArgumentException("PG text must contain valid Unicode."); }
        if (length >= PgWire.MaxMessageLength) throw new ArgumentException("Oversized PG text field.");
        return length + 1;
    }
}
