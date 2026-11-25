using static KingbaseConnector.KingbaseObjectSchema;

namespace KingbaseConnector;

internal static class KingbaseUdtSchema
{
    const string Identity = "pg_catalog.current_database() AS udt_catalog, n.nspname AS udt_schema, t.typname AS udt_name, ";
    const string TypeFrom = " FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid OPERATOR(pg_catalog.=) t.typnamespace ";
    const string Visible = "t.typisdefined AND NOT pg_catalog.pg_is_other_temp_schema(t.typnamespace) AND " +
        "(pg_catalog.pg_has_role(t.typowner,'USAGE') OR pg_catalog.has_type_privilege(t.oid,'USAGE'))";
    static readonly string[] Restrictions = ["Catalog", "Schema", "Type"];
    static readonly string[] Filters = ["udt_catalog", "udt_schema", "udt_name"];

    internal static readonly Definition[] All =
    [
        new("UserDefinedTypes", 3, Restrictions, Filters,
            [.. IdentityFields(), Text("TYPE_KIND_CODE", true), Text("TYPE_KIND", true), Text("OWNER", true),
                Text("BASE_TYPE_CATALOG"), Text("BASE_TYPE_SCHEMA"), Text("BASE_TYPE_NAME"), Text("DOMAIN_DEFAULT"), Flag("DOMAIN_IS_NULLABLE", false),
                .. TypeLimits(), .. CollationFields()],
            "(SELECT " + Identity + "t.typtype AS type_kind_code, CASE WHEN t.typtype OPERATOR(pg_catalog.=) 'e'::pg_catalog.\"char\" THEN 'ENUM' " +
            "WHEN t.typtype OPERATOR(pg_catalog.=) 'd'::pg_catalog.\"char\" THEN 'DOMAIN' WHEN t.typtype OPERATOR(pg_catalog.=) 'c'::pg_catalog.\"char\" " +
            "THEN 'COMPOSITE' END AS type_kind, pg_catalog.pg_get_userbyid(t.typowner) AS owner, " +
            "d.udt_catalog AS base_type_catalog, d.udt_schema AS base_type_schema, d.udt_name AS base_type_name, d.domain_default, " +
            "CASE WHEN t.typtype OPERATOR(pg_catalog.=) 'd'::pg_catalog.\"char\" THEN CASE WHEN t.typnotnull THEN 'NO' ELSE 'YES' END END AS domain_is_nullable, " +
            Limits("d") + ", d.collation_catalog, d.collation_schema, d.collation_name" + TypeFrom +
            "LEFT JOIN pg_catalog.pg_class c ON c.oid OPERATOR(pg_catalog.=) t.typrelid LEFT JOIN information_schema.domains d ON " +
            Exact("d.domain_schema", "n.nspname") + " AND " + Exact("d.domain_name", "t.typname") +
            " WHERE " + Visible + " AND (t.typtype OPERATOR(pg_catalog.=) 'e'::pg_catalog.\"char\" OR " +
            "t.typtype OPERATOR(pg_catalog.=) 'd'::pg_catalog.\"char\" OR (t.typtype OPERATOR(pg_catalog.=) 'c'::pg_catalog.\"char\" AND " +
            "c.relkind OPERATOR(pg_catalog.=) 'c'::pg_catalog.\"char\"))) AS udt_metadata"),
        new("TypeAttributes", 4, [.. Restrictions, "Attribute"], [.. Filters, "attribute_name"],
            [.. IdentityFields(), Number("ORDINAL_POSITION", true), Text("ATTRIBUTE_NAME", true), Text("ATTRIBUTE_DEFAULT"), Flag("IS_NULLABLE"),
                Text("DATA_TYPE", true), .. TypeLimits(), Text("ATTRIBUTE_UDT_CATALOG", true), Text("ATTRIBUTE_UDT_SCHEMA", true), Text("ATTRIBUTE_UDT_NAME", true), .. CollationFields()],
            "(SELECT " + Identity + "i.ordinal_position, i.attribute_name, i.attribute_default, i.is_nullable, i.data_type, " + Limits("i") +
            ", i.attribute_udt_catalog, i.attribute_udt_schema, i.attribute_udt_name, i.collation_catalog, i.collation_schema, i.collation_name" + TypeFrom +
            "JOIN information_schema.attributes i ON " + Exact("i.udt_schema", "n.nspname") + " AND " + Exact("i.udt_name", "t.typname") +
            " JOIN pg_catalog.pg_attribute a ON a.attrelid OPERATOR(pg_catalog.=) t.typrelid AND " +
            "a.attnum OPERATOR(pg_catalog.=) i.ordinal_position::pg_catalog.int4 WHERE " + Visible +
            " AND t.typtype OPERATOR(pg_catalog.=) 'c'::pg_catalog.\"char\" AND a.attnum OPERATOR(pg_catalog.>) 0 AND NOT a.attisdropped) AS udt_attributes",
            [.. Filters, "ordinal_position"]),
        new("EnumLabels", 4, [.. Restrictions, "Label"], [.. Filters, "enum_label"],
            [.. IdentityFields(), Number("ORDINAL_POSITION", true), Text("ENUM_LABEL", true)],
            "(SELECT " + Identity + "pg_catalog.row_number() OVER (PARTITION BY t.oid ORDER BY e.enumsortorder)::pg_catalog.int4 AS ordinal_position, " +
            "e.enumlabel AS enum_label" + TypeFrom + "JOIN pg_catalog.pg_enum e ON e.enumtypid OPERATOR(pg_catalog.=) t.oid WHERE " + Visible +
            " AND t.typtype OPERATOR(pg_catalog.=) 'e'::pg_catalog.\"char\") AS enum_labels", [.. Filters, "ordinal_position"]),
        new("DomainConstraints", 4, [.. Restrictions, "Constraint"], [.. Filters, "constraint_name"],
            [.. IdentityFields(), Text("CONSTRAINT_NAME", true), Text("CONSTRAINT_CATALOG", true), Text("CONSTRAINT_SCHEMA", true),
                Text("CONSTRAINT_TYPE_CODE", true), Text("CONSTRAINT_TYPE"), Flag("IS_DEFERRABLE"), Flag("INITIALLY_DEFERRED"), Flag("IS_VALIDATED"), Text("DEFINITION")],
            "(SELECT " + Identity + "c.conname AS constraint_name, pg_catalog.current_database() AS constraint_catalog, cn.nspname AS constraint_schema, " +
            "c.contype AS constraint_type_code, CASE WHEN c.contype OPERATOR(pg_catalog.=) 'c'::pg_catalog.\"char\" THEN 'CHECK' END AS constraint_type, " +
            Yes("c.condeferrable") + " AS is_deferrable, " + Yes("c.condeferred") + " AS initially_deferred, " + Yes("c.convalidated") + " AS is_validated, " +
            "CASE WHEN pg_catalog.pg_has_role(t.typowner,'USAGE') THEN pg_catalog.pg_get_constraintdef(c.oid,true) END AS definition" + TypeFrom +
            "JOIN pg_catalog.pg_constraint c ON c.contypid OPERATOR(pg_catalog.=) t.oid JOIN pg_catalog.pg_namespace cn ON " +
            "cn.oid OPERATOR(pg_catalog.=) c.connamespace WHERE " + Visible + " AND t.typtype OPERATOR(pg_catalog.=) 'd'::pg_catalog.\"char\") AS domain_constraints")
    ];

    static (string, Type, bool)[] IdentityFields() => [Text("UDT_CATALOG", true), Text("UDT_SCHEMA", true), Text("UDT_NAME", true)];
    static (string, Type, bool)[] TypeLimits() =>
        [LongNumber("CHARACTER_MAXIMUM_LENGTH"), LongNumber("CHARACTER_OCTET_LENGTH"), Number("NUMERIC_PRECISION"), Number("NUMERIC_PRECISION_RADIX"),
            Number("NUMERIC_SCALE"), Number("DATETIME_PRECISION"), Text("INTERVAL_TYPE"), Number("INTERVAL_PRECISION")];
    static (string, Type, bool)[] CollationFields() => [Text("COLLATION_CATALOG"), Text("COLLATION_SCHEMA"), Text("COLLATION_NAME")];
    static string Limits(string alias) => string.Join(", ", TypeLimits().Select(field => alias + "." + field.Item1.ToLowerInvariant()));
    static string Exact(string left, string right) => left + "::pg_catalog.text COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) " + right + "::pg_catalog.text";
    static string Yes(string expression) => "CASE WHEN " + expression + " THEN 'YES' ELSE 'NO' END";
}
