using System.Data;
using System.Globalization;
using System.Numerics;

namespace KingbaseConnector.Protocol;

internal sealed class PgParameterBindings
{
    PgParameterBindings(string sql, PgQueryParameter[] parameters)
    {
        Sql = sql;
        Parameters = Array.AsReadOnly(parameters);
    }
    internal string Sql { get; }
    internal IReadOnlyList<PgQueryParameter> Parameters { get; }
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
            if (values[i].Value is byte[] bytes) values[i] = new PgQueryParameter(values[i].TypeOid, (byte[])bytes.Clone());
        return new PgParameterBindings(plan.Sql, values);
    }

    static PgTypeDescription Resolve(KingbaseParameter p, PgTypeCatalog catalog)
    {
        PgTypeDescription? selected = null;
        if (p.DataTypeName is { } explicitName)
        {
            var key = explicitName.IndexOf('.') < 0 ? "sys_catalog." + explicitName : explicitName;
            if (!catalog.ByName.TryGetValue(key, out selected)) throw KingbaseParameterTypes.Unsupported();
        }
        if (p.ExplicitProviderType is { } provider)
        {
            var name = KingbaseParameterTypes.Name(provider);
            if (selected is not null && selected.Name != name) throw InvalidBinding();
            selected ??= Lookup(name, catalog);
        }
        if (p.ExplicitDbType is { } dbType)
        {
            var mapped = KingbaseParameterTypes.FromDbType(dbType);
            if (selected is not null)
            {
                var selectedProvider = Provider(selected.Name);
                if (!KingbaseParameterTypes.Compatible(dbType, selectedProvider)) throw InvalidBinding();
            }
            else selected = Lookup(KingbaseParameterTypes.Name(mapped), catalog);
        }
        if (selected is not null) return selected;
        if (p.Value is null or DBNull) throw new ArgumentException("SQL NULL requires an explicit parameter type.");
        var inferred = KingbaseParameterTypes.Infer(p.Value) ?? throw KingbaseParameterTypes.Unsupported();
        return Lookup(KingbaseParameterTypes.Name(inferred), catalog);
    }

    static PgTypeDescription Lookup(string name, PgTypeCatalog catalog) =>
        catalog.ByName.TryGetValue("sys_catalog." + name, out var type) ? type : throw KingbaseParameterTypes.Unsupported();
    static KingbaseDbType Provider(string name)
    {
        foreach (KingbaseDbType type in Enum.GetValues(typeof(KingbaseDbType)))
            if (KingbaseParameterTypes.Name(type) == name) return type;
        throw KingbaseParameterTypes.Unsupported();
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
