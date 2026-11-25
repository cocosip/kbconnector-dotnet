using System.Data;
using System.Data.Common;
using System.Globalization;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

internal static class KingbaseSchema
{
    internal const string KeywordsSql = "SELECT word::text FROM pg_catalog.pg_get_keywords() WHERE catcode = 'R' ORDER BY word";
    static readonly string[] Names = ["MetaDataCollections", "Restrictions", "DataSourceInformation", "DataTypes", "ReservedWords"];

    internal static string Resolve(string? name, string?[]? restrictions)
    {
        if (restrictions is { Length: > 0 }) throw new ArgumentException("This schema collection does not support restrictions.", nameof(restrictions));
        if (name is null) return Names[0];
        return Names.FirstOrDefault(value => value.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
            throw new ArgumentException("Unknown or unimplemented Kingbase schema collection.", nameof(name));
    }

    internal static DataTable Create(string name, KingbaseProductVersion version, PgTypeCatalog catalog)
    {
        switch (name)
        {
            case "MetaDataCollections":
                var collections = Table(name, ("CollectionName", typeof(string)), ("NumberOfRestrictions", typeof(int)), ("NumberOfIdentifierParts", typeof(int)));
                foreach (var item in Names) collections.Rows.Add(item, 0, 0);
                return collections;
            case "Restrictions":
                return Table(name, ("CollectionName", typeof(string)), ("RestrictionName", typeof(string)), ("RestrictionDefault", typeof(string)), ("RestrictionNumber", typeof(int)));
            case "DataSourceInformation": return Source(version);
            case "DataTypes": return Types(catalog);
            default: throw new ArgumentException("This schema collection requires server retrieval.", nameof(name));
        }
    }

    static DataTable Source(KingbaseProductVersion version)
    {
        var table = Table("DataSourceInformation",
            ("CompositeIdentifierSeparatorPattern", typeof(string)), ("DataSourceProductName", typeof(string)),
            ("DataSourceProductVersion", typeof(string)), ("DataSourceProductVersionNormalized", typeof(string)),
            ("GroupByBehavior", typeof(GroupByBehavior)), ("IdentifierPattern", typeof(string)),
            ("IdentifierCase", typeof(IdentifierCase)), ("OrderByColumnsInSelect", typeof(bool)),
            ("ParameterMarkerFormat", typeof(string)), ("ParameterMarkerPattern", typeof(string)),
            ("ParameterNameMaxLength", typeof(int)), ("ParameterNamePattern", typeof(string)),
            ("QuotedIdentifierPattern", typeof(string)), ("QuotedIdentifierCase", typeof(IdentifierCase)),
            ("StatementSeparatorPattern", typeof(string)), ("StringLiteralPattern", typeof(string)),
            ("SupportedJoinOperators", typeof(SupportedJoinOperators)));
        var row = table.NewRow();
        row["CompositeIdentifierSeparatorPattern"] = @"\.";
        row["DataSourceProductName"] = "KingbaseES";
        row["DataSourceProductVersion"] = version.RawText;
        row["IdentifierCase"] = IdentifierCase.Insensitive;
        row["ParameterMarkerFormat"] = "@{0}";
        row["ParameterMarkerPattern"] = @"[@:]([A-Za-z_][A-Za-z0-9_]{0,127})";
        row["ParameterNameMaxLength"] = 128;
        row["ParameterNamePattern"] = @"([A-Za-z_][A-Za-z0-9_]{0,127})";
        row["QuotedIdentifierPattern"] = "\"((?:[^\"]|\"\")*)\"";
        row["QuotedIdentifierCase"] = IdentifierCase.Sensitive;
        // Unknown server capabilities and non-comparable partial versions remain DBNull.
        table.Rows.Add(row);
        return table;
    }

    static DataTable Types(PgTypeCatalog catalog)
    {
        var table = Table("DataTypes",
            ("TypeName", typeof(string)), ("ProviderDbType", typeof(int)), ("ColumnSize", typeof(long)),
            ("CreateFormat", typeof(string)), ("CreateParameters", typeof(string)), ("DataType", typeof(string)),
            ("IsAutoIncrementable", typeof(bool)), ("IsBestMatch", typeof(bool)), ("IsCaseSensitive", typeof(bool)),
            ("IsFixedLength", typeof(bool)), ("IsFixedPrecisionScale", typeof(bool)), ("IsLong", typeof(bool)),
            ("IsNullable", typeof(bool)), ("IsSearchable", typeof(bool)), ("IsSearchableWithLike", typeof(bool)),
            ("IsUnsigned", typeof(bool)), ("MaximumScale", typeof(short)), ("MinimumScale", typeof(short)),
            ("IsConcurrencyType", typeof(bool)), ("IsLiteralSupported", typeof(bool)),
            ("LiteralPrefix", typeof(string)), ("LiteralSuffix", typeof(string)), ("NativeDataType", typeof(string)),
            ("TypeOid", typeof(uint)), ("ProviderSpecificDataType", typeof(string)));
        var codecs = new PgTextCodecs(catalog);
        foreach (KingbaseDbType provider in Enum.GetValues(typeof(KingbaseDbType)))
        {
            var name = KingbaseParameterTypes.Name(provider);
            if (!catalog.ByName.TryGetValue("pg_catalog." + name, out var type)) continue;
            Type clr;
            try { clr = codecs.GetFieldType(type.Oid); }
            catch (NotSupportedException) { continue; }
            var row = table.NewRow();
            row["TypeName"] = name; row["ProviderDbType"] = (int)provider;
            row["DataType"] = clr.FullName!; row["TypeOid"] = type.Oid;
            row["ProviderSpecificDataType"] = codecs.GetProviderSpecificFieldType(type.Oid).FullName!;
            row["CreateFormat"] = "pg_catalog.\"" + name + "\"";
            row["CreateParameters"] = "";
            row["IsBestMatch"] = false;
            if (name != "bpchar") row["IsFixedLength"] = type.Size > 0;
            row["IsLong"] = clr == typeof(string) || clr == typeof(byte[]);
            row["IsNullable"] = true;
            if (name == "oid") row["IsUnsigned"] = true;
            else if (name is "int2" or "int4" or "int8" or "float4" or "float8" or "numeric") row["IsUnsigned"] = false;
            table.Rows.Add(row);
        }
        // Select only from supported rows that survived catalog/layout filtering.
        foreach (var group in table.Rows.Cast<DataRow>().GroupBy(row => (string)row["DataType"]))
        {
            var preferred = group.FirstOrDefault(row => (string)row["TypeName"] is "text" or "timestamp") ?? group.First();
            preferred["IsBestMatch"] = true;
        }
        return table;
    }

    internal static DataTable ReadWords(KingbaseDataReader reader, CancellationToken token)
    {
        var table = Table("ReservedWords", ("ReservedWord", typeof(string)));
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (reader.FieldCount != 1 || reader.GetFieldType(0) != typeof(string)) throw InvalidWords();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.IsDBNull(0)) throw InvalidWords();
                var word = reader.GetString(0);
                if (word.Length is 0 or > 128 || word.Any(c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) || !unique.Add(word))
                    throw InvalidWords();
                table.Rows.Add(word);
            }
            if (reader.NextResult()) throw InvalidWords();
            token.ThrowIfCancellationRequested();
            return table;
        }
        catch { table.Dispose(); throw; }
    }

    static DataTable Table(string name, params (string Name, Type Type)[] columns)
    {
        var table = new DataTable(name) { Locale = CultureInfo.InvariantCulture };
        foreach (var column in columns) table.Columns.Add(column.Name, column.Type);
        return table;
    }
    static IOException InvalidWords() => new("Invalid or oversized Kingbase reserved-word metadata.");
}
