using static KingbaseConnector.KingbaseObjectSchema;

namespace KingbaseConnector;

internal static class KingbaseDirectorySchema
{
    internal static readonly Definition[] All =
    [
        new("Databases", 1, ["Database"], ["database_name"],
            [Text("DATABASE_NAME", true), Text("OWNER", true), Text("ENCODING", true), Text("COLLATION", true), Text("CTYPE", true),
                Flag("IS_TEMPLATE"), Flag("ALLOWS_CONNECTIONS"), Number("CONNECTION_LIMIT", true), Flag("HAS_CONNECT_PRIVILEGE")],
            "(SELECT d.datname AS database_name, pg_catalog.pg_get_userbyid(d.datdba) AS owner, " +
                "pg_catalog.pg_encoding_to_char(d.encoding) AS encoding, d.datcollate AS collation, d.datctype AS ctype, " +
                Yes("d.datistemplate") + " AS is_template, " + Yes("d.datallowconn") + " AS allows_connections, " +
                "d.datconnlimit AS connection_limit, " + Yes("pg_catalog.has_database_privilege(d.oid,'CONNECT')") +
                " AS has_connect_privilege FROM pg_catalog.pg_database d) AS directory_metadata"),
        new("Users", 1, ["User"], ["user_name"],
            [Text("USER_NAME", true), Flag("IS_SUPERUSER"), Flag("INHERITS_PRIVILEGES"), Flag("CAN_CREATE_ROLE"),
                Flag("CAN_CREATE_DATABASE"), Flag("CAN_REPLICATE"), Flag("BYPASSES_ROW_SECURITY"), Number("CONNECTION_LIMIT", true)],
            "(SELECT r.rolname AS user_name, " + Yes("r.rolsuper") + " AS is_superuser, " + Yes("r.rolinherit") + " AS inherits_privileges, " +
                Yes("r.rolcreaterole") + " AS can_create_role, " + Yes("r.rolcreatedb") + " AS can_create_database, " +
                Yes("r.rolreplication") + " AS can_replicate, " + Yes("r.rolbypassrls") + " AS bypasses_row_security, " +
                "r.rolconnlimit AS connection_limit FROM pg_catalog.pg_roles r WHERE r.rolcanlogin) AS directory_metadata")
    ];

    static string Yes(string expression) => "CASE WHEN " + expression + " THEN 'YES' ELSE 'NO' END";
}
