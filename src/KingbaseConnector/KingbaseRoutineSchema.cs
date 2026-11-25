using static KingbaseConnector.KingbaseObjectSchema;

namespace KingbaseConnector;

internal static class KingbaseRoutineSchema
{
    const string Identity = "r.routine_catalog, r.routine_schema, r.routine_name, r.specific_name, ";
    const string CatalogJoin = "FROM information_schema.routines r JOIN pg_catalog.pg_namespace n ON " +
        "n.nspname::pg_catalog.text COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) r.routine_schema::pg_catalog.text " +
        "JOIN pg_catalog.pg_proc p ON p.pronamespace OPERATOR(pg_catalog.=) n.oid AND " +
        "pg_catalog.nameconcatoid(p.proname,p.oid)::pg_catalog.text COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) r.specific_name::pg_catalog.text ";
    const string VisibleSession = "NOT pg_catalog.pg_is_other_temp_schema(p.pronamespace)";
    static readonly string[] Restrictions = ["Catalog", "Schema", "Routine", "SpecificName"];
    static readonly string[] Filters = ["routine_catalog", "routine_schema", "routine_name", "specific_name"];

    internal static readonly Definition[] All = [Routine("Functions", "f"), Routine("Procedures", "p"), Parameters()];

    static Definition Routine(string name, string kind) => new(name, 4, Restrictions, Filters,
        [.. IdentityFields(), Text("ROUTINE_TYPE", true), Text("DATA_TYPE"), .. TypeLimits(),
            Text("UDT_CATALOG"), Text("UDT_SCHEMA"), Text("UDT_NAME"), Flag("RETURNS_SET"),
            Number("INPUT_PARAMETER_COUNT", true), Number("DEFAULT_PARAMETER_COUNT", true), Text("EXTERNAL_LANGUAGE", true),
            Flag("IS_DETERMINISTIC"), Text("SECURITY_TYPE", true), Flag("IS_NULL_CALL", false)],
        "(SELECT " + Identity + "r.routine_type, r.data_type, " + Limits("r") +
        "r.type_udt_catalog AS udt_catalog, r.type_udt_schema AS udt_schema, r.type_udt_name AS udt_name, " +
        "CASE WHEN p.proretset THEN 'YES' ELSE 'NO' END AS returns_set, p.pronargs AS input_parameter_count, " +
        "p.pronargdefaults AS default_parameter_count, r.external_language, r.is_deterministic, r.security_type, r.is_null_call " +
        CatalogJoin + "WHERE p.prokind OPERATOR(pg_catalog.=) '" + kind + "'::pg_catalog.\"char\" AND " + VisibleSession + ") AS routine_metadata");

    static Definition Parameters() => new("ProcedureParameters", 5, [.. Restrictions, "Parameter"], [.. Filters, "parameter_name"],
        [.. IdentityFields(), Number("ORDINAL_POSITION", true), Text("PARAMETER_NAME"), Text("PARAMETER_MODE"),
            Flag("IS_VARIADIC"), Flag("IS_TABLE_COLUMN"), Text("DATA_TYPE", true), .. TypeLimits(),
            Text("UDT_CATALOG", true), Text("UDT_SCHEMA", true), Text("UDT_NAME", true), Text("PARAMETER_DEFAULT")],
        "(SELECT " + Identity + "a.ordinal_position, a.parameter_name, a.parameter_mode, " +
        ModeFlag("v") + " AS is_variadic, " + ModeFlag("t") + " AS is_table_column, a.data_type, " + Limits("a") +
        "a.udt_catalog, a.udt_schema, a.udt_name, a.parameter_default " + CatalogJoin +
        "JOIN information_schema.parameters a ON a.specific_schema::pg_catalog.text COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) r.specific_schema::pg_catalog.text " +
        "AND a.specific_name::pg_catalog.text COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) r.specific_name::pg_catalog.text " +
        "WHERE (p.prokind OPERATOR(pg_catalog.=) 'f'::pg_catalog.\"char\" OR p.prokind OPERATOR(pg_catalog.=) 'p'::pg_catalog.\"char\") AND " + VisibleSession + ") AS routine_parameters",
        [.. Filters, "ordinal_position"]);

    static (string, Type, bool)[] IdentityFields() =>
        [Text("ROUTINE_CATALOG", true), Text("ROUTINE_SCHEMA", true), Text("ROUTINE_NAME", true), Text("SPECIFIC_NAME", true)];
    static (string, Type, bool)[] TypeLimits() =>
        [LongNumber("CHARACTER_MAXIMUM_LENGTH"), LongNumber("CHARACTER_OCTET_LENGTH"), Number("NUMERIC_PRECISION"), Number("NUMERIC_PRECISION_RADIX"),
            Number("NUMERIC_SCALE"), Number("DATETIME_PRECISION"), Text("INTERVAL_TYPE"), Number("INTERVAL_PRECISION")];
    static string Limits(string alias) => string.Join(", ", TypeLimits().Select(field => alias + "." + field.Item1.ToLowerInvariant())) + ", ";
    static string ModeFlag(string mode) => "CASE WHEN p.proargmodes[(pg_catalog.array_lower(p.proargmodes,1) OPERATOR(pg_catalog.+) " +
        "a.ordinal_position::pg_catalog.int4) OPERATOR(pg_catalog.-) 1] OPERATOR(pg_catalog.=) '" + mode + "'::pg_catalog.\"char\" THEN 'YES' ELSE 'NO' END";
}
