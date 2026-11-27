namespace KingbaseConnector.Protocol;

internal static class PgBinaryCopyCodec
{
    internal const int MaxRowBytes = 1024 * 1024;
    internal static string[] Resolve(PgTypeCatalog catalog, IReadOnlyList<KingbaseDbType> selectors)
    {
        if (selectors is null) throw new ArgumentNullException(nameof(selectors));
        if (selectors.Count > 1024) throw new ArgumentException("Binary COPY supports at most 1024 columns.", nameof(selectors));
        var names = selectors.Select(type => type switch {
            KingbaseDbType.Boolean => "bool", KingbaseDbType.Smallint => "int2", KingbaseDbType.Integer => "int4",
            KingbaseDbType.Bigint => "int8", KingbaseDbType.Oid => "oid", KingbaseDbType.Real => "float4",
            KingbaseDbType.Double => "float8", KingbaseDbType.Text => "text", KingbaseDbType.Varchar => "varchar",
            KingbaseDbType.Char => "bpchar", KingbaseDbType.Bytea => "bytea", KingbaseDbType.Uuid => "uuid",
            KingbaseDbType.Numeric => "numeric", KingbaseDbType.Date => "date", KingbaseDbType.Time => "time",
            KingbaseDbType.TimeTz => "timetz", KingbaseDbType.Timestamp => "timestamp", KingbaseDbType.TimestampTz => "timestamptz", KingbaseDbType.Interval => "interval",
            KingbaseDbType.Json => "json", KingbaseDbType.Jsonb => "jsonb", KingbaseDbType.Bit => "bit", KingbaseDbType.Varbit => "varbit", KingbaseDbType.Xml => "xml",
            KingbaseDbType.Binary => "binary", KingbaseDbType.VarBinary => "varbinary",
            _ => throw new NotSupportedException("This type has no implemented binary COPY codec.") }).ToArray();
        var layouts = new PgTextCodecs(catalog);
        foreach (var name in names)
        {
            // Binary COPY takes base scalar types only, so their own catalog row must not expose an element type.
            // sys.binary/sys.varbinary are native varlena types with the same field layout as bytea.
            var key = name is "binary" or "varbinary" ? "sys." + name : "pg_catalog." + name;
            if (!catalog.ByName.TryGetValue(key, out var type) || type.Kind != 'b' ||
                type.BaseTypeOid != 0 || type.ElementTypeOid != 0 || type.CodecName != name)
                throw new NotSupportedException("The binary COPY base type is unavailable in the actual catalog.");
            layouts.GetFieldType(type.Oid);
        }
        return names;
    }

    internal static byte[]? Encode(string name, object? value, int budget)
    {
        if (value is null or DBNull) return null;
        if (name == "xml") return PgBinaryCopyXml.Encode(value, budget);
        if (name == "numeric") return PgBinaryCopyNumeric.Encode(value, budget);
        if (PgBinaryCopyTemporal.Supports(name)) return PgBinaryCopyTemporal.Encode(name, value, budget);
        if (name is "bit" or "varbit") return PgBinaryCopyBits.Encode(value, budget);
        var length = name switch {
            "bool" when value is bool => 1, "int2" when value is short => 2,
            "int4" when value is int => 4, "int8" when value is long => 8, "oid" when value is uint => 4,
            "float4" when value is float => 4, "float8" when value is double => 8, "uuid" when value is Guid => 16,
            "bytea" or "binary" or "varbinary" when value is byte[] bytes => bytes.Length,
            "text" or "varchar" or "bpchar" or "json" or "jsonb" when value is string text && text.IndexOf('\0') < 0 => checked(PgBinary.Utf8.GetByteCount(text) + (name == "jsonb" ? 1 : 0)),
            _ => throw new InvalidCastException("Binary COPY input requires the exact supported CLR type and valid text.") };
        if (length > budget) throw new ArgumentException("Binary COPY row values exceed 1 MiB.");
        if (value is string s)
        {
            var bytes = new byte[length]; var offset = name == "jsonb" ? 1 : 0;
            if (offset != 0) bytes[0] = 1;
            PgBinary.Utf8.GetBytes(s, 0, s.Length, bytes, offset); return bytes;
        }
        if (value is byte[] b) return (byte[])b.Clone();
        if (value is Guid guid) { var bytes = guid.ToByteArray(); GuidOrder(bytes); return bytes; }
        if (value is bool boolean) return new byte[] { boolean ? (byte)1 : (byte)0 };
        if (value is float single) return Network(BitConverter.GetBytes(single));
        if (value is double number) return Network(BitConverter.GetBytes(number));
        var result = new byte[length];
        var integer = value switch { short small => (ulong)(long)small, int i => (ulong)(long)i, long big => (ulong)big, uint oid => oid, _ => 0UL };
        for (var i = length - 1; i >= 0; i--) { result[i] = (byte)integer; integer >>= 8; }
        return result;
    }

    internal static object Decode(string name, byte[] bytes)
    {
        if (name == "numeric") return PgBinaryCopyNumeric.Decode(bytes);
        if (PgBinaryCopyTemporal.Supports(name)) return PgBinaryCopyTemporal.Decode(name, bytes);
        if (name is "bit" or "varbit") return PgBinaryCopyBits.Decode(bytes);
        var size = name switch { "bool" => 1, "int2" => 2, "int4" or "oid" or "float4" => 4, "int8" or "float8" => 8, "uuid" => 16, _ => -1 };
        if (size != -1 && bytes.Length != size) throw Invalid();
        switch (name)
        {
            case "bool": return bytes[0] switch { 0 => false, 1 => true, _ => throw Invalid() };
            case "int2": return (short)((bytes[0] << 8) | bytes[1]);
            case "int4": return PgBinary.ReadInt32(bytes, 0);
            case "oid": return (uint)PgBinary.ReadInt32(bytes, 0);
            case "int8": ulong number = 0; foreach (var b in bytes) number = (number << 8) | b; return (long)number;
            case "float4": return BitConverter.ToSingle(Network((byte[])bytes.Clone()), 0);
            case "float8": return BitConverter.ToDouble(Network((byte[])bytes.Clone()), 0);
            case "uuid": var uuid = (byte[])bytes.Clone(); GuidOrder(uuid); return new Guid(uuid);
            case "bytea": return (byte[])bytes.Clone();
            case "binary": case "varbinary": return (byte[])bytes.Clone();
            case "text": case "varchar": case "bpchar": case "json": case "jsonb": case "xml":
                string text;
                var offset = name == "jsonb" ? 1 : 0;
                if (offset != 0 && (bytes.Length == 0 || bytes[0] != 1)) throw Invalid();
                try { text = PgBinary.Utf8.GetString(bytes, offset, bytes.Length - offset); } catch (System.Text.DecoderFallbackException) { throw Invalid(); }
                return text.IndexOf('\0') < 0 ? text : throw Invalid();
            default: throw new NotSupportedException("This type has no implemented binary COPY codec.");
        }
    }
    static byte[] Network(byte[] bytes) { if (BitConverter.IsLittleEndian) Array.Reverse(bytes); return bytes; }
    static void GuidOrder(byte[] bytes) { Array.Reverse(bytes, 0, 4); Array.Reverse(bytes, 4, 2); Array.Reverse(bytes, 6, 2); }
    static IOException Invalid() => new("Invalid binary COPY value.");
}
