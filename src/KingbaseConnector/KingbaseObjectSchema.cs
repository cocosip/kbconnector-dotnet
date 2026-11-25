using System.Data;
using System.Globalization;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

internal static class KingbaseObjectSchema
{
    internal sealed class Definition(string name, int identifierParts, string[] restrictions, string[] filterColumns,
        (string Name, Type Type, bool Required)[] fields, string? source = null, string[]? orderColumns = null)
    {
        internal string Name { get; } = name;
        internal int IdentifierParts { get; } = identifierParts;
        internal string[] Restrictions { get; } = restrictions;
        internal string[] FilterColumns { get; } = filterColumns;
        internal (string Name, Type Type, bool Required)[] Fields { get; } = fields;
        internal string Source { get; } = source ?? "information_schema." + (name == "Schemas" ? "schemata" : name.ToLowerInvariant());
        internal string[] OrderColumns { get; } = orderColumns ?? (name == "Columns" ?
            filterColumns.Take(3).Concat(["ordinal_position"]).ToArray() : filterColumns.Take(identifierParts).ToArray());
    }

    internal static readonly Definition[] All =
    [
        new("Schemas", 2, ["Catalog", "Schema"], ["catalog_name", "schema_name"],
            [Text("CATALOG_NAME", true), Text("SCHEMA_NAME", true), Text("SCHEMA_OWNER", true)]),
        new("Tables", 3, ["Catalog", "Schema", "Table", "TableType"], ["table_catalog", "table_schema", "table_name", "table_type"],
            [Text("TABLE_CATALOG", true), Text("TABLE_SCHEMA", true), Text("TABLE_NAME", true), Text("TABLE_TYPE", true)]),
        new("Views", 3, ["Catalog", "Schema", "Table"], ["table_catalog", "table_schema", "table_name"],
            [Text("TABLE_CATALOG", true), Text("TABLE_SCHEMA", true), Text("TABLE_NAME", true), Text("VIEW_DEFINITION"), Text("CHECK_OPTION", true), Flag("IS_UPDATABLE")]),
        new("Columns", 4, ["Catalog", "Schema", "Table", "Column"], ["table_catalog", "table_schema", "table_name", "column_name"],
            [Text("TABLE_CATALOG", true), Text("TABLE_SCHEMA", true), Text("TABLE_NAME", true), Text("COLUMN_NAME", true),
                Number("ORDINAL_POSITION", true), Text("COLUMN_DEFAULT"), Flag("IS_NULLABLE"), Text("DATA_TYPE", true),
                Length("CHARACTER_MAXIMUM_LENGTH"), Length("CHARACTER_OCTET_LENGTH"), Number("NUMERIC_PRECISION"), Number("NUMERIC_PRECISION_RADIX"),
                Number("NUMERIC_SCALE"), Number("DATETIME_PRECISION"), Text("INTERVAL_TYPE"), Number("INTERVAL_PRECISION"),
                Text("UDT_CATALOG", true), Text("UDT_SCHEMA", true), Text("UDT_NAME", true), Text("DOMAIN_CATALOG"), Text("DOMAIN_SCHEMA"), Text("DOMAIN_NAME"),
                Text("COLLATION_CATALOG"), Text("COLLATION_SCHEMA"), Text("COLLATION_NAME"),
                Flag("IS_IDENTITY"), Text("IDENTITY_GENERATION"), LongNumber("IDENTITY_START"), LongNumber("IDENTITY_INCREMENT"),
                LongNumber("IDENTITY_MAXIMUM"), LongNumber("IDENTITY_MINIMUM"), Flag("IDENTITY_CYCLE", false),
                Text("IS_GENERATED", true), Text("GENERATION_EXPRESSION")]),
        KingbaseGenerationSchema.Sequences,
        .. KingbaseRelationalSchema.All,
        .. KingbaseDirectorySchema.All,
        .. KingbaseRoutineSchema.All,
        .. KingbaseUdtSchema.All
    ];

    internal static Definition? Find(string name) => All.FirstOrDefault(item => item.Name == name);

    internal static KingbaseCommand Command(Definition definition, string?[]? restrictions, KingbaseConnection connection, KingbaseTransaction? transaction)
    {
        var projection = string.Join(", ", definition.Fields.Select(field => (definition.Name == "Columns" && field.Name == "IDENTITY_CYCLE" ?
            "(CASE WHEN is_identity::pg_catalog.text OPERATOR(pg_catalog.=) 'YES' THEN identity_cycle END)" : "\"" + field.Name.ToLowerInvariant() + "\"") + "::pg_catalog." +
            WireType(field.Type) + " AS \"" + field.Name + "\""));
        var command = new KingbaseCommand { Connection = connection, Transaction = transaction,
            ValidateResultSchema = (results, catalog) => ValidateHeader(definition, results, catalog) };
        var predicates = new List<string>();
        for (var i = 0; i < (restrictions?.Length ?? 0); i++)
        {
            if (restrictions![i] is not { } value) continue;
            var parameter = "r" + i;
            predicates.Add(definition.FilterColumns[i] + "::pg_catalog.text COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) @" + parameter);
            command.Parameters.Add(new KingbaseParameter(parameter, value) { KingbaseDbType = KingbaseDbType.Text });
        }
        var order = definition.OrderColumns.Select(column => column + (definition.Fields.First(field =>
            field.Name.Equals(column, StringComparison.OrdinalIgnoreCase)).Type == typeof(string) ? "::pg_catalog.text COLLATE pg_catalog.\"C\"" : ""));
        command.CommandText = "SELECT " + projection + " FROM " + definition.Source +
            (predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates)) + " ORDER BY " + string.Join(", ", order);
        return command;
    }

    static string WireType(Type type) => type == typeof(int) ? "int4" : type == typeof(long) ? "int8" : "text";

    static void ValidateHeader(Definition definition, IReadOnlyList<PgTextResult> results, PgTypeCatalog catalog)
    {
        if (results.Count != 1 || results[0].Columns.Count != definition.Fields.Length) throw InvalidMetadata();
        for (var i = 0; i < definition.Fields.Length; i++)
        {
            var field = definition.Fields[i];
            var column = results[0].Columns[i];
            var type = catalog.ByName["pg_catalog." + WireType(field.Type)];
            if (column.Name != field.Name || column.TypeOid != type.Oid || column.TypeSize != type.Size)
                throw InvalidMetadata();
        }
    }

    internal static DataTable Read(Definition definition, KingbaseDataReader reader, CancellationToken token)
    {
        var table = KingbaseSchema.Table(definition.Name, definition.Fields.Select(field => (field.Name, field.Type)).ToArray());
        var unique = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (reader.FieldCount != definition.Fields.Length) throw InvalidMetadata();
            for (var i = 0; i < definition.Fields.Length; i++)
                if (reader.GetFieldType(i) != (definition.Fields[i].Type == typeof(bool) ? typeof(string) : definition.Fields[i].Type))
                    throw InvalidMetadata();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var values = new object[definition.Fields.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    var field = definition.Fields[i];
                    var value = reader.GetValue(i);
                    if (value is DBNull)
                    {
                        if (field.Required) throw InvalidMetadata();
                    }
                    else if (field.Type == typeof(bool))
                        value = value is "YES" ? true : value is "NO" ? false : throw InvalidMetadata();
                    else if (field.Required && value is string text && (text.IndexOf('\0') >= 0 || text.Length == 0 &&
                        !(definition.Name == "EnumLabels" && field.Name == "ENUM_LABEL")))
                        throw InvalidMetadata();
                    else if (value is long length &&
                        ((field.Name is "CHARACTER_MAXIMUM_LENGTH" or "CHARACTER_OCTET_LENGTH") && length < 0 ||
                            field.Name == "CACHE_SIZE" && length <= 0 || (field.Name is "INCREMENT" or "IDENTITY_INCREMENT") && length == 0) || value is int number &&
                        (field.Name == "ORDINAL_POSITION" ? number <= 0 : field.Name == "CONNECTION_LIMIT" ?
                            number < (definition.Name == "Databases" ? -2 : -1) : field.Name != "NUMERIC_SCALE" && number < 0))
                        throw InvalidMetadata();
                    values[i] = value;
                }
                if (!unique.Add(string.Join("\0", values.Take(definition.IdentifierParts).Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))))) throw InvalidMetadata();
                table.Rows.Add(values);
            }
            if (reader.NextResult()) throw InvalidMetadata();
            token.ThrowIfCancellationRequested();
            return table;
        }
        catch (Exception error) when (error is FormatException or OverflowException)
        { table.Dispose(); throw InvalidMetadata(); }
        catch { table.Dispose(); throw; }
    }

    internal static (string, Type, bool) Text(string name, bool required = false) => (name, typeof(string), required);
    internal static (string, Type, bool) Number(string name, bool required = false) => (name, typeof(int), required);
    internal static (string, Type, bool) LongNumber(string name, bool required = false) => (name, typeof(long), required);
    static (string, Type, bool) Length(string name) => (name, typeof(long), false);
    internal static (string, Type, bool) Flag(string name, bool required = true) => (name, typeof(bool), required);
    static IOException InvalidMetadata() => new("Invalid Kingbase object schema metadata.");
}
