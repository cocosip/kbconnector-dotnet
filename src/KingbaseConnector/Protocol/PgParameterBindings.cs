using System.Collections;
using System.Data;
using System.Globalization;
using System.Numerics;

namespace KingbaseConnector.Protocol;

internal sealed class PgParameterBindings
{
    PgParameterBindings(string sql, PgQueryParameter[] parameters, bool endsTransaction, bool invalidatesLargeObjects, bool changesLargeObjectScopes)
    {
        Sql = sql;
        Parameters = Array.AsReadOnly(parameters);
        EndsTransaction = endsTransaction;
        InvalidatesLargeObjects = invalidatesLargeObjects;
        ChangesLargeObjectScopes = changesLargeObjectScopes;
    }
    internal string Sql { get; }
    internal IReadOnlyList<PgQueryParameter> Parameters { get; }
    internal bool EndsTransaction { get; }
    internal bool InvalidatesLargeObjects { get; }
    internal bool ChangesLargeObjectScopes { get; }
    internal static PgParameterBindings Create(string sql, KingbaseParameterCollection parameters,
        PgTypeCatalog catalog, KingbaseCompatibilityMode mode, bool? standardConformingStrings)
    {
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (parameters.Count > 1024) throw InvalidBinding();
        var snapshot = parameters.Cast<KingbaseParameter>().Select(p => p.Snapshot()).ToArray();
        var plan = PgParameterPlan.Create(sql, snapshot.Select(p => (string?)p.ParameterName).ToArray(), mode, standardConformingStrings);
        var codecs = new PgTextCodecs(catalog);
        var values = new PgQueryParameter[plan.ParameterIndexes.Count];
        var textBytes = PgFrontendMessages.CStringLength(plan.Sql);
        for (var i = 0; i < values.Length; i++)
        {
            var p = snapshot[plan.ParameterIndexes[i]];
            if (p.Direction != ParameterDirection.Input)
                throw new NotSupportedException("Parameter direction requires an implemented output contract.");
            var type = Resolve(p, catalog);
            var clrType = codecs.GetFieldType(type.Oid);
            ValidateIntegerInput(p);
            ValidateSize(p, clrType);
            ValidateNumeric(p, type.Name);
            // This validates exact CLR/layout/text before a later exchange can write.
            var text = codecs.Encode(type.Oid, p.Value);
            textBytes += text is null ? 0 : PgBinary.Utf8.GetByteCount(text);
            if (textBytes > 1024 * 1024) throw InvalidBinding();
            values[i] = new PgQueryParameter(type.Oid, p.Value);
        }
        // Validate all inputs and aggregate size before copying any mutable buffer.
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].Value is byte[] bytes) values[i] = new PgQueryParameter(values[i].TypeOid, (byte[])bytes.Clone());
            else if (values[i].Value is BitArray bits) values[i] = new PgQueryParameter(values[i].TypeOid, bits.Clone());
        }
        return new PgParameterBindings(plan.Sql, values, plan.EndsTransaction, plan.InvalidatesLargeObjects, plan.ChangesLargeObjectScopes);
    }

    static PgTypeDescription Resolve(KingbaseParameter p, PgTypeCatalog catalog)
    {
        if (p.ExplicitProviderType is { } logical && p.ExplicitDbType is { } constraint &&
            (!KingbaseParameterTypes.Compatible(constraint, logical) || (constraint == DbType.Byte && logical == KingbaseDbType.UnsignedInteger)))
            throw InvalidBinding();
        PgTypeDescription? selected = null;
        if (p.DataTypeName is { } explicitName)
        {
            var key = explicitName.IndexOf('.') < 0 ? "pg_catalog." + explicitName : explicitName;
            if (!catalog.ByName.TryGetValue(key, out selected)) throw KingbaseParameterTypes.Unsupported();
        }
        if (p.ExplicitProviderType is { } provider)
        {
            var resolved = Lookup(provider, catalog);
            if (selected is not null && selected.Oid != resolved.Oid) throw InvalidBinding();
            selected ??= resolved;
        }
        if (p.ExplicitDbType is { } dbType)
        {
            if (selected is not null)
            {
                if (p.ExplicitProviderType is null && !KingbaseParameterTypes.Compatible(dbType, Provider(selected.CodecName))) throw InvalidBinding();
            }
            else selected = Lookup(KingbaseParameterTypes.FromDbType(dbType), catalog);
        }
        if (selected is not null) return selected;
        if (p.Value is null or DBNull) throw new ArgumentException("SQL NULL requires an explicit parameter type.");
        var inferred = KingbaseParameterTypes.Infer(p.Value) ?? throw KingbaseParameterTypes.Unsupported();
        return Lookup(inferred, catalog);
    }

    static PgTypeDescription Lookup(KingbaseDbType provider, PgTypeCatalog catalog)
    {
        var name = KingbaseParameterTypes.Name(provider);
        if (provider is KingbaseDbType.Blob or KingbaseDbType.Clob or KingbaseDbType.NClob)
        {
            var codec = provider == KingbaseDbType.Blob ? "bytea" : "text";
            foreach (var schema in new[] { "pg_catalog.", "sys." })
                if (catalog.ByName.TryGetValue(schema + name, out var lob) && lob.CodecName == codec) return lob;
            throw KingbaseParameterTypes.Unsupported();
        }
        if (name == "oracle_date" || name.StartsWith("sqlserver_", StringComparison.Ordinal) || name.StartsWith("mysql_", StringComparison.Ordinal))
        {
            var key = "sys." + (name == "oracle_date" ? "date" : name.Substring(name.IndexOf('_') + 1));
            return catalog.ByName.TryGetValue(key, out var nativeType) && nativeType.CodecName == name ? nativeType : throw KingbaseParameterTypes.Unsupported();
        }
        if (provider == KingbaseDbType.SignedTinyint)
        {
            if (catalog.ByName.TryGetValue("pg_catalog.tinyint", out var signed) && EligibleTiny(signed, "sbyte")) return signed;
            if (catalog.ByName.TryGetValue("sys.tinyint", out signed) && EligibleTiny(signed, "sbyte")) return signed;
            name = "int2";
        }
        else if (provider == KingbaseDbType.UnsignedTinyint)
        {
            if (catalog.ByName.TryGetValue("sys.tinyint", out var unsigned) && EligibleTiny(unsigned, "byte")) return unsigned;
            if (catalog.ByName.TryGetValue("sys.uint4", out unsigned)) return unsigned;
            name = "int2";
        }
        if (provider == KingbaseDbType.UnsignedInteger && !catalog.ByName.ContainsKey("sys.uint4"))
            return catalog.ByName["pg_catalog.int8"];
        if (provider == KingbaseDbType.UnsignedBigint && !catalog.ByName.ContainsKey("sys.uint8"))
            return catalog.ByName.TryGetValue("pg_catalog.int16", out var wide) ? wide : throw KingbaseParameterTypes.Unsupported();
        var native = provider is KingbaseDbType.UnsignedInteger or KingbaseDbType.UnsignedBigint or KingbaseDbType.VarcharByte or
            KingbaseDbType.CharByte or KingbaseDbType.NChar or KingbaseDbType.NVarchar or KingbaseDbType.SqlServerBit;
        if (provider == KingbaseDbType.SqlServerBit) name = "bit";
        return catalog.ByName.TryGetValue((native ? "sys." : "pg_catalog.") + name, out var type) ? type : throw KingbaseParameterTypes.Unsupported();
    }
    static bool EligibleTiny(PgTypeDescription type, string codec) => type.CodecName == codec && type.Kind == 'b' && type.Category == 'N' &&
        type.Size == 1 && type.BaseTypeOid == 0 && type.ElementTypeOid == 0;
    static KingbaseDbType Provider(string name)
    {
        if (name == "sbyte") return KingbaseDbType.SignedTinyint;
        if (name == "byte") return KingbaseDbType.UnsignedTinyint;
        foreach (KingbaseDbType type in Enum.GetValues(typeof(KingbaseDbType)))
            if (KingbaseParameterTypes.Name(type) == name) return type;
        throw KingbaseParameterTypes.Unsupported();
    }

    static void ValidateIntegerInput(KingbaseParameter parameter)
    {
        if (parameter.Value is null or DBNull) return;
        if ((parameter.ExplicitDbType == DbType.Byte || parameter.ExplicitProviderType == KingbaseDbType.UnsignedTinyint) && parameter.Value is not byte)
            throw new InvalidCastException("Unsigned tiny integer input requires Byte.");
        if ((parameter.ExplicitDbType == DbType.SByte || parameter.ExplicitProviderType == KingbaseDbType.SignedTinyint) && parameter.Value is not sbyte)
            throw new InvalidCastException("Signed tiny integer input requires SByte.");
        if ((parameter.ExplicitDbType == DbType.UInt32 || parameter.ExplicitProviderType == KingbaseDbType.UnsignedInteger) && parameter.Value is not uint)
            throw new InvalidCastException("Unsigned integer input requires UInt32.");
        if ((parameter.ExplicitDbType == DbType.UInt64 || parameter.ExplicitProviderType == KingbaseDbType.UnsignedBigint) && parameter.Value is not ulong)
            throw new InvalidCastException("Unsigned big integer input requires UInt64.");
    }

    static void ValidateSize(KingbaseParameter p, Type clrType)
    {
        if (p.Size == 0) return;
        if (clrType != typeof(string) && clrType != typeof(byte[]))
            throw new NotSupportedException("Parameter Size applies only to text or binary input.");
        var length = p.Value switch { string text => text.Length, byte[] bytes => bytes.Length, _ => 0 };
        if (length > p.Size) throw new ArgumentException("Parameter input exceeds Size; truncation is not supported.");
    }

    static void ValidateNumeric(KingbaseParameter p, string name)
    {
        if (p.Precision == 0 && !p.ScaleSpecified) return;
        if (name != "numeric") throw new NotSupportedException("Parameter precision/scale requires numeric input.");
        if (p.Precision != 0 && p.ScaleSpecified && p.Scale > p.Precision) throw InvalidBinding();
        if (p.Value is null or DBNull) return;
        var numeric = p.Value switch
        {
            decimal value => KingbaseNumeric.FromDecimal(value), KingbaseNumeric value => value,
            _ => throw new InvalidCastException("Numeric input requires Decimal or KingbaseNumeric.")
        };
        if (!numeric.IsFinite) throw NumericRange();
        var scale = p.ScaleSpecified ? p.Scale : numeric.Scale;
        if (p.Precision != 0 && scale > p.Precision) throw NumericRange();
        var coefficient = BigInteger.Abs(numeric.Coefficient);
        if (numeric.Scale > scale)
        {
            coefficient = BigInteger.DivRem(coefficient, BigInteger.Pow(10, numeric.Scale - scale), out var remainder);
            if (!remainder.IsZero) throw NumericRange();
        }
        else if (numeric.Scale < scale) coefficient *= BigInteger.Pow(10, scale - numeric.Scale);
        if (p.Precision != 0 && !coefficient.IsZero && coefficient.ToString(CultureInfo.InvariantCulture).Length > p.Precision)
            throw NumericRange();
    }

    static ArgumentException InvalidBinding() => new("Invalid or conflicting Kingbase parameter constraints.");
    static OverflowException NumericRange() => new("Numeric input cannot satisfy precision/scale exactly.");
}
