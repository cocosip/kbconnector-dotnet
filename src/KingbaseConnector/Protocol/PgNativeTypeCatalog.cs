namespace KingbaseConnector.Protocol;

internal static class PgNativeTypeCatalog
{
    static readonly string[] Names = ["varcharbyte", "bpcharbyte", "nchar", "nvarchar", "bit", "tinyint", "uint4", "uint8"];
    static readonly string Sql = "SELECT t.oid,t.typnamespace,n.nspname,t.typname,t.typlen,t.typtype,t.typcategory,t.typisdefined,t.typbasetype,t.typelem,t.typarray,t.typtypmod," +
        "ni.nspname::pg_catalog.text OPERATOR(pg_catalog.||) '.'::pg_catalog.text OPERATOR(pg_catalog.||) pi.proname::pg_catalog.text," +
        "no.nspname::pg_catalog.text OPERATOR(pg_catalog.||) '.'::pg_catalog.text OPERATOR(pg_catalog.||) po.proname::pg_catalog.text," +
        "(li.lanname OPERATOR(pg_catalog.=) 'internal'::pg_catalog.name AND lo.lanname OPERATOR(pg_catalog.=) 'internal'::pg_catalog.name),(rt.rolsuper AND rn.rolsuper) " +
        "FROM sys_catalog.sys_type t JOIN sys_catalog.sys_namespace n ON n.oid OPERATOR(pg_catalog.=) t.typnamespace " +
        "JOIN sys_catalog.sys_proc pi ON pi.oid OPERATOR(pg_catalog.=) t.typinput JOIN sys_catalog.sys_namespace ni ON ni.oid OPERATOR(pg_catalog.=) pi.pronamespace " +
        "JOIN sys_catalog.sys_proc po ON po.oid OPERATOR(pg_catalog.=) t.typoutput JOIN sys_catalog.sys_namespace no ON no.oid OPERATOR(pg_catalog.=) po.pronamespace " +
        "JOIN sys_catalog.sys_language li ON li.oid OPERATOR(pg_catalog.=) pi.prolang JOIN sys_catalog.sys_language lo ON lo.oid OPERATOR(pg_catalog.=) po.prolang " +
        "JOIN pg_catalog.pg_roles rt ON rt.oid OPERATOR(pg_catalog.=) t.typowner JOIN pg_catalog.pg_roles rn ON rn.oid OPERATOR(pg_catalog.=) n.nspowner " +
        "WHERE n.nspname OPERATOR(pg_catalog.=) 'sys'::pg_catalog.name AND t.typisdefined AND (" +
        string.Join(" OR ", Names.Select(name => "pg_catalog.nameeq(t.typname,'" + name + "'::pg_catalog.name)")) + ")";

    internal static async Task AddAsync(PgQuerySession session, PgIdentityResult identity,
        Dictionary<uint, PgTypeDescription> byOid, Dictionary<string, PgTypeDescription> byName, HashSet<uint> seenOids, CancellationToken token)
    {
        PgTypeCatalog.CheckMode(session, identity);
        IReadOnlyList<PgTextResult> results;
        try { results = await session.ExecuteAsync(Sql, token).ConfigureAwait(false); }
        catch (PgQueryException error) when (!session.IsDisposed && session.TransactionStatus == PgTransactionStatus.Idle &&
            ReferenceEquals(session.CommandRejection, error.ServerError) &&
            error.ServerError.SqlState is "42501" or "42P01" or "42703" or "42883" or "0A000")
        {
            token.ThrowIfCancellationRequested(); PgTypeCatalog.CheckMode(session, identity); return;
        }
        if (session.TransactionStatus != PgTransactionStatus.Idle || results.Count != 1 || results[0].Columns.Count != 16 || results[0].Rows.Count > Names.Length ||
            results[0].CommandTag != "SELECT " + results[0].Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw PgTypeCatalog.InvalidCatalog();
        PgTypeCatalog.CheckMode(session, identity);
        uint? namespaceOid = null;
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in results[0].Rows)
        {
            if (row.Count != 16 || row.Any(value => value is null) || !Names.Contains(row[3], StringComparer.Ordinal)) throw PgTypeCatalog.InvalidCatalog();
            var type = PgTypeCatalog.ReadType(row, "sys");
            if (type.NamespaceOid == byName["pg_catalog.text"].NamespaceOid || (namespaceOid.HasValue && namespaceOid.Value != type.NamespaceOid) ||
                byOid.ContainsKey(type.Oid) || !seenOids.Add(type.Oid) || !seenNames.Add(type.Name)) throw PgTypeCatalog.InvalidCatalog();
            namespaceOid = type.NamespaceOid;
            var internalIo = Flag(row[14]!); var trustedOwner = Flag(row[15]!);
            var spec = Specification(identity.EffectiveMode, type.Name);
            if (spec is not { } supported || !internalIo || !trustedOwner || type.Kind != 'b' || type.BaseTypeOid != 0 || type.ElementTypeOid != 0 ||
                type.Size != supported.Size || type.Category != supported.Category || row[12] != supported.Input || row[13] != supported.Output) continue;
            type = PgTypeCatalog.ReadType(row, "sys", supported.Codec);
            byOid.Add(type.Oid, type); byName.Add("sys." + type.Name, type);
        }
    }

    static (string Codec, short Size, char Category, string Input, string Output)? Specification(KingbaseCompatibilityMode mode, string name) => name switch
    {
        "varcharbyte" => ("varchar", -1, 'S', "sys.varcharbytein", "sys.varcharbyteout"),
        "bpcharbyte" => ("bpchar", -1, 'S', "sys.bpcharbytein", "sys.bpcharbyteout"),
        "nchar" when mode == KingbaseCompatibilityMode.SqlServer => ("bpchar", -1, 'S', "sys.nchar_in", "sys.nchar_out"),
        "nvarchar" when mode == KingbaseCompatibilityMode.SqlServer => ("varchar", -1, 'S', "sys.nvarchar_in", "sys.nvarchar_out"),
        "bit" when mode == KingbaseCompatibilityMode.SqlServer => ("sqlserver_bit", 1, 'B', "sys.sqlserver_bit_in", "sys.sqlserver_bit_out"),
        "tinyint" when mode == KingbaseCompatibilityMode.SqlServer => ("byte", 1, 'N', "sys.sqlserver_tinyint_in", "sys.sqlserver_tinyint_out"),
        "tinyint" when mode == KingbaseCompatibilityMode.MySql => ("sbyte", 1, 'N', "pg_catalog.tinyintin", "pg_catalog.tinyintout"),
        "uint4" when mode == KingbaseCompatibilityMode.MySql => ("uint4", 4, 'N', "pg_catalog.uint4in", "pg_catalog.uint4out"),
        "uint8" when mode == KingbaseCompatibilityMode.MySql => ("uint8", 8, 'N', "pg_catalog.uint8in", "pg_catalog.uint8out"),
        _ => null
    };
    static bool Flag(string value) => value.ToLowerInvariant() switch
    { "t" or "true" => true, "f" or "false" => false, _ => throw PgTypeCatalog.InvalidCatalog() };
}
