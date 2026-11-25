using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class RoutineSchemaLiveTests
{
    const string Overload = "kb_Routine_%'汉";
    const string Argument = "Arg_%'汉";
    const string Procedure = "kb_Procedure_%'汉";
    const string Variadic = "kb_variadic";
    const string Table = "kb_table_output";
    const string Output = "kb_out";
    const string Empty = "kb_no_args";

    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgRoutineSignaturesPreserveOverloadsArgumentsAndUnknownModifiers(bool async) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var transaction = await connection.BeginTransactionAsync(token); var schema = await Fixtures(connection, token);
        using var functions = await Schema("Functions", [connection.Database, schema]); Assert.Equal(6, functions.Rows.Count);
        using var procedures = await Schema("Procedures", [connection.Database, schema]); Assert.Single(procedures.Rows.Cast<DataRow>());
        using var parameters = await Schema("ProcedureParameters", [connection.Database, schema]); Assert.Equal(11, parameters.Rows.Count);
        using var oracle = new KingbaseCommand("SELECT p.proname::pg_catalog.text, pg_catalog.nameconcatoid(p.proname,p.oid)::pg_catalog.text, " +
            "p.prokind::pg_catalog.text, p.pronargs::pg_catalog.int4, p.pronargdefaults::pg_catalog.int4, p.proretset, " +
            "COALESCE(pg_catalog.array_length(p.proallargtypes,1),p.pronargs)::pg_catalog.int4 " +
            "FROM pg_catalog.pg_proc p WHERE p.pronamespace OPERATOR(pg_catalog.=) pg_catalog.pg_my_temp_schema() " +
            "AND (p.prokind OPERATOR(pg_catalog.=) 'f'::pg_catalog.\"char\" OR p.prokind OPERATOR(pg_catalog.=) 'p'::pg_catalog.\"char\")", connection);
        using (var reader = await oracle.ExecuteReaderAsync(token))
        {
            while (reader.Read())
            {
                var facts = reader.GetString(2) == "p" ? procedures : functions;
                var row = Assert.Single(facts.Rows.Cast<DataRow>(), row => (string)row["SPECIFIC_NAME"] == reader.GetString(1));
                Assert.Equal(reader.GetString(0), row["ROUTINE_NAME"]); Assert.Equal(reader.GetInt32(3), row["INPUT_PARAMETER_COUNT"]);
                Assert.Equal(reader.GetInt32(4), row["DEFAULT_PARAMETER_COUNT"]); Assert.Equal(reader.GetBoolean(5), row["RETURNS_SET"]);
                var args = parameters.Rows.Cast<DataRow>().Where(arg => (string)arg["SPECIFIC_NAME"] == reader.GetString(1)).ToArray();
                Assert.Equal(reader.GetInt32(6), args.Length); Assert.Equal(Enumerable.Range(1, args.Length), args.Select(arg => (int)arg["ORDINAL_POSITION"]));
            }
        }
        using var overloads = await Schema("functions", [null, schema, Overload]); Assert.Equal(2, overloads.Rows.Count);
        var numeric = Assert.Single(overloads.Rows.Cast<DataRow>(), row => (string)row["UDT_NAME"] == "numeric");
        Assert.Equal("FUNCTION", numeric["ROUTINE_TYPE"]); Assert.Equal("SQL", numeric["EXTERNAL_LANGUAGE"]);
        Assert.Equal("DEFINER", numeric["SECURITY_TYPE"]); Assert.True((bool)numeric["IS_DETERMINISTIC"]); Assert.True((bool)numeric["IS_NULL_CALL"]);
        Assert.Equal(DBNull.Value, numeric["NUMERIC_PRECISION"]); Assert.Equal(DBNull.Value, numeric["NUMERIC_SCALE"]);
        using var defaultArg = await Schema("ProcedureParameters", [null, schema, Overload, (string)numeric["SPECIFIC_NAME"], Argument]);
        var defaultRow = Assert.Single(defaultArg.Rows.Cast<DataRow>()); Assert.Equal("42", defaultRow["PARAMETER_DEFAULT"]);
        Assert.Equal("IN", defaultRow["PARAMETER_MODE"]); Assert.Equal("int4", defaultRow["UDT_NAME"]);
        Assert.Single(parameters.Rows.Cast<DataRow>(), row => (string)row["ROUTINE_NAME"] == Overload && row["PARAMETER_NAME"] is DBNull);
        foreach (var column in new[] { "DATA_TYPE", "UDT_CATALOG", "UDT_SCHEMA", "UDT_NAME", "IS_NULL_CALL" })
            Assert.Equal(DBNull.Value, procedures.Rows[0][column]);
        Assert.Equal(new[] { "IN", "INOUT" }, Args(Procedure).Select(row => (string)row["PARAMETER_MODE"]));
        Assert.Equal(new[] { "IN", "OUT", "OUT" }, Args(Output).Select(row => (string)row["PARAMETER_MODE"]));
        Assert.All(Args(Output), row => Assert.False((bool)row["IS_TABLE_COLUMN"]));
        Assert.Equal(new[] { false, true, true }, Args(Table).Select(row => (bool)row["IS_TABLE_COLUMN"]));
        var variadic = Assert.Single(Args(Variadic)); Assert.True((bool)variadic["IS_VARIADIC"]);
        Assert.Equal("IN", variadic["PARAMETER_MODE"]); Assert.Equal("ARRAY", variadic["DATA_TYPE"]); Assert.Equal("_int4", variadic["UDT_NAME"]);
        Assert.Empty(Args(Empty));
        foreach (var name in new[] { "Functions", "Procedures", "ProcedureParameters" })
        {
            using var aggregate = await Schema(name, [null, schema, "kb_routine_aggregate"]); Assert.Empty(aggregate.Rows.Cast<DataRow>());
            using var window = await Schema(name, [null, "pg_catalog", "row_number"]); Assert.Empty(window.Rows.Cast<DataRow>());
        }
        foreach (var name in new[] { "Functions", "Procedures", "ProcedureParameters" })
        {
            foreach (var missing in new[] { "", "kb_absent_%_'汉😀 OR 1=1 --", Overload.ToUpperInvariant() })
            { using var absent = await Schema(name, [null, schema, missing]); Assert.Empty(absent.Rows.Cast<DataRow>()); }
            using var selected = await Schema(name, [null, schema]); var count = selected.Rows.Count;
            selected.Rows.Clear(); using var fresh = await Schema(name, [null, schema]); Assert.Equal(count, fresh.Rows.Count);
        }
        await transaction.RollbackAsync(token);
        using var rolledBack = await Schema("Functions", [null, schema, Overload]); Assert.Empty(rolledBack.Rows.Cast<DataRow>());
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
        DataRow[] Args(string name) => parameters.Rows.Cast<DataRow>().Where(row => (string)row["ROUTINE_NAME"] == name).ToArray();
        Task<DataTable> Schema(string name, string?[] filters) => async ? connection.GetSchemaAsync(name, filters, token) : Task.FromResult(connection.GetSchema(name, filters));
    });

    [LiveFact(writes: true)]
    public Task PgRoutineVisibilityAndDefaultTextFollowCurrentRole() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var transaction = await connection.BeginTransactionAsync(token); var schema = await Fixtures(connection, token);
        await Scalar(connection, "REVOKE ALL ON FUNCTION pg_temp." + Quote(Overload) + "(integer) FROM PUBLIC", token);
        await Scalar(connection, "REVOKE ALL ON PROCEDURE pg_temp." + Quote(Procedure) + "(integer,text) FROM PUBLIC", token);
        await Scalar(connection, "GRANT EXECUTE ON FUNCTION pg_temp." + Quote(Overload) + "(integer) TO pg_monitor", token);
        await Scalar(connection, "SET LOCAL ROLE pg_monitor", token);
        using var visible = await connection.GetSchemaAsync("Functions", [null, schema, Overload], token); Assert.Equal(2, visible.Rows.Count);
        using var parameters = connection.GetSchema("ProcedureParameters", [null, schema, Overload]); Assert.Equal(2, parameters.Rows.Count);
        Assert.All(parameters.Rows.Cast<DataRow>(), row => Assert.Equal(DBNull.Value, row["PARAMETER_DEFAULT"]));
        using var hidden = connection.GetSchema("Procedures", [null, schema, Procedure]); Assert.Empty(hidden.Rows.Cast<DataRow>());
        using var hiddenArgs = await connection.GetSchemaAsync("ProcedureParameters", [null, schema, Procedure], token); Assert.Empty(hiddenArgs.Rows.Cast<DataRow>());
        await transaction.RollbackAsync(token); Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    [LiveFact(writes: true)]
    public Task PgRoutineMetadataExcludesOtherSessionsTemporarySchemas() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var owner = new KingbaseConnection(settings.ConnectionString); await owner.OpenAsync(token);
        using var transaction = await owner.BeginTransactionAsync(token); var schema = await Fixtures(owner, token);
        await transaction.CommitAsync(token);
        using var observer = new KingbaseConnection(settings.ConnectionString); await observer.OpenAsync(token);
        Assert.Equal(8L, await Scalar(observer, "SELECT pg_catalog.count(*) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n " +
            "ON n.oid OPERATOR(pg_catalog.=) p.pronamespace WHERE n.nspname::pg_catalog.text OPERATOR(pg_catalog.=) '" + schema.Replace("'", "''") + "'", token));
        foreach (var name in new[] { "Functions", "Procedures", "ProcedureParameters" })
        { using var hidden = await observer.GetSchemaAsync(name, [null, schema], token); Assert.Empty(hidden.Rows.Cast<DataRow>()); }
    });

    static async Task<string> Fixtures(KingbaseConnection connection, CancellationToken token)
    {
        await Scalar(connection, "CREATE TEMP TABLE kb_routine_anchor (id integer)", token);
        await Scalar(connection, "CREATE FUNCTION pg_temp." + Quote(Overload) + "(" + Quote(Argument) + " integer DEFAULT 42) " +
            "RETURNS numeric(12,2) LANGUAGE SQL IMMUTABLE STRICT SECURITY DEFINER AS 'SELECT $1::numeric'", token);
        await Scalar(connection, "CREATE FUNCTION pg_temp." + Quote(Overload) + "(text) RETURNS text LANGUAGE SQL AS 'SELECT $1'", token);
        await Scalar(connection, "CREATE FUNCTION pg_temp." + Quote(Variadic) + "(VARIADIC vals integer[]) RETURNS integer LANGUAGE SQL AS 'SELECT 1'", token);
        await Scalar(connection, "CREATE FUNCTION pg_temp." + Quote(Table) + "(x integer) RETURNS TABLE (id integer,label text) LANGUAGE SQL AS 'SELECT $1, ''one''::text'", token);
        await Scalar(connection, "CREATE FUNCTION pg_temp." + Quote(Output) + "(IN x integer, OUT doubled integer, OUT label text) LANGUAGE SQL AS 'SELECT $1, ''two''::text'", token);
        await Scalar(connection, "CREATE FUNCTION pg_temp." + Quote(Empty) + "() RETURNS integer LANGUAGE SQL AS 'SELECT 42'", token);
        await Scalar(connection, "CREATE PROCEDURE pg_temp." + Quote(Procedure) + "(IN a integer, INOUT b text) LANGUAGE plpgsql AS 'BEGIN b := ''value''; END'", token);
        await Scalar(connection, "CREATE AGGREGATE pg_temp.kb_routine_aggregate(integer) (SFUNC=pg_catalog.int4pl, STYPE=integer, INITCOND='0')", token);
        return Assert.IsType<string>(await Scalar(connection, "SELECT nspname::pg_catalog.text FROM pg_catalog.pg_namespace " +
            "WHERE oid OPERATOR(pg_catalog.=) pg_catalog.pg_my_temp_schema()", token));
    }
    static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    { using var command = new KingbaseCommand(sql, connection); return await command.ExecuteScalarAsync(token); }
}
