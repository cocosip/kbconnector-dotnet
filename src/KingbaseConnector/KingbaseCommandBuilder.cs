using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KingbaseConnector.Protocol;
#if !NETSTANDARD2_0
using System.Diagnostics.CodeAnalysis;
#endif

namespace KingbaseConnector;

/// <summary>Generates commands for proven direct PG table projections.</summary>
public sealed class KingbaseCommandBuilder : DbCommandBuilder
{
    DataTable? _refreshSnapshot;
    bool _namesGenerated;
    bool _refreshNames;
    public KingbaseCommandBuilder() { }
    public KingbaseCommandBuilder(KingbaseDataAdapter? adapter) => DataAdapter = adapter;
    public new KingbaseDataAdapter? DataAdapter { get => (KingbaseDataAdapter?)base.DataAdapter; set => base.DataAdapter = value; }
#if !NETSTANDARD2_0
    [AllowNull]
#endif
    public override string QuotePrefix { get => "U&\""; set { if (value != "U&\"") throw new NotSupportedException("PG command generation requires Unicode-quoted identifiers."); } }
#if !NETSTANDARD2_0
    [AllowNull]
#endif
    public override string QuoteSuffix { get => "\""; set { if (value != "\"") throw new NotSupportedException("PG command generation requires Unicode-quoted identifiers."); } }
    public override string QuoteIdentifier(string unquotedIdentifier)
    {
        ValidateIdentifier(unquotedIdentifier);
        return "U&\"" + EscapeIdentifier(unquotedIdentifier) + "\"";
    }
    public override string UnquoteIdentifier(string quotedIdentifier)
    {
        ValidateIdentifier(quotedIdentifier);
        if (quotedIdentifier.StartsWith("U&\"", StringComparison.OrdinalIgnoreCase))
        {
            if (quotedIdentifier.Length < 4 || !quotedIdentifier.EndsWith("\"", StringComparison.Ordinal)) throw new ArgumentException("Invalid quoted identifier.");
            var decoded = PgUnicodeIdentifier.Decode(quotedIdentifier.Substring(3, quotedIdentifier.Length - 4));
            ValidateIdentifier(decoded);
            return decoded;
        }
        if (quotedIdentifier.Length >= 2 && quotedIdentifier[0] == '"' && quotedIdentifier[quotedIdentifier.Length - 1] == '"')
        {
            var value = quotedIdentifier.Substring(1, quotedIdentifier.Length - 2);
            for (var i = 0; i < value.Length; i++)
                if (value[i] == '"' && (++i == value.Length || value[i] != '"')) throw new ArgumentException("Invalid quoted identifier.", nameof(quotedIdentifier));
            return value.Replace("\"\"", "\"");
        }
        return quotedIdentifier;
    }
    static void ValidateIdentifier(string value) => PgUnicodeIdentifier.Validate(value);
    static string EscapeIdentifier(string value) => value.Replace("\\", "\\005c").Replace("\"", "\\0022");
    public new KingbaseCommand GetInsertCommand() => (KingbaseCommand)base.GetInsertCommand();
    public new KingbaseCommand GetInsertCommand(bool useColumnsForParameterNames) => (KingbaseCommand)base.GetInsertCommand(useColumnsForParameterNames);
    public new KingbaseCommand GetUpdateCommand() => (KingbaseCommand)base.GetUpdateCommand();
    public new KingbaseCommand GetUpdateCommand(bool useColumnsForParameterNames) => (KingbaseCommand)base.GetUpdateCommand(useColumnsForParameterNames);
    public new KingbaseCommand GetDeleteCommand() => (KingbaseCommand)base.GetDeleteCommand();
    public new KingbaseCommand GetDeleteCommand(bool useColumnsForParameterNames) => (KingbaseCommand)base.GetDeleteCommand(useColumnsForParameterNames);
    public override void RefreshSchema()
    {
        base.RefreshSchema();
        _refreshNames = _namesGenerated;
    }

    protected override string GetParameterName(int parameterOrdinal) => "p" + parameterOrdinal.ToString(CultureInfo.InvariantCulture);
    protected override string GetParameterPlaceholder(int parameterOrdinal) => "@" + GetParameterName(parameterOrdinal);
    protected override string GetParameterName(string parameterName)
    {
        _namesGenerated = true;
        using var hash = SHA256.Create();
        return "c" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(parameterName))).Replace("-", "").ToLowerInvariant();
    }
    protected override DbCommand InitializeCommand(DbCommand? command)
    {
        var result = base.InitializeCommand(command);
        ((KingbaseCommand)result).RequiresPgMetadata = true;
        // BCL regeneration can rename parameters in an order that temporarily duplicates existing names.
        result.Parameters.Clear();
        return result;
    }
    protected override void ApplyParameterInfo(DbParameter parameter, DataRow row, StatementType statementType, bool whereClause)
    {
        var target = (KingbaseParameter)parameter;
        target.ResetDbType();
        if (target.SourceColumnNullMapping) target.DbType = DbType.Int32;
        else
        {
            // Reader metadata reports pg_catalog base names bare and other catalog types qualified.
            var name = (string)row["DataTypeName"];
            target.DataTypeName = name.Contains('.') ? name : "pg_catalog." + name;
        }
    }
    protected override DataTable GetSchemaTable(DbCommand sourceCommand)
    {
        if (_refreshSnapshot is { } snapshot) return snapshot;
        var schema = base.GetSchemaTable(sourceCommand) ?? throw new InvalidOperationException("Command generation requires column metadata.");
        if (PgDirectProjection.Parse(sourceCommand.CommandText)?.Only == true)
        { schema.Dispose(); throw new NotSupportedException("Command generation for ONLY projections is not implemented."); }
        var origins = new HashSet<(uint, short)>();
        schema.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));
        foreach (DataRow row in schema.Rows)
        {
            foreach (var field in new[] { "BaseCatalogName", "BaseSchemaName", "BaseTableName", "BaseColumnName", "DataTypeName", "AllowDBNull", "IsKey", "IsUnique", "IsAutoIncrement", "IsReadOnly" })
                if (row.IsNull(field)) { schema.Dispose(); throw new InvalidOperationException("Command generation requires proven direct table column metadata."); }
            if (!origins.Add(((uint)row["BaseTableOID"], (short)row["ColumnAttributeNumber"])))
            { schema.Dispose(); throw new NotSupportedException("Command generation requires distinct base columns."); }
            row[SchemaTableColumn.IsLong] = (string)row["DataTypeName"] is "xml" or "json";
        }
        // BCL rejects literal quote characters in table names; PG Unicode escapes preserve each identifier without them.
        foreach (DataRow row in schema.Rows)
            foreach (var field in new[] { "BaseCatalogName", "BaseSchemaName", "BaseTableName", "BaseColumnName" })
                row[field] = EscapeIdentifier((string)row[field]);
        if (_refreshNames)
        {
            // BCL RefreshSchema retains its old name array; rebuild it before the outer cache installs this snapshot.
            _refreshNames = false; _refreshSnapshot = schema;
            try { base.GetInsertCommand(true); }
            finally { _refreshSnapshot = null; }
        }
        return schema;
    }
    protected override void SetRowUpdatingHandler(DbDataAdapter adapter)
    {
        if (adapter is not KingbaseDataAdapter target) throw new ArgumentException("The command builder requires a KingbaseDataAdapter.", nameof(adapter));
        if (ReferenceEquals(adapter, base.DataAdapter)) target.RowUpdating -= HandleRowUpdating;
        else target.RowUpdating += HandleRowUpdating;
    }
    void HandleRowUpdating(object? sender, KingbaseRowUpdatingEventArgs args) => RowUpdatingHandler(args);
}
