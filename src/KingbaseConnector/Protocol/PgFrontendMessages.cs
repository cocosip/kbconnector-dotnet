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

    internal static byte[] Startup(string username, string database)
    {
        var userLength = CStringLength(username);
        var databaseLength = CStringLength(database);
        const string userKey = "user\0";
        const string databaseKey = "database\0";
        const string encodingPair = "client_encoding\0UTF8\0";
        var length = 8L + userKey.Length + userLength + databaseKey.Length + databaseLength + encodingPair.Length + 1;
        if (length > PgWire.MaxMessageLength) throw new ArgumentException("Oversized PG startup message.");
        var message = new byte[(int)length];
        PgBinary.WriteInt32(message, 0, message.Length);
        PgBinary.WriteInt32(message, 4, 196608);
        var offset = 8;
        offset += PgBinary.Utf8.GetBytes(userKey, 0, userKey.Length, message, offset);
        offset += PgBinary.Utf8.GetBytes(username, 0, username.Length, message, offset) + 1;
        offset += PgBinary.Utf8.GetBytes(databaseKey, 0, databaseKey.Length, message, offset);
        offset += PgBinary.Utf8.GetBytes(database, 0, database.Length, message, offset) + 1;
        PgBinary.Utf8.GetBytes(encodingPair, 0, encodingPair.Length, message, offset);
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
        var textLength = CStringLength(password);
        if (textLength > PgWire.MaxMessageLength - 4) throw new ArgumentException("Oversized PG password message.");
        var message = new byte[5 + textLength];
        message[0] = (byte)'p';
        PgBinary.WriteInt32(message, 1, message.Length - 1);
        PgBinary.Utf8.GetBytes(password, 0, password.Length, message, 5);
        return message;
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
