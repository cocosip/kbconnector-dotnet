using static KingbaseConnector.KingbaseObjectSchema;

namespace KingbaseConnector;

internal static class KingbaseGenerationSchema
{
    internal static readonly Definition Sequences = new("Sequences", 3,
        ["Catalog", "Schema", "Sequence"], ["sequence_catalog", "sequence_schema", "sequence_name"],
        [Text("SEQUENCE_CATALOG", true), Text("SEQUENCE_SCHEMA", true), Text("SEQUENCE_NAME", true), Text("DATA_TYPE", true),
            Number("NUMERIC_PRECISION", true), Number("NUMERIC_PRECISION_RADIX", true), Number("NUMERIC_SCALE", true),
            LongNumber("START_VALUE", true), LongNumber("MINIMUM_VALUE", true), LongNumber("MAXIMUM_VALUE", true), LongNumber("INCREMENT", true),
            Flag("CYCLE_OPTION"), LongNumber("CACHE_SIZE", true)],
        "(SELECT pg_catalog.current_database() AS sequence_catalog, n.nspname AS sequence_schema, r.relname AS sequence_name, " +
        "pg_catalog.format_type(s.seqtypid,NULL) AS data_type, " +
        "t.typlen::pg_catalog.int4 OPERATOR(pg_catalog.*) 8 AS numeric_precision, 2 AS numeric_precision_radix, 0 AS numeric_scale, " +
        "s.seqstart AS start_value, s.seqmin AS minimum_value, s.seqmax AS maximum_value, s.seqincrement AS increment, " +
        "CASE WHEN s.seqcycle THEN 'YES' ELSE 'NO' END AS cycle_option, s.seqcache AS cache_size " +
        "FROM pg_catalog.pg_sequence s JOIN pg_catalog.pg_class r ON r.oid OPERATOR(pg_catalog.=) s.seqrelid " +
        "JOIN pg_catalog.pg_namespace n ON n.oid OPERATOR(pg_catalog.=) r.relnamespace " +
        "JOIN pg_catalog.pg_type t ON t.oid OPERATOR(pg_catalog.=) s.seqtypid " +
        "WHERE r.relkind OPERATOR(pg_catalog.=) 'S' AND NOT pg_catalog.pg_is_other_temp_schema(r.relnamespace) AND " +
        "(pg_catalog.pg_has_role(r.relowner, 'USAGE') OR pg_catalog.has_sequence_privilege(r.oid, 'SELECT, UPDATE, USAGE'))) AS metadata");
}
