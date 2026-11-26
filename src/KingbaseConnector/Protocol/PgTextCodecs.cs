using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace KingbaseConnector.Protocol;

internal sealed class PgTextCodecs
{
    const int MaxTextBytes = 1024 * 1024;
    readonly PgTypeCatalog _catalog;

    internal PgTextCodecs(PgTypeCatalog catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    internal Type GetFieldType(uint oid) => Resolve(oid).ClrType;
    internal object DecodeStreaming(uint oid, string? text, string? dateStyle, string? intervalStyle, bool provider)
    {
        if (text is null) return DBNull.Value;
        var (name, type) = Resolve(oid);
        if (type == typeof(string) || name == "bytea")
        {
            if (text.IndexOf('\0') >= 0) throw InvalidText();
            try { _ = PgBinary.Utf8.GetByteCount(text); } catch (EncoderFallbackException) { throw InvalidText(); }
            return name == "bytea" ? DecodeBytes(text) : text;
        }
        return provider ? DecodeProviderSpecific(oid, text, dateStyle, intervalStyle) : Decode(oid, text, dateStyle, intervalStyle);
    }
    internal Type GetProviderSpecificFieldType(uint oid) => Resolve(oid).Name switch
    { "numeric" => typeof(KingbaseNumeric), "date" => typeof(KingbaseDate), "timestamp" => typeof(KingbaseTimestamp),
        "timestamptz" => typeof(KingbaseTimestampTz), "oracle_date" => typeof(KingbaseTimestamp),
        "sqlserver_date" => typeof(KingbaseDate), "mysql_date" => typeof(KingbaseMySqlDate),
        "mysql_datetime" or "mysql_timestamp" => typeof(KingbaseMySqlDateTime), _ => GetFieldType(oid) };
    internal object DecodeProviderSpecific(uint oid, string? text, string? dateStyle = null, string? intervalStyle = null)
    {
        var name = Resolve(oid).Name;
        if (PgNativeTemporalText.Supports(name))
        { if (text is null) return DBNull.Value; ValidateText(text); return PgNativeTemporalText.Decode(name, text, dateStyle, provider: true); }
        if (name is not ("numeric" or "date" or "timestamp" or "timestamptz")) return Decode(oid, text, dateStyle, intervalStyle);
        if (text is null) return DBNull.Value;
        ValidateText(text);
        if (name == "date") { RequireIsoDateStyle(dateStyle); return KingbaseDate.Parse(text); }
        if (name == "timestamp") { RequireIsoDateStyle(dateStyle); return KingbaseTimestamp.Parse(text); }
        if (name == "timestamptz") { RequireIsoDateStyle(dateStyle); return KingbaseTimestampTz.Parse(text); }
        return KingbaseNumeric.Parse(text);
    }

    internal object Decode(uint oid, string? text, string? dateStyle = null, string? intervalStyle = null)
    {
        var (name, _) = Resolve(oid);
        if (text is null) return DBNull.Value;
        ValidateText(text);
        if (PgNativeTemporalText.Supports(name)) return PgNativeTemporalText.Decode(name, text, dateStyle, provider: false);
        switch (name)
        {
            case "bool": return text switch { "t" => true, "f" => false, _ => throw InvalidText() };
            case "sqlserver_bit": return text switch { "1" => true, "0" => false, _ => throw InvalidText() };
            case "bit":
            case "varbit":
                var bits = new BitArray(text.Length);
                for (var i = 0; i < text.Length; i++) bits[i] = text[i] switch { '0' => false, '1' => true, _ => throw InvalidText() };
                return bits;
            case "sbyte":
                ValidateNumber(text, integer: true);
                if (sbyte.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var signedByte)) return signedByte;
                throw OutOfRange();
            case "byte":
                ValidateNumber(text, integer: true);
                if (text[0] != '-' && byte.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var unsignedByte)) return unsignedByte;
                throw OutOfRange();
            case "int2":
                ValidateNumber(text, integer: true);
                if (short.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var int2)) return int2;
                throw OutOfRange();
            case "int4":
                ValidateNumber(text, integer: true);
                if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var int4)) return int4;
                throw OutOfRange();
            case "int8":
                ValidateNumber(text, integer: true);
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var int8)) return int8;
                throw OutOfRange();
            case "oid":
            case "uint4":
                ValidateNumber(text, integer: true);
                if (text[0] == '-') throw InvalidText();
                if (uint.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var oidValue)) return oidValue;
                throw OutOfRange();
            case "uint8":
                ValidateNumber(text, integer: true);
                if (text[0] != '-' && ulong.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var unsignedLong)) return unsignedLong;
                throw OutOfRange();
            case "int16":
                ValidateNumber(text, integer: true);
                if (text.Length > 40 || !BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var wide)) throw OutOfRange();
                ValidateWide(wide); return wide;
            case "float4": return DecodeFloating(text, single: true);
            case "float8": return DecodeFloating(text, single: false);
            case "numeric": return KingbaseNumeric.Parse(text).ToDecimal();
            case "date": RequireIsoDateStyle(dateStyle); return KingbaseDate.Parse(text).ToDateTime();
            case "time": return PgTimeText.Parse(text);
            case "timetz": return KingbaseTimeTz.Parse(text);
            case "timestamp": RequireIsoDateStyle(dateStyle); return KingbaseTimestamp.Parse(text).ToDateTime();
            case "timestamptz": RequireIsoDateStyle(dateStyle); return KingbaseTimestampTz.Parse(text).ToDateTime();
            case "interval": return PgIntervalText.Parse(text, intervalStyle);
            case "uuid":
                if (text.Length == 36 && Guid.TryParseExact(text, "D", out var uuid)) return uuid;
                throw InvalidText();
            case "bytea": return DecodeBytes(text);
            default: return text;
        }
    }

    internal string? Encode(uint oid, object? value)
    {
        var (name, clrType) = Resolve(oid);
        if (value is null || value is DBNull) return null;
        if (PgNativeTemporalText.Supports(name)) return PgNativeTemporalText.Encode(name, value);
        if (name == "int16" && value is BigInteger wide) { ValidateWide(wide); return wide.ToString(CultureInfo.InvariantCulture); }
        if (name == "int16" && value is ulong unsignedWide) return ((BigInteger)unsignedWide).ToString(CultureInfo.InvariantCulture);
        if (name == "int8" && value is uint unsignedInteger) return ((long)unsignedInteger).ToString(CultureInfo.InvariantCulture);
        if (name == "int2" && value is byte or sbyte) return Convert.ToInt16(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        if (name == "uint4" && value is byte smallUnsigned) return ((uint)smallUnsigned).ToString(CultureInfo.InvariantCulture);
        if (name == "numeric" && value is KingbaseNumeric numeric) return numeric.ToString();
        if (name == "interval") return value switch
        {
            KingbaseInterval interval => interval.ToString(),
            TimeSpan interval => KingbaseInterval.FromTimeSpan(interval).ToString(),
            _ => throw new InvalidCastException("PG INTERVAL input requires KingbaseInterval or exact TimeSpan.")
        };
        if (name == "timestamp") return value switch
        {
            KingbaseTimestamp timestamp => timestamp.ToString(),
            DateTime timestamp => KingbaseTimestamp.FromDateTime(timestamp).ToString(),
            _ => throw new InvalidCastException("PG TIMESTAMP input requires a calendar timestamp or Unspecified DateTime.")
        };
        if (name == "timestamptz") return value switch
        {
            KingbaseTimestampTz timestamp => timestamp.ToString(),
            DateTime timestamp => KingbaseTimestampTz.FromDateTime(timestamp).ToString(),
            DateTimeOffset timestamp => KingbaseTimestampTz.FromDateTimeOffset(timestamp).ToString(),
            _ => throw new InvalidCastException("PG TIMESTAMPTZ input requires a UTC timestamp or explicit-offset instant.")
        };
        if (name == "time") return value switch
        {
            TimeSpan time => PgTimeText.Format(time),
#if NET6_0_OR_GREATER
            TimeOnly time => PgTimeText.Format(time.ToTimeSpan()),
#endif
            _ => throw new InvalidCastException("PG TIME input requires TimeSpan or a supported TimeOnly value.")
        };
        if (name == "date") return value switch
        {
            KingbaseDate date => date.ToString(),
            DateTime date => KingbaseDate.FromDateTime(date).ToString(),
#if NET6_0_OR_GREATER
            DateOnly date => KingbaseDate.FromDateOnly(date).ToString(),
#endif
            _ => throw new InvalidCastException("PG DATE input requires an exact calendar date value.")
        };
        if (value.GetType() != clrType) throw new InvalidCastException("PG scalar input requires its exact mapped CLR type.");
        var text = name switch
        {
            "bool" => (bool)value ? "t" : "f",
            "sqlserver_bit" => (bool)value ? "1" : "0",
            "bit" or "varbit" => EncodeBits((BitArray)value),
            "float4" => EncodeFloating((float)value),
            "float8" => EncodeFloating((double)value),
            "numeric" => KingbaseNumeric.FromDecimal((decimal)value).ToString(),
            "uuid" => ((Guid)value).ToString("D"),
            "bytea" => EncodeBytes((byte[])value),
            "timetz" => ((KingbaseTimeTz)value).ToString(),
            "sbyte" or "byte" or "int2" or "int4" or "int8" or "oid" or "uint4" or "uint8" => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
            _ => (string)value
        };
        ValidateText(text);
        return text;
    }

    (string Name, Type ClrType) Resolve(uint oid)
    {
        if (!_catalog.ByOid.TryGetValue(oid, out var type) || type.ElementTypeOid != 0 ||
            (!SupportedNativeDomain(type) && (type.Kind != 'b' || type.BaseTypeOid != 0)))
            throw Unsupported();
        if (type.CodecName is "sbyte" or "byte" or "uint4" or "uint8" or "int16" && type.Category != 'N') throw Unsupported();
        if (type.CodecName is "bit" or "varbit" && type.Category != 'V') throw Unsupported();
        if (PgNativeTemporalText.Supports(type.CodecName) && type.Category != (type.CodecName == "mysql_year" ? 'N' : 'D')) throw Unsupported();
        var (clrType, size) = type.CodecName switch
        {
            "bool" => (typeof(bool), 1),
            "sqlserver_bit" => (typeof(bool), 1),
            "bit" or "varbit" => (typeof(BitArray), -1),
            "sbyte" => (typeof(sbyte), 1),
            "byte" => (typeof(byte), 1),
            "int2" => (typeof(short), 2),
            "int4" => (typeof(int), 4),
            "int8" => (typeof(long), 8),
            "oid" => (typeof(uint), 4),
            "uint4" => (typeof(uint), 4),
            "uint8" => (typeof(ulong), 8),
            "int16" => (typeof(BigInteger), 16),
            "float4" => (typeof(float), 4),
            "float8" => (typeof(double), 8),
            "numeric" => (typeof(decimal), -1),
            "date" => (typeof(DateTime), 4),
            "sqlserver_date" or "mysql_date" => (typeof(DateTime), 4),
            "oracle_date" or "sqlserver_datetime" or "sqlserver_datetime2" or "sqlserver_smalldatetime" or "mysql_datetime" or "mysql_timestamp" => (typeof(DateTime), 8),
            "sqlserver_time" or "mysql_time" => (typeof(TimeSpan), 8),
            "mysql_year" => (typeof(int), 4),
            "time" => (typeof(TimeSpan), 8),
            "timetz" => (typeof(KingbaseTimeTz), 12),
            "timestamp" or "timestamptz" => (typeof(DateTime), 8),
            "interval" => (typeof(KingbaseInterval), 16),
            "uuid" => (typeof(Guid), 16),
            "bytea" => (typeof(byte[]), -1),
            "text" or "varchar" or "bpchar" or "json" or "jsonb" or "xml" => (typeof(string), -1),
            _ => throw Unsupported()
        };
        if (type.Size != size) throw Unsupported();
        return (type.CodecName, clrType);
    }

    bool SupportedNativeDomain(PgTypeDescription type)
    {
        var lob = type.Name is "blob" or "clob" or "nclob";
        var key = type.CodecName switch { "oracle_date" when type.Name == "date" => "pg_catalog.timestamp", "mysql_year" when type.Name == "year" => "pg_catalog.int4",
            "bytea" when type.Name == "blob" => "pg_catalog.bytea", "text" when type.Name is "clob" or "nclob" => "pg_catalog.text", _ => null };
        return key is not null && (type.Namespace == "sys" || (lob && type.Namespace == "pg_catalog")) && type.Kind == 'd' && _catalog.ByName.TryGetValue(key, out var baseType) &&
            type.BaseTypeOid == baseType.Oid && baseType.Kind == 'b' && baseType.BaseTypeOid == 0 && baseType.ElementTypeOid == 0 && baseType.Size == type.Size && baseType.Category == type.Category;
    }

    static void ValidateWide(BigInteger value)
    {
        if (value < -(BigInteger.One << 127) || value >= (BigInteger.One << 127)) throw OutOfRange();
    }

    static string EncodeBits(BitArray bits)
    {
        ValidateBitLength(bits);
        var chars = new char[bits.Length];
        for (var i = 0; i < chars.Length; i++) chars[i] = bits[i] ? '1' : '0';
        return new string(chars);
    }

    internal static void ValidateBitLength(BitArray bits)
    {
        if (bits.Length > MaxTextBytes) throw TooLarge();
    }

    internal static void RequireIsoDateStyle(string? style)
    {
        if (style is not ("ISO, MDY" or "ISO, DMY" or "ISO, YMD"))
            throw new NotSupportedException("PG calendar results require a confirmed ISO DateStyle.");
    }

    static void ValidateText(string text)
    {
        if (text.Length > MaxTextBytes) throw TooLarge();
        if (text.IndexOf('\0') >= 0) throw InvalidText();
        try { if (PgBinary.Utf8.GetByteCount(text) > MaxTextBytes) throw TooLarge(); }
        catch (EncoderFallbackException) { throw InvalidText(); }
    }

    // Return whether the significand is nonzero, independently of exponent digits.
    static bool ValidateNumber(string text, bool integer = false)
    {
        var index = text.Length != 0 && (text[0] == '-' || text[0] == '+') ? 1 : 0;
        var digits = 0;
        var nonzero = false;
        ReadDigits();
        if (!integer && index < text.Length && text[index] == '.') { index++; ReadDigits(); }
        if (digits == 0) throw InvalidText();
        if (!integer && index < text.Length && (text[index] == 'e' || text[index] == 'E'))
        {
            index++;
            if (index < text.Length && (text[index] == '-' || text[index] == '+')) index++;
            var start = index;
            while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
            if (index == start) throw InvalidText();
        }
        if (index != text.Length) throw InvalidText();
        return nonzero;

        void ReadDigits()
        {
            while (index < text.Length && text[index] >= '0' && text[index] <= '9')
            {
                nonzero |= text[index] != '0';
                digits++;
                index++;
            }
        }
    }

    static object DecodeFloating(string text, bool single)
    {
        if (text == "NaN") return single ? (object)float.NaN : double.NaN;
        if (text == "Infinity") return single ? (object)float.PositiveInfinity : double.PositiveInfinity;
        if (text == "-Infinity") return single ? (object)float.NegativeInfinity : double.NegativeInfinity;
        var nonzero = ValidateNumber(text);
        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        if (single)
        {
            if (!float.TryParse(text, style, CultureInfo.InvariantCulture, out var value) ||
                float.IsInfinity(value) || (value == 0 && nonzero)) throw OutOfRange();
            return value == 0 && text[0] == '-' ? -0.0F : value;
        }
        else
        {
            if (!double.TryParse(text, style, CultureInfo.InvariantCulture, out var value) ||
                double.IsInfinity(value) || (value == 0 && nonzero)) throw OutOfRange();
            return value == 0 && text[0] == '-' ? -0.0D : value;
        }
    }

    static string EncodeFloating(float value) => float.IsNaN(value) ? "NaN" :
        float.IsPositiveInfinity(value) ? "Infinity" : float.IsNegativeInfinity(value) ? "-Infinity" :
        value == 0 && 1F / value == float.NegativeInfinity ? "-0" : value.ToString("R", CultureInfo.InvariantCulture);
    static string EncodeFloating(double value) => double.IsNaN(value) ? "NaN" :
        double.IsPositiveInfinity(value) ? "Infinity" : double.IsNegativeInfinity(value) ? "-Infinity" :
        value == 0 && 1D / value == double.NegativeInfinity ? "-0" : value.ToString("R", CultureInfo.InvariantCulture);

    static byte[] DecodeBytes(string text)
    {
        if (text.StartsWith("\\x", StringComparison.Ordinal))
        {
            if (text.Length % 2 != 0) throw InvalidText();
            var bytes = new byte[(text.Length - 2) / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)((Hex(text[2 + i * 2]) << 4) | Hex(text[3 + i * 2]));
            return bytes;
        }
        var output = new byte[text.Length];
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c > 127) throw InvalidText();
            if (c != '\\') { output[count++] = (byte)c; continue; }
            if (++i >= text.Length) throw InvalidText();
            if (text[i] == '\\') { output[count++] = 92; continue; }
            if (i + 2 >= text.Length || text[i] < '0' || text[i] > '3' ||
                text[i + 1] < '0' || text[i + 1] > '7' || text[i + 2] < '0' || text[i + 2] > '7') throw InvalidText();
            output[count++] = (byte)(((text[i] - '0') << 6) | ((text[i + 1] - '0') << 3) | (text[i + 2] - '0'));
            i += 2;
        }
        if (count != output.Length) Array.Resize(ref output, count);
        return output;
    }

    static int Hex(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 : throw InvalidText();

    static string EncodeBytes(byte[] bytes)
    {
        if (bytes.Length > (MaxTextBytes - 2) / 2) throw TooLarge();
        const string hex = "0123456789abcdef";
        var text = new char[2 + bytes.Length * 2];
        text[0] = '\\'; text[1] = 'x';
        for (var i = 0; i < bytes.Length; i++)
        {
            text[2 + i * 2] = hex[bytes[i] >> 4];
            text[3 + i * 2] = hex[bytes[i] & 15];
        }
        return new string(text);
    }

    static FormatException InvalidText() => new("Invalid PG scalar text.");
    static OverflowException OutOfRange() => new("PG scalar value is outside the CLR range.");
    static ArgumentOutOfRangeException TooLarge() => new("value", "PG scalar text exceeds the bounded value limit.");
    static NotSupportedException Unsupported() => new("No scalar text codec is available for this PG catalog type.");
}
