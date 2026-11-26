using System.Collections.ObjectModel;
using System.Globalization;

namespace KingbaseConnector.Protocol;

internal sealed class PgTypeDescription(uint oid, uint namespaceOid, string name, short size, char kind,
    char category, uint baseTypeOid, uint elementTypeOid, uint arrayTypeOid, int typeModifier,
    string namespaceName = "pg_catalog", string? codecName = null)
{
    internal uint Oid { get; } = oid;
    internal uint NamespaceOid { get; } = namespaceOid;
    internal string Namespace { get; } = namespaceName;
    internal string CodecName { get; } = codecName ?? name;
    internal string Name { get; } = name;
    internal short Size { get; } = size;
    internal char Kind { get; } = kind;
    internal char Category { get; } = category;
    internal uint BaseTypeOid { get; } = baseTypeOid;
    internal uint ElementTypeOid { get; } = elementTypeOid;
    internal uint ArrayTypeOid { get; } = arrayTypeOid;
    internal int TypeModifier { get; } = typeModifier;
}

internal sealed class PgTypeCatalog
{
    internal static readonly string[] Names = ["bool", "int2", "int4", "int8", "oid", "float4", "float8", "numeric", "text",
        "varchar", "bpchar", "bytea", "uuid", "date", "time", "timetz", "timestamp", "timestamptz", "interval", "json", "jsonb", "xml", "int16", "tinyint", "bit", "varbit",
        "name", "char", "regproc", "regprocedure", "regoper", "regoperator", "regclass", "regtype", "regconfig", "regdictionary", "regnamespace", "regrole", "xid", "cid"];
    static readonly string[] Required = ["bool", "int2", "int4", "int8", "text", "bytea"];
    static readonly string Sql = "SELECT t.oid, t.typnamespace, n.nspname, t.typname, t.typlen, t.typtype, " +
        "t.typcategory, t.typisdefined, t.typbasetype, t.typelem, t.typarray, t.typtypmod " +
        "FROM sys_catalog.sys_type AS t JOIN sys_catalog.sys_namespace AS n ON n.oid OPERATOR(pg_catalog.=) t.typnamespace " +
        "WHERE n.nspname OPERATOR(pg_catalog.=) 'pg_catalog'::pg_catalog.name AND t.typisdefined AND (" +
        string.Join(" OR ", Names.Select(name => "pg_catalog.nameeq(t.typname,'" + name + "'::pg_catalog.name)")) + ")";

    PgTypeCatalog(Dictionary<uint, PgTypeDescription> byOid, Dictionary<string, PgTypeDescription> byName)
    {
        ByOid = new ReadOnlyDictionary<uint, PgTypeDescription>(byOid);
        ByName = new ReadOnlyDictionary<string, PgTypeDescription>(byName);
    }

    internal IReadOnlyDictionary<uint, PgTypeDescription> ByOid { get; }
    internal IReadOnlyDictionary<string, PgTypeDescription> ByName { get; }

    // The initialization owner holds this session exclusively, as for identity probing.
    internal static async Task<PgTypeCatalog> LoadAsync(PgQuerySession session, PgIdentityResult identity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        if (identity.Version.Major != 9 || identity.EffectiveMode == KingbaseCompatibilityMode.Auto ||
            !Enum.IsDefined(typeof(KingbaseCompatibilityMode), identity.EffectiveMode))
            throw new ArgumentException("PG catalog loading requires a resolved Kingbase V9 identity.", nameof(identity));
        if (session.TransactionStatus != PgTransactionStatus.Idle)
            throw new InvalidOperationException("PG catalog initialization requires an idle session.");
        try
        {
            CheckMode(session, identity);
            var results = await session.ExecuteAsync(Sql, cancellationToken).ConfigureAwait(false);
            if (session.TransactionStatus != PgTransactionStatus.Idle || results.Count != 1 ||
                results[0].Columns.Count != 12 || results[0].Rows.Count > Names.Length)
                throw InvalidCatalog();
            CheckMode(session, identity);
            var byOid = new Dictionary<uint, PgTypeDescription>();
            var byName = new Dictionary<string, PgTypeDescription>(StringComparer.Ordinal);
            uint? namespaceOid = null;
            var seenOids = new HashSet<uint>(); var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in results[0].Rows)
            {
                if (row.Count != 12 || !Names.Contains(row[3], StringComparer.Ordinal))
                    throw InvalidCatalog();
                var type = ReadType(row, "pg_catalog");
                if (namespaceOid.HasValue && namespaceOid.Value != type.NamespaceOid) throw InvalidCatalog();
                namespaceOid = type.NamespaceOid;
                var name = type.Namespace + "." + type.Name;
                if (!seenOids.Add(type.Oid) || !seenNames.Add(name)) throw InvalidCatalog();
                if (type.Name is "int16" or "tinyint")
                {
                    if (type.Kind != 'b' || type.Category != 'N' || type.BaseTypeOid != 0 || type.ElementTypeOid != 0 ||
                        (type.Name == "int16" ? type.Size != 16 : type.Size != 1 || identity.EffectiveMode != KingbaseCompatibilityMode.Oracle)) continue;
                    if (type.Name == "tinyint") type = ReadType(row, "pg_catalog", "sbyte");
                }
                if (type.Name is "bit" or "varbit" && (type.Kind != 'b' || type.Category != 'V' || type.Size != -1 || type.BaseTypeOid != 0 || type.ElementTypeOid != 0)) continue;
                byOid.Add(type.Oid, type);
                byName.Add(name, type);
            }
            foreach (var name in Required)
                if (!byName.TryGetValue("pg_catalog." + name, out var type) || type.Kind != 'b' || type.BaseTypeOid != 0)
                    throw InvalidCatalog();
            if (identity.EffectiveMode != KingbaseCompatibilityMode.PostgreSql)
                await PgNativeTypeCatalog.AddAsync(session, identity, byOid, byName, seenOids, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new PgTypeCatalog(byOid, byName);
        }
        catch
        {
            try { session.Dispose(); } catch { }
            throw;
        }
    }

    internal static void CheckMode(PgQuerySession session, PgIdentityResult identity)
    {
        if (session.Parameters.TryGetValue("database_mode", out var mode) && PgIdentityProbe.ParseMode(mode) != identity.EffectiveMode)
            throw new NotSupportedException("The reported database mode changed during PG catalog initialization.");
    }

    internal static PgTypeDescription ReadType(IReadOnlyList<string?> row, string namespaceName, string? codecName = null)
    {
        if (row.Count < 12 || row.Take(12).Any(value => value is null) || row[2] != namespaceName ||
            !(row[7]!.Equals("t", StringComparison.OrdinalIgnoreCase) || row[7]!.Equals("true", StringComparison.OrdinalIgnoreCase))) throw InvalidCatalog();
        var size = ParseInt(row[4]!);
        if (size < -2 || size == 0 || size > short.MaxValue) throw InvalidCatalog();
        return new PgTypeDescription(ParseOid(row[0]!, false), ParseOid(row[1]!, false), row[3]!, (short)size,
            ParseChar(row[5]!), ParseChar(row[6]!), ParseOid(row[8]!, true), ParseOid(row[9]!, true), ParseOid(row[10]!, true),
            ParseInt(row[11]!), namespaceName, codecName);
    }

    static uint ParseOid(string value, bool allowZero)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || (!allowZero && result == 0))
            throw InvalidCatalog();
        return result;
    }
    static int ParseInt(string value)
    {
        if (value.Length == 0 || value[0] == '+' ||
            !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result))
            throw InvalidCatalog();
        return result;
    }
    static char ParseChar(string value)
    {
        if (value.Length != 1 || value[0] < '!' || value[0] > '~') throw InvalidCatalog();
        return value[0];
    }
    internal static IOException InvalidCatalog() => new("Invalid or inadequate PG built-in type catalog.");
}
