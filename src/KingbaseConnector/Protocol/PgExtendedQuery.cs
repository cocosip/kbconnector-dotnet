namespace KingbaseConnector.Protocol;

internal sealed class PgQueryParameter(uint typeOid, object? value)
{
    internal uint TypeOid { get; } = typeOid;
    internal object? Value { get; } = value;
}

internal static class PgExtendedQuery
{
    const int MaxBytes = 1024 * 1024;
    internal static byte[][] Encode(string sql, PgTypeCatalog catalog, IReadOnlyList<PgQueryParameter> parameters) =>
        EncodeCore(sql, catalog, parameters, "", includeParse: true);

    internal static byte[][] EncodePrepared(string name, PgTypeCatalog catalog, IReadOnlyList<PgQueryParameter> parameters) =>
        EncodeCore("", catalog, parameters, name, includeParse: false);

    internal static byte[][] EncodePrepare(string sql, IReadOnlyList<uint> oids, string name)
    {
        var length = 7L + PgFrontendMessages.CStringLength(name) + PgFrontendMessages.CStringLength(sql) + 4L * oids.Count;
        if (oids.Count > 1024 || length + 5 > MaxBytes) throw TooLarge();
        return [Parse(sql, oids, name, (int)length), PgFrontendMessages.Sync()];
    }

    internal static byte[][] EncodeDescribe(string sql, IReadOnlyList<uint> oids)
    {
        var frames = EncodePrepare(sql, oids, "");
        if (frames[0].Length + 12 > MaxBytes)
        {
            Array.Clear(frames[0], 0, frames[0].Length);
            throw TooLarge();
        }
        return [frames[0], [(byte)'D', 0, 0, 0, 6, (byte)'S', 0], frames[1]];
    }

    internal static byte[][] EncodeClose(string name)
    {
        var message = new byte[6 + PgFrontendMessages.CStringLength(name)];
        message[0] = (byte)'C';
        PgBinary.WriteInt32(message, 1, message.Length - 1);
        message[5] = (byte)'S';
        PgBinary.Utf8.GetBytes(name, 0, name.Length, message, 6);
        return [message, PgFrontendMessages.Sync()];
    }

    static byte[][] EncodeCore(string sql, PgTypeCatalog catalog, IReadOnlyList<PgQueryParameter> parameters,
        string name, bool includeParse)
    {
        var codecs = new PgTextCodecs(catalog);
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));
        if (parameters.Count > 1024) throw TooLarge();
        var snapshot = parameters.ToArray();
        if (snapshot.Length > 1024) throw TooLarge();
        var sqlLength = PgFrontendMessages.CStringLength(sql);
        var nameBytes = PgFrontendMessages.CStringLength(name) - 1;
        var parseLength = includeParse ? 8L + nameBytes + sqlLength + 4L * snapshot.Length : 0;
        var bindLength = 13L + nameBytes + 4L * snapshot.Length;
        if (parseLength + bindLength + 22 > MaxBytes) throw TooLarge();
        var values = new string?[snapshot.Length];
        var lengths = new int[snapshot.Length];
        for (var i = 0; i < snapshot.Length; i++)
        {
            var parameter = snapshot[i] ?? throw new ArgumentException("PG query parameters cannot contain null entries.", nameof(parameters));
            values[i] = codecs.Encode(parameter.TypeOid, parameter.Value);
            lengths[i] = values[i] is null ? -1 : PgBinary.Utf8.GetByteCount(values[i]!);
            bindLength += Math.Max(0, lengths[i]);
            if (parseLength + bindLength + 22 > MaxBytes) throw TooLarge();
        }

        var bind = new byte[(int)bindLength];
        byte[][] messages = [bind, [(byte)'D', 0, 0, 0, 6, (byte)'P', 0],
            [(byte)'E', 0, 0, 0, 9, 0, 0, 0, 0, 0], PgFrontendMessages.Sync()];
        if (includeParse) messages = [Parse(sql, snapshot.Select(p => p.TypeOid).ToArray(), name, (int)parseLength), .. messages];
        bind[0] = (byte)'B';
        PgBinary.WriteInt32(bind, 1, bind.Length - 1);
        PgBinary.Utf8.GetBytes(name, 0, name.Length, bind, 6);
        WriteCount(bind, 9 + nameBytes, snapshot.Length);
        var offset = 11 + nameBytes;
        for (var i = 0; i < snapshot.Length; i++)
        {
            PgBinary.WriteInt32(bind, offset, lengths[i]);
            offset += 4;
            if (values[i] is not { } text) continue;
            offset += PgBinary.Utf8.GetBytes(text, 0, text.Length, bind, offset);
        }
        return messages;
    }

    static byte[] Parse(string sql, IReadOnlyList<uint> oids, string name, int length)
    {
        var message = new byte[length];
        message[0] = (byte)'P';
        PgBinary.WriteInt32(message, 1, message.Length - 1);
        var offset = 5;
        offset += PgBinary.Utf8.GetBytes(name, 0, name.Length, message, offset) + 1;
        offset += PgBinary.Utf8.GetBytes(sql, 0, sql.Length, message, offset) + 1;
        WriteCount(message, offset, oids.Count);
        offset += 2;
        foreach (var oid in oids)
        {
            PgBinary.WriteInt32(message, offset, unchecked((int)oid));
            offset += 4;
        }
        return message;
    }

    static void WriteCount(byte[] message, int offset, int count)
    {
        message[offset] = (byte)(count >> 8);
        message[offset + 1] = (byte)count;
    }
    static ArgumentException TooLarge() => new("PG extended query exceeds the parameter or encoded-byte limit.");
}
