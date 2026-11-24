namespace KingbaseConnector.Protocol;

internal sealed class PgQueryParameter(uint typeOid, object? value)
{
    internal uint TypeOid { get; } = typeOid;
    internal object? Value { get; } = value;
}

internal static class PgExtendedQuery
{
    const int MaxBytes = 1024 * 1024;
    internal static byte[][] Encode(string sql, PgTypeCatalog catalog, IReadOnlyList<PgQueryParameter> parameters)
    {
        var codecs = new PgTextCodecs(catalog);
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));
        if (parameters.Count > 1024) throw TooLarge();
        var snapshot = parameters.ToArray();
        if (snapshot.Length > 1024) throw TooLarge();
        var sqlLength = PgFrontendMessages.CStringLength(sql);
        var parseLength = 8L + sqlLength + 4L * snapshot.Length;
        var bindLength = 13L + 4L * snapshot.Length;
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

        var parse = new byte[(int)parseLength];
        var bind = new byte[(int)bindLength];
        byte[][] messages = [parse, bind, [(byte)'D', 0, 0, 0, 6, (byte)'P', 0],
            [(byte)'E', 0, 0, 0, 9, 0, 0, 0, 0, 0], PgFrontendMessages.Sync()];
        parse[0] = (byte)'P';
        PgBinary.WriteInt32(parse, 1, parse.Length - 1);
        PgBinary.Utf8.GetBytes(sql, 0, sql.Length, parse, 6);
        var offset = 6 + sqlLength;
        WriteCount(parse, offset, snapshot.Length);
        offset += 2;
        foreach (var parameter in snapshot)
        {
            PgBinary.WriteInt32(parse, offset, unchecked((int)parameter.TypeOid));
            offset += 4;
        }
        bind[0] = (byte)'B';
        PgBinary.WriteInt32(bind, 1, bind.Length - 1);
        WriteCount(bind, 9, snapshot.Length);
        offset = 11;
        for (var i = 0; i < snapshot.Length; i++)
        {
            PgBinary.WriteInt32(bind, offset, lengths[i]);
            offset += 4;
            if (values[i] is not { } text) continue;
            offset += PgBinary.Utf8.GetBytes(text, 0, text.Length, bind, offset);
        }
        return messages;
    }

    static void WriteCount(byte[] message, int offset, int count)
    {
        message[offset] = (byte)(count >> 8);
        message[offset + 1] = (byte)count;
    }
    static ArgumentException TooLarge() => new("PG extended query exceeds the parameter or encoded-byte limit.");
}
