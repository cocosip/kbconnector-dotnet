using static KingbaseConnector.KingbaseObjectSchema;

namespace KingbaseConnector;

internal static class KingbaseRelationalSchema
{
    const string Database = "pg_catalog.current_database()";
    const string ConstraintFrom = " FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class r ON r.oid OPERATOR(pg_catalog.=) c.conrelid " +
        "JOIN pg_catalog.pg_namespace n ON n.oid OPERATOR(pg_catalog.=) r.relnamespace ";
    const string IndexFrom = " FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class r ON r.oid OPERATOR(pg_catalog.=) i.indrelid " +
        "JOIN pg_catalog.pg_namespace n ON n.oid OPERATOR(pg_catalog.=) r.relnamespace " +
        "JOIN pg_catalog.pg_class x ON x.oid OPERATOR(pg_catalog.=) i.indexrelid " +
        "JOIN pg_catalog.pg_namespace xn ON xn.oid OPERATOR(pg_catalog.=) x.relnamespace " +
        "JOIN pg_catalog.pg_am am ON am.oid OPERATOR(pg_catalog.=) x.relam ";
    const string Visible = "NOT pg_catalog.pg_is_other_temp_schema(r.relnamespace) AND (pg_catalog.pg_has_role(r.relowner, 'USAGE') OR " +
        "pg_catalog.has_table_privilege(r.oid, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER') OR " +
        "pg_catalog.has_any_column_privilege(r.oid, 'SELECT, INSERT, UPDATE, REFERENCES'))";
    const string TableIdentity = Database + " AS table_catalog, n.nspname AS table_schema, r.relname AS table_name, ";
    const string IndexIdentity = Database + " AS index_catalog, xn.nspname AS index_schema, x.relname AS index_name, ";
    const string ReferenceFrom = "JOIN pg_catalog.pg_class rr ON rr.oid OPERATOR(pg_catalog.=) c.confrelid " +
        "JOIN pg_catalog.pg_namespace rn ON rn.oid OPERATOR(pg_catalog.=) rr.relnamespace ";
    const string ReferenceIdentity = Database + " AS referenced_table_catalog, rn.nspname AS referenced_table_schema, rr.relname AS referenced_table_name, ";
    const string KeyPosition = "i.indkey[pg_catalog.array_lower(i.indkey,1) OPERATOR(pg_catalog.+) k.position OPERATOR(pg_catalog.-) 1]";
    const string Key = "k.position OPERATOR(pg_catalog.<=) i.indnkeyatts";
    const string BtreeKey = "am.amname OPERATOR(pg_catalog.=) 'btree' AND " + Key;
    static readonly string ConstraintType = Code("c.contype", [("c", "CHECK"), ("f", "FOREIGN KEY"), ("p", "PRIMARY KEY"),
        ("u", "UNIQUE"), ("x", "EXCLUSION"), ("t", "TRIGGER"), ("n", "NOT NULL")]);

    internal static readonly Definition[] All =
    [
        ConstraintColumns("PrimaryKeys", "p"),
        ConstraintColumns("UniqueKeys", "u"),
        new("ForeignKeys", 4, ["Catalog", "Schema", "Table", "Constraint"], ["table_catalog", "table_schema", "table_name", "constraint_name"],
            [Text("TABLE_CATALOG", true), Text("TABLE_SCHEMA", true), Text("TABLE_NAME", true), Text("CONSTRAINT_NAME", true),
                Text("REFERENCED_TABLE_CATALOG", true), Text("REFERENCED_TABLE_SCHEMA", true), Text("REFERENCED_TABLE_NAME", true), Text("REFERENCED_INDEX_NAME", true),
                Text("MATCH_OPTION", true), Text("UPDATE_RULE", true), Text("DELETE_RULE", true), Flag("IS_DEFERRABLE"), Flag("INITIALLY_DEFERRED"), Flag("IS_VALIDATED")],
            Source("SELECT " + TableIdentity + "c.conname AS constraint_name, " + ReferenceIdentity + "rx.relname AS referenced_index_name, " +
                Code("c.confmatchtype", [("s", "SIMPLE"), ("f", "FULL"), ("p", "PARTIAL")]) + " AS match_option, " +
                Action("c.confupdtype") + " AS update_rule, " + Action("c.confdeltype") + " AS delete_rule, " +
                Yes("c.condeferrable") + " AS is_deferrable, " + Yes("c.condeferred") + " AS initially_deferred, " + Yes("c.convalidated") + " AS is_validated" +
                ConstraintFrom + ReferenceFrom + "JOIN pg_catalog.pg_class rx ON rx.oid OPERATOR(pg_catalog.=) c.conindid " +
                "WHERE c.contype OPERATOR(pg_catalog.=) 'f' AND " + Visible), ["table_catalog", "table_schema", "table_name", "constraint_name"]),
        new("ForeignKeyColumns", 5, ["Catalog", "Schema", "Table", "Constraint", "Column"], ["table_catalog", "table_schema", "table_name", "constraint_name", "column_name"],
            [Text("TABLE_CATALOG", true), Text("TABLE_SCHEMA", true), Text("TABLE_NAME", true), Text("CONSTRAINT_NAME", true), Text("COLUMN_NAME", true),
                Number("ORDINAL_POSITION", true), Text("REFERENCED_TABLE_CATALOG", true), Text("REFERENCED_TABLE_SCHEMA", true), Text("REFERENCED_TABLE_NAME", true), Text("REFERENCED_COLUMN_NAME", true)],
            Source("SELECT " + TableIdentity + "c.conname AS constraint_name, a.attname AS column_name, k.position AS ordinal_position, " +
                ReferenceIdentity + "ra.attname AS referenced_column_name" + ConstraintFrom + ReferenceFrom +
                "CROSS JOIN pg_catalog.generate_series(1,pg_catalog.array_length(c.conkey,1)) AS k(position) " +
                "JOIN pg_catalog.pg_attribute a ON a.attrelid OPERATOR(pg_catalog.=) c.conrelid AND " +
                "a.attnum OPERATOR(pg_catalog.=) c.conkey[pg_catalog.array_lower(c.conkey,1) OPERATOR(pg_catalog.+) k.position OPERATOR(pg_catalog.-) 1] " +
                "JOIN pg_catalog.pg_attribute ra ON ra.attrelid OPERATOR(pg_catalog.=) c.confrelid AND " +
                "ra.attnum OPERATOR(pg_catalog.=) c.confkey[pg_catalog.array_lower(c.confkey,1) OPERATOR(pg_catalog.+) k.position OPERATOR(pg_catalog.-) 1] " +
                "WHERE c.contype OPERATOR(pg_catalog.=) 'f' AND " + Visible), ["table_catalog", "table_schema", "table_name", "constraint_name", "ordinal_position"]),
        new("Indexes", 3, ["Catalog", "Schema", "Table", "Index"], ["table_catalog", "table_schema", "table_name", "index_name"],
            [Text("INDEX_CATALOG", true), Text("INDEX_SCHEMA", true), Text("INDEX_NAME", true), Text("TABLE_CATALOG", true), Text("TABLE_SCHEMA", true), Text("TABLE_NAME", true),
                Text("INDEX_TYPE", true), Flag("IS_UNIQUE"), Flag("IS_PRIMARY"), Flag("IS_VALID"), Flag("IS_READY"), Flag("IS_LIVE"), Flag("IS_CLUSTERED"),
                Number("KEY_COLUMN_COUNT", true), Number("COLUMN_COUNT", true), Text("PREDICATE"), Text("EXPRESSION"), Text("DEFINITION", true)],
            Source("SELECT " + IndexIdentity + TableIdentity + "am.amname AS index_type, " +
                Yes("i.indisunique") + " AS is_unique, " + Yes("i.indisprimary") + " AS is_primary, " + Yes("i.indisvalid") + " AS is_valid, " +
                Yes("i.indisready") + " AS is_ready, " + Yes("i.indislive") + " AS is_live, " + Yes("i.indisclustered") + " AS is_clustered, " +
                "i.indnkeyatts AS key_column_count, i.indnatts AS column_count, pg_catalog.pg_get_expr(i.indpred,i.indrelid) AS predicate, " +
                "pg_catalog.pg_get_expr(i.indexprs,i.indrelid) AS expression, pg_catalog.pg_get_indexdef(i.indexrelid) AS definition" +
                IndexFrom + "WHERE " + Visible), ["index_catalog", "index_schema", "index_name"]),
        new("IndexColumns", 4, ["Catalog", "Schema", "Table", "Index", "Column"], ["table_catalog", "table_schema", "table_name", "index_name", "column_name"],
            [Text("INDEX_CATALOG", true), Text("INDEX_SCHEMA", true), Text("INDEX_NAME", true), Number("ORDINAL_POSITION", true),
                Text("TABLE_CATALOG", true), Text("TABLE_SCHEMA", true), Text("TABLE_NAME", true), Text("COLUMN_NAME"), Text("COLUMN_EXPRESSION"), Flag("IS_INCLUDED"),
                Flag("IS_DESCENDING", false), Flag("NULLS_FIRST", false), Text("COLLATION_CATALOG"), Text("COLLATION_SCHEMA"), Text("COLLATION_NAME"),
                Text("OPERATOR_CLASS_CATALOG"), Text("OPERATOR_CLASS_SCHEMA"), Text("OPERATOR_CLASS_NAME")],
            Source("SELECT " + IndexIdentity + "k.position AS ordinal_position, " + TableIdentity + "a.attname AS column_name, " +
                "CASE WHEN " + KeyPosition + " OPERATOR(pg_catalog.=) 0 THEN pg_catalog.pg_get_indexdef(i.indexrelid,k.position,true) END AS column_expression, " +
                Yes("k.position OPERATOR(pg_catalog.>) i.indnkeyatts") + " AS is_included, " +
                "CASE WHEN " + BtreeKey + " THEN " + Yes("(i.indoption[pg_catalog.array_lower(i.indoption,1) OPERATOR(pg_catalog.+) k.position OPERATOR(pg_catalog.-) 1] OPERATOR(pg_catalog.&) 1) OPERATOR(pg_catalog.<>) 0") + " END AS is_descending, " +
                "CASE WHEN " + BtreeKey + " THEN " + Yes("(i.indoption[pg_catalog.array_lower(i.indoption,1) OPERATOR(pg_catalog.+) k.position OPERATOR(pg_catalog.-) 1] OPERATOR(pg_catalog.&) 2) OPERATOR(pg_catalog.<>) 0") + " END AS nulls_first, " +
                "CASE WHEN co.oid IS NOT NULL THEN " + Database + " END AS collation_catalog, cn.nspname AS collation_schema, co.collname AS collation_name, " +
                "CASE WHEN op.oid IS NOT NULL THEN " + Database + " END AS operator_class_catalog, onsp.nspname AS operator_class_schema, op.opcname AS operator_class_name" +
                IndexFrom + "CROSS JOIN pg_catalog.generate_series(1,i.indnatts::pg_catalog.int4) AS k(position) " +
                "LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid OPERATOR(pg_catalog.=) i.indrelid AND a.attnum OPERATOR(pg_catalog.=) " + KeyPosition + " " +
                "LEFT JOIN pg_catalog.pg_collation co ON co.oid OPERATOR(pg_catalog.=) i.indcollation[pg_catalog.array_lower(i.indcollation,1) OPERATOR(pg_catalog.+) k.position OPERATOR(pg_catalog.-) 1] " +
                "LEFT JOIN pg_catalog.pg_namespace cn ON cn.oid OPERATOR(pg_catalog.=) co.collnamespace " +
                "LEFT JOIN pg_catalog.pg_opclass op ON op.oid OPERATOR(pg_catalog.=) i.indclass[pg_catalog.array_lower(i.indclass,1) OPERATOR(pg_catalog.+) k.position OPERATOR(pg_catalog.-) 1] " +
                "LEFT JOIN pg_catalog.pg_namespace onsp ON onsp.oid OPERATOR(pg_catalog.=) op.opcnamespace " +
                "WHERE " + Visible), ["index_catalog", "index_schema", "index_name", "ordinal_position"]),
        new("Constraints", 4, ["Catalog", "Schema", "Table", "Constraint"], ["table_catalog", "table_schema", "table_name", "constraint_name"],
            [Text("TABLE_CATALOG", true), Text("TABLE_SCHEMA", true), Text("TABLE_NAME", true), Text("CONSTRAINT_NAME", true),
                Text("CONSTRAINT_TYPE_CODE", true), Text("CONSTRAINT_TYPE"), Flag("IS_DEFERRABLE"), Flag("INITIALLY_DEFERRED"),
                Flag("IS_VALIDATED"), Flag("NO_INHERIT"), Text("DEFINITION", true)],
            Source("SELECT " + TableIdentity + "c.conname AS constraint_name, c.contype AS constraint_type_code, " + ConstraintType + " AS constraint_type, " +
                Yes("c.condeferrable") + " AS is_deferrable, " + Yes("c.condeferred") + " AS initially_deferred, " +
                Yes("c.convalidated") + " AS is_validated, " + Yes("c.connoinherit") + " AS no_inherit, " +
                "pg_catalog.pg_get_constraintdef(c.oid,true) AS definition" + ConstraintFrom + "WHERE " + Visible)),
        new("ConstraintColumns", 5, ["Catalog", "Schema", "Table", "Constraint", "Column"], ["table_catalog", "table_schema", "table_name", "constraint_name", "column_name"],
            [Text("TABLE_CATALOG", true), Text("TABLE_SCHEMA", true), Text("TABLE_NAME", true), Text("CONSTRAINT_NAME", true),
                Number("ORDINAL_POSITION", true), Text("COLUMN_NAME", true), Text("CONSTRAINT_TYPE_CODE", true), Text("CONSTRAINT_TYPE")],
            Source("SELECT " + TableIdentity + "c.conname AS constraint_name, k.position AS ordinal_position, a.attname AS column_name, " +
                "c.contype AS constraint_type_code, " + ConstraintType + " AS constraint_type" + ConstraintFrom +
                "CROSS JOIN pg_catalog.generate_series(1,pg_catalog.array_length(c.conkey,1)) AS k(position) " +
                "JOIN pg_catalog.pg_attribute a ON a.attrelid OPERATOR(pg_catalog.=) c.conrelid AND " +
                "a.attnum OPERATOR(pg_catalog.=) c.conkey[pg_catalog.array_lower(c.conkey,1) OPERATOR(pg_catalog.+) k.position OPERATOR(pg_catalog.-) 1] " +
                "WHERE a.attnum OPERATOR(pg_catalog.>) 0 AND NOT a.attisdropped AND " + Visible),
            ["table_catalog", "table_schema", "table_name", "constraint_name", "ordinal_position"])
    ];

    static Definition ConstraintColumns(string name, string kind) => new(name, 5,
        ["Catalog", "Schema", "Table", "Constraint"], ["table_catalog", "table_schema", "table_name", "constraint_name"],
        [Text("TABLE_CATALOG", true), Text("TABLE_SCHEMA", true), Text("TABLE_NAME", true), Text("CONSTRAINT_NAME", true), Text("COLUMN_NAME", true),
            Number("ORDINAL_POSITION", true), Flag("IS_DEFERRABLE"), Flag("INITIALLY_DEFERRED")],
        Source("SELECT " + TableIdentity + "c.conname AS constraint_name, a.attname AS column_name, k.position AS ordinal_position, " +
            Yes("c.condeferrable") + " AS is_deferrable, " + Yes("c.condeferred") + " AS initially_deferred" + ConstraintFrom +
            "CROSS JOIN pg_catalog.generate_series(1,pg_catalog.array_length(c.conkey,1)) AS k(position) " +
            "JOIN pg_catalog.pg_attribute a ON a.attrelid OPERATOR(pg_catalog.=) c.conrelid AND " +
            "a.attnum OPERATOR(pg_catalog.=) c.conkey[pg_catalog.array_lower(c.conkey,1) OPERATOR(pg_catalog.+) k.position OPERATOR(pg_catalog.-) 1] " +
            "WHERE c.contype OPERATOR(pg_catalog.=) '" + kind + "' AND " + Visible),
        ["table_catalog", "table_schema", "table_name", "constraint_name", "ordinal_position"]);

    static string Source(string sql) => "(" + sql + ") AS metadata";
    static string Yes(string expression) => "CASE WHEN " + expression + " THEN 'YES' ELSE 'NO' END";
    static string Action(string expression) => Code(expression, [("a", "NO ACTION"), ("r", "RESTRICT"), ("c", "CASCADE"), ("n", "SET NULL"), ("d", "SET DEFAULT")]);
    static string Code(string expression, (string Code, string Value)[] values) => "CASE " +
        string.Join(" ", values.Select(value => "WHEN " + expression + " OPERATOR(pg_catalog.=) '" + value.Code + "' THEN '" + value.Value + "'")) + " END";
}
