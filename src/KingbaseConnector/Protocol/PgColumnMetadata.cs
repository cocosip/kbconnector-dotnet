using System.Globalization;

namespace KingbaseConnector.Protocol;

internal sealed class PgColumnMetadata(string? catalog = null, string? schema = null, string? table = null, string? column = null,
    bool? allowNull = null, bool? key = null, bool? unique = null, bool? autoIncrement = null, bool? identity = null,
    bool? readOnly = null, bool? expression = null, string? version = null)
{
    internal string? Catalog { get; } = catalog;
    internal string? Schema { get; } = schema;
    internal string? Table { get; } = table;
    internal string? Column { get; } = column;
    internal bool? AllowNull { get; } = allowNull;
    internal bool? Key { get; } = key;
    internal bool? Unique { get; } = unique;
    internal bool? AutoIncrement { get; } = autoIncrement;
    internal bool? Identity { get; } = identity;
    internal bool? ReadOnly { get; } = readOnly;
    internal bool? Expression { get; } = expression;
    internal string? Version { get; } = version;
    internal static readonly string[] Fields = ["table_oid", "attribute_number", "type_oid", "type_modifier", "catalog_name", "schema_name", "table_name", "column_name", "not_null", "identity_kind", "generated_kind", "relation_kind", "has_subclasses", "auto_increment", "key_columns", "single_unique", "attribute_version", "table_version", "isolation_level"];
    const string KeyPosition = "i.indkey[pg_catalog.array_lower(i.indkey,1) OPERATOR(pg_catalog.+) g.position OPERATOR(pg_catalog.-) 1]";
    const string ValidIndex = "i.indisunique AND i.indisvalid AND i.indisready AND i.indislive AND i.indimmediate AND i.indpred IS NULL AND i.indexprs IS NULL AND i.indnkeyatts OPERATOR(pg_catalog.=) (SELECT pg_catalog.count(DISTINCT " + KeyPosition + ") FROM pg_catalog.generate_series(1,i.indnkeyatts::pg_catalog.int4) g(position)) AND NOT EXISTS (SELECT 1 FROM pg_catalog.generate_series(1,i.indnkeyatts::pg_catalog.int4) g(position) JOIN pg_catalog.pg_attribute ca ON ca.attrelid OPERATOR(pg_catalog.=) c.oid AND ca.attnum OPERATOR(pg_catalog.=) " + KeyPosition +
        " LEFT JOIN pg_catalog.pg_opclass oc ON oc.oid OPERATOR(pg_catalog.=) i.indclass[pg_catalog.array_lower(i.indclass,1) OPERATOR(pg_catalog.+) g.position OPERATOR(pg_catalog.-) 1] LEFT JOIN pg_catalog.pg_am am ON am.oid OPERATOR(pg_catalog.=) oc.opcmethod WHERE ca.attcollation OPERATOR(pg_catalog.<>) i.indcollation[pg_catalog.array_lower(i.indcollation,1) OPERATOR(pg_catalog.+) g.position OPERATOR(pg_catalog.-) 1] OR oc.oid IS NULL OR NOT oc.opcdefault OR oc.opcnamespace OPERATOR(pg_catalog.<>) 'pg_catalog'::pg_catalog.regnamespace OR am.amname OPERATOR(pg_catalog.<>) 'btree')";
    const string NonNullKeys = "NOT EXISTS (SELECT 1 FROM pg_catalog.generate_series(1,i.indnkeyatts::pg_catalog.int4) g(position) LEFT JOIN pg_catalog.pg_attribute ka ON ka.attrelid OPERATOR(pg_catalog.=) c.oid AND ka.attnum OPERATOR(pg_catalog.=) " + KeyPosition + " WHERE ka.attnum IS NULL OR NOT ka.attnotnull OR ka.attisdropped)";
    static readonly string Sql = """
        WITH requested AS (SELECT * FROM pg_catalog.json_to_recordset($1::pg_catalog.json) AS q(table_oid pg_catalog.oid, attribute_number pg_catalog.int2))
        SELECT c.oid::pg_catalog.text AS table_oid, a.attnum::pg_catalog.text AS attribute_number,
        a.atttypid::pg_catalog.text AS type_oid, a.atttypmod::pg_catalog.text AS type_modifier,
        pg_catalog.current_database()::pg_catalog.text AS catalog_name, n.nspname::pg_catalog.text AS schema_name,
        c.relname::pg_catalog.text AS table_name, a.attname::pg_catalog.text AS column_name,
        a.attnotnull::pg_catalog.text AS not_null, a.attidentity::pg_catalog.text AS identity_kind,
        a.attgenerated::pg_catalog.text AS generated_kind, c.relkind::pg_catalog.text AS relation_kind,
        c.relhassubclass::pg_catalog.text AS has_subclasses,
        (CASE WHEN a.attidentity OPERATOR(pg_catalog.<>) '' THEN true
          WHEN automatic.canonical IS TRUE THEN true WHEN automatic.canonical IS FALSE THEN NULL ELSE false END)::pg_catalog.text AS auto_increment,
        keys.key_columns::pg_catalog.text AS key_columns,
        """ + "(EXISTS (SELECT 1 FROM pg_catalog.pg_index i WHERE i.indrelid OPERATOR(pg_catalog.=) c.oid AND " + ValidIndex + " AND " + NonNullKeys +
        " AND i.indnkeyatts OPERATOR(pg_catalog.=) 1 AND i.indkey[pg_catalog.array_lower(i.indkey,1)] OPERATOR(pg_catalog.=) a.attnum))::pg_catalog.text AS single_unique, " +
        "pg_catalog.concat_ws(':',a.xmin::pg_catalog.text,a.cmin::pg_catalog.text,def.oid::pg_catalog.text,def.xmin::pg_catalog.text,def.cmin::pg_catalog.text,automatic.versions) AS attribute_version, " +
        "CASE WHEN pg_catalog.row_number() OVER(PARTITION BY c.oid ORDER BY a.attnum) OPERATOR(pg_catalog.=) 1 THEN " +
        "pg_catalog.concat_ws(':',c.xmin::pg_catalog.text,c.cmin::pg_catalog.text,n.xmin::pg_catalog.text,n.cmin::pg_catalog.text," +
        "(SELECT pg_catalog.string_agg(pg_catalog.concat_ws('/',ix.indexrelid::pg_catalog.text,ix.xmin::pg_catalog.text,ix.cmin::pg_catalog.text),',' ORDER BY ix.indexrelid) " +
        "FROM pg_catalog.pg_index ix WHERE ix.indrelid OPERATOR(pg_catalog.=) c.oid)) END AS table_version, " +
        "pg_catalog.current_setting('transaction_isolation')::pg_catalog.text AS isolation_level " +
        "FROM requested q JOIN pg_catalog.pg_class c ON c.oid OPERATOR(pg_catalog.=) q.table_oid " +
        "JOIN pg_catalog.pg_namespace n ON n.oid OPERATOR(pg_catalog.=) c.relnamespace " +
        "JOIN pg_catalog.pg_attribute a ON a.attrelid OPERATOR(pg_catalog.=) c.oid AND a.attnum OPERATOR(pg_catalog.=) q.attribute_number " +
        "LEFT JOIN pg_catalog.pg_attrdef def ON def.adrelid OPERATOR(pg_catalog.=) c.oid AND def.adnum OPERATOR(pg_catalog.=) a.attnum " +
        """
        LEFT JOIN LATERAL (SELECT pg_catalog.bool_or((pg_catalog.pg_get_expr(ad.adbin,ad.adrelid) OPERATOR(pg_catalog.=) ANY
          (ARRAY[pg_catalog.format('nextval(%L::regclass)',seq.oid::pg_catalog.regclass::pg_catalog.text),
           pg_catalog.format('pg_catalog.nextval(%L::regclass)',seq.oid::pg_catalog.regclass::pg_catalog.text),
           pg_catalog.format('nextval(%L::pg_catalog.regclass)',seq.oid::pg_catalog.regclass::pg_catalog.text),
           pg_catalog.format('pg_catalog.nextval(%L::pg_catalog.regclass)',seq.oid::pg_catalog.regclass::pg_catalog.text)]))
          AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend fn WHERE fn.classid OPERATOR(pg_catalog.=) 'pg_catalog.pg_attrdef'::pg_catalog.regclass
            AND fn.objid OPERATOR(pg_catalog.=) ad.oid AND fn.refclassid OPERATOR(pg_catalog.=) 'pg_catalog.pg_proc'::pg_catalog.regclass
            AND fn.refobjid OPERATOR(pg_catalog.<>) 'pg_catalog.nextval(pg_catalog.regclass)'::pg_catalog.regprocedure)) AS canonical,
          pg_catalog.string_agg(pg_catalog.concat_ws('/',own.objid::pg_catalog.text,own.xmin::pg_catalog.text,own.cmin::pg_catalog.text),',' ORDER BY own.objid) AS versions
          FROM pg_catalog.pg_depend own JOIN pg_catalog.pg_class seq ON seq.oid OPERATOR(pg_catalog.=) own.objid
          JOIN pg_catalog.pg_attrdef ad ON ad.adrelid OPERATOR(pg_catalog.=) c.oid AND ad.adnum OPERATOR(pg_catalog.=) a.attnum
          JOIN pg_catalog.pg_depend uses ON uses.classid OPERATOR(pg_catalog.=) 'pg_catalog.pg_attrdef'::pg_catalog.regclass
            AND uses.objid OPERATOR(pg_catalog.=) ad.oid AND uses.refclassid OPERATOR(pg_catalog.=) 'pg_catalog.pg_class'::pg_catalog.regclass
            AND uses.refobjid OPERATOR(pg_catalog.=) seq.oid
          WHERE own.classid OPERATOR(pg_catalog.=) 'pg_catalog.pg_class'::pg_catalog.regclass
            AND own.refclassid OPERATOR(pg_catalog.=) 'pg_catalog.pg_class'::pg_catalog.regclass
            AND own.refobjid OPERATOR(pg_catalog.=) c.oid AND own.refobjsubid OPERATOR(pg_catalog.=) a.attnum
            AND (own.deptype OPERATOR(pg_catalog.=) 'a' OR own.deptype OPERATOR(pg_catalog.=) 'i') AND seq.relkind OPERATOR(pg_catalog.=) 'S') automatic ON true
        """ +
        " LEFT JOIN LATERAL (SELECT (SELECT pg_catalog.string_agg((" + KeyPosition + ")::pg_catalog.text, ',' ORDER BY g.position) " +
        "FROM pg_catalog.generate_series(1,i.indnkeyatts::pg_catalog.int4) g(position)) AS key_columns FROM pg_catalog.pg_index i " +
        "WHERE i.indrelid OPERATOR(pg_catalog.=) c.oid AND " + ValidIndex + " AND " + NonNullKeys +
        " AND NOT EXISTS (SELECT 1 FROM pg_catalog.generate_series(1,i.indnkeyatts::pg_catalog.int4) g(position) WHERE NOT EXISTS " +
        "(SELECT 1 FROM requested p WHERE p.table_oid OPERATOR(pg_catalog.=) c.oid AND p.attribute_number OPERATOR(pg_catalog.=) " + KeyPosition + ")) " +
        "ORDER BY i.indisprimary DESC,i.indnkeyatts,i.indexrelid LIMIT 1) keys ON true " +
        "WHERE a.attnum OPERATOR(pg_catalog.>) 0 AND NOT a.attisdropped AND NOT pg_catalog.pg_is_other_temp_schema(c.relnamespace) " +
        "AND (pg_catalog.pg_has_role(c.relowner,'USAGE') OR pg_catalog.has_column_privilege(c.oid,a.attnum,'SELECT')) ORDER BY c.oid,a.attnum";

    sealed class Fact(IReadOnlyList<string?> row, short[] keys)
    {
        internal IReadOnlyList<string?> Row { get; } = row;
        internal short[] Keys { get; } = keys;
    }
    internal static async Task<IReadOnlyList<PgTextResult>> EnrichAsync(PgPhysicalSession session, IReadOnlyList<PgTextResult> results,
        string sql, CancellationToken token)
    {
        var shape = PgDirectProjection.Parse(sql);
        var output = new List<PgTextResult>();
        foreach (var result in results)
        {
            var origins = result.Columns.Where(c => c.TableOid != 0 && c.AttributeNumber > 0)
                .GroupBy(c => (c.TableOid, c.AttributeNumber)).ToDictionary(g => g.Key, g => g.First());
            var facts = new Dictionary<(uint, short), Fact>();
            var versions = new Dictionary<uint, string>();
            if (origins.Count != 0)
            {
                var json = "[" + string.Join(",", origins.Keys.Select(k => "{\"table_oid\":" + k.TableOid.ToString(CultureInfo.InvariantCulture) +
                    ",\"attribute_number\":" + k.AttributeNumber.ToString(CultureInfo.InvariantCulture) + "}")) + "]";
                var textOid = session.Catalog.ByName["pg_catalog.text"].Oid;
                var response = await session.Queries.ExecuteExtendedAsync(Sql, session.Catalog, [new PgQueryParameter(textOid, json)], token).ConfigureAwait(false);
                try
                {
                    if (response.Count != 1 || response[0].Columns.Count != Fields.Length || response[0].Rows.Count > origins.Count ||
                        response[0].CommandTag != "SELECT " + response[0].Rows.Count.ToString(CultureInfo.InvariantCulture)) throw Invalid();
                    for (var i = 0; i < Fields.Length; i++)
                        if (response[0].Columns[i].Name != Fields[i] || response[0].Columns[i].TypeOid != textOid || response[0].Columns[i].TypeSize != -1 || response[0].Columns[i].TypeModifier != -1) throw Invalid();
                    var seen = new HashSet<(uint, short)>();
                    foreach (var row in response[0].Rows)
                    {
                        if (row.Count != Fields.Length || row.Where((v, i) => i is not (13 or 14 or 17) && v is null).Any()) throw Invalid();
                        var oid = UInt(row[0]!); var attribute = Attribute(row[1]!); var key = (oid, attribute);
                        if (!origins.TryGetValue(key, out var origin) || !seen.Add(key)) throw Invalid();
                        var typeOid = UInt(row[2]!); var modifier = Int(row[3]!);
                        for (var i = 4; i <= 7; i++) if (row[i]!.Length == 0 || row[i]!.IndexOf('\0') >= 0) throw Invalid();
                        _ = Bool(row[8]!); _ = Bool(row[12]!); if (row[13] is not null) _ = Bool(row[13]!); _ = Bool(row[15]!);
                        if (row[9] is not ("" or "a" or "d") || row[10] is not ("" or "s" or "v") || row[11]!.Length != 1) throw Invalid();
                        var keys = row[14] is null ? Array.Empty<short>() : row[14]!.Split(',').Select(Attribute).ToArray();
                        if (keys.Length > 1024 || keys.Distinct().Count() != keys.Length || keys.Any(a => !origins.ContainsKey((oid, a)))) throw Invalid();
                        if ((!Bool(row[8]!) && (keys.Contains(attribute) || Bool(row[15]!))) || (row[9] != "" && row[13] != "true") || (row[9] != "" && row[10] != "")) throw Invalid();
                        if ((keys.Length == 1 && keys[0] == attribute && !Bool(row[15]!)) || (Bool(row[15]!) && keys.Length == 0)) throw Invalid();
                        if (row[16]!.Length == 0 || row[18] is not ("read committed" or "read uncommitted" or "repeatable read" or "serializable")) throw Invalid();
                        if (row[17] is { } tableVersion)
                        { if (tableVersion.Length == 0 || versions.ContainsKey(oid)) throw Invalid(); versions.Add(oid, tableVersion); }
                        if (origin.TypeOid == typeOid && origin.TypeModifier == modifier) facts.Add(key, new Fact(row, keys));
                    }
                    foreach (var group in facts.GroupBy(pair => pair.Key.Item1))
                    {
                        var first = group.First().Value;
                        if (!versions.ContainsKey(group.Key) || group.Any(pair => !pair.Value.Keys.SequenceEqual(first.Keys) || new[] { 4, 5, 6, 11, 12, 18 }.Any(i => pair.Value.Row[i] != first.Row[i]))) throw Invalid();
                    }
                }
                catch { session.Dispose(); throw; }
            }
            var direct = shape is not null && origins.Count != 0 && facts.Count == origins.Count && result.Columns.All(c => c.TableOid != 0 && c.AttributeNumber > 0) && origins.Keys.Select(k => k.TableOid).Distinct().Count() == 1;
            var safe = false;
            if (direct)
            {
                var row = facts.Values.First().Row;
                safe = shape!.Matches(row[4]!, row[5]!, row[6]!, facts.Values.Select(f => f.Row[7]!), result.Columns.Select(c => c.Name)) &&
                    row[11] is "r" or "p" && (!Bool(row[12]!) || shape.Only || row[11] == "p");
            }
            var metadata = new List<PgColumnMetadata?>();
            foreach (var column in result.Columns)
            {
                if (column.TableOid == 0 || column.AttributeNumber <= 0) { metadata.Add(new PgColumnMetadata()); continue; }
                if (!facts.TryGetValue((column.TableOid, column.AttributeNumber), out var fact)) { metadata.Add(null); continue; }
                var row = fact.Row;
                // Snapshot-isolated catalog reads can lag internal relation lookup; do not publish stale identities or guarantees.
                if (row[18] is "repeatable read" or "serializable") { metadata.Add(null); continue; }
                metadata.Add(new PgColumnMetadata(row[4], row[5], row[6], row[7],
                    allowNull: safe ? !Bool(row[8]!) : null, key: safe ? fact.Keys.Contains(column.AttributeNumber) : null,
                    unique: safe ? Bool(row[15]!) : null, autoIncrement: safe && row[13] is not null ? Bool(row[13]!) : null,
                    identity: safe ? row[9] != "" : null, readOnly: safe ? row[10] != "" || row[9] == "a" : null, expression: false, version: row[16]));
            }
            output.Add(new PgTextResult(result.Columns, result.Rows, result.CommandTag, result.DateStyle, result.IntervalStyle, metadata.AsReadOnly(), versions));
        }
        return output.AsReadOnly();
    }
    static uint UInt(string value) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed != 0 ? parsed : throw Invalid();
    static int Int(string value) => value.Length != 0 && value[0] != '+' && int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw Invalid();
    static short Attribute(string value) => short.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : throw Invalid();
    static bool Bool(string value) => value switch { "true" => true, "false" => false, _ => throw Invalid() };
    static IOException Invalid() => new("Invalid PG column metadata response.");

    internal static void VerifyStable(IReadOnlyList<PgTextResult> before, IReadOnlyList<PgTextResult> after)
    {
        if (before.Count != after.Count) throw Changed();
        for (var i = 0; i < before.Count; i++)
        {
            var first = before[i]; var last = after[i];
            if (first.Columns.Count != last.Columns.Count || first.MetadataVersions.Count != last.MetadataVersions.Count ||
                first.MetadataVersions.Any(pair => !last.MetadataVersions.TryGetValue(pair.Key, out var version) || version != pair.Value)) throw Changed();
            for (var j = 0; j < first.Columns.Count; j++)
            {
                var a = first.Columns[j]; var b = last.Columns[j];
                if (a.Name != b.Name || a.TableOid != b.TableOid || a.AttributeNumber != b.AttributeNumber || a.TypeOid != b.TypeOid || a.TypeSize != b.TypeSize || a.TypeModifier != b.TypeModifier) throw Changed();
                var x = first.Metadata?[j]; var y = last.Metadata?[j];
                if ((x is null) != (y is null)) throw Changed();
                if (x is not null && y is not null && (x.Catalog != y.Catalog || x.Schema != y.Schema || x.Table != y.Table || x.Column != y.Column ||
                    x.AllowNull != y.AllowNull || x.Key != y.Key || x.Unique != y.Unique || x.AutoIncrement != y.AutoIncrement || x.Identity != y.Identity ||
                    x.ReadOnly != y.ReadOnly || x.Expression != y.Expression || x.Version != y.Version)) throw Changed();
            }
        }
    }
    static InvalidOperationException Changed() => new("PG schema changed while obtaining column metadata; the original command is not retried.");

    internal static long RetainedBytes(PgTextResult result)
    {
        var bytes = result.MetadataVersions.Values.Sum(v => (long)PgBinary.Utf8.GetByteCount(v));
        if (result.Metadata is not null)
            foreach (var item in result.Metadata)
                if (item is not null)
                    foreach (var value in new[] { item.Catalog, item.Schema, item.Table, item.Column, item.Version })
                        if (value is not null) bytes += PgBinary.Utf8.GetByteCount(value);
        return bytes;
    }
}
