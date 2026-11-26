using System.Collections;

namespace KingbaseConnector.IntegrationTests;

public sealed class BinaryCopyJsonBitLiveTests
{
    static readonly KingbaseDbType[] Types = [KingbaseDbType.Json, KingbaseDbType.Jsonb, KingbaseDbType.Bit, KingbaseDbType.Varbit];
    const string Definition = "j pg_catalog.json,n pg_catalog.jsonb,b pg_catalog.bit(9),v pg_catalog.varbit";
    const string Json = "{ \"b\": 1, \"a\": 2, \"a\": 3 }";
    const string Jsonb = "{\"a\": 3, \"b\": 1}";

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ServerSendFunctionsAndGeneratedExportEstablishFormats(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        (string Function, string Literal, string Hex)[] cases = [
            ("json_send", "'{\"x\":1}'::pg_catalog.json", "7b2278223a317d"),
            ("jsonb_send", "'{\"x\":1}'::pg_catalog.jsonb", "017b2278223a20317d"),
            ("bit_send", "'001011001'::pg_catalog.bit(9)", "000000092c80"),
            ("varbit_send", "'001011001'::pg_catalog.varbit", "000000092c80") ];
        foreach (var item in cases)
        {
            using var send = new KingbaseCommand($"SELECT pg_catalog.encode(pg_catalog.{item.Function}({item.Literal}),'hex')", connection);
            Assert.Equal(item.Hex, await send.ExecuteScalarAsync(token));
        }
        using var output = connection.BeginBinaryExport("COPY (SELECT '{\"x\":1}'::pg_catalog.json,'{\"x\":1}'::pg_catalog.jsonb,'001011001'::pg_catalog.bit(9),'0'::pg_catalog.varbit) TO STDOUT (FORMAT BINARY)", Types);
        var row = (await output.ReadRowAsync(token))!;
        Assert.Equal("{\"x\":1}", row[0]); Assert.Equal("{\"x\": 1}", row[1]); EqualBits(Bits("001011001"), row[2]); EqualBits(Bits("0"), row[3]);
        Assert.Null(output.ReadRow()); Assert.Equal(1UL, output.RowsCopied);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ImportMatchesIndependentTextReaderAndNormalizedBinaryExport(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_json_bits (" + Definition + ")", connection)) await create.ExecuteNonQueryAsync(token);
        using (var input = await connection.BeginBinaryImportAsync("COPY kb_copy_json_bits FROM STDIN (FORMAT BINARY)", Types, token))
        {
            input.WriteRow(Json, Json, Bits("001011001"), Bits(""));
            input.WriteRow("\"界😀\"", "\"界😀\"", Bits("111111111"), Bits("01001011"));
            input.WriteRow(DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
            Assert.Equal(3UL, await input.CompleteAsync(token));
        }
        using (var select = new KingbaseCommand("SELECT j,n,b,v FROM kb_copy_json_bits", connection))
        using (var reader = await select.ExecuteReaderAsync(token))
        {
            Assert.True(reader.Read()); Assert.Equal(Json, reader.GetString(0)); Assert.Equal(Jsonb, reader.GetString(1)); EqualBits(Bits("001011001"), reader.GetValue(2)); EqualBits(Bits(""), reader.GetValue(3));
            Assert.True(reader.Read()); Assert.Equal("\"界😀\"", reader.GetString(0)); Assert.Equal("\"界😀\"", reader.GetString(1)); EqualBits(Bits("111111111"), reader.GetValue(2)); EqualBits(Bits("01001011"), reader.GetValue(3));
            Assert.True(reader.Read()); for (var i = 0; i < 4; i++) Assert.True(reader.IsDBNull(i)); Assert.False(reader.Read());
        }
        using var output = await connection.BeginBinaryExportAsync("COPY kb_copy_json_bits TO STDOUT (FORMAT BINARY)", Types, token);
        var first = output.ReadRow()!; Assert.Equal(Json, first[0]); Assert.Equal(Jsonb, first[1]); EqualBits(Bits("001011001"), first[2]); EqualBits(Bits(""), first[3]);
        var second = output.ReadRow()!; Assert.Equal("\"界😀\"", second[0]); Assert.Equal("\"界😀\"", second[1]); EqualBits(Bits("111111111"), second[2]); EqualBits(Bits("01001011"), second[3]);
        Assert.All(output.ReadRow()!, value => Assert.IsType<DBNull>(value)); Assert.Null(output.ReadRow()); Assert.Equal(3UL, output.RowsCopied);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ServerJsonSyntaxRejectionSynchronizesWithoutClientParsing(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        foreach (var type in new[] { KingbaseDbType.Json, KingbaseDbType.Jsonb })
        {
            using (var create = new KingbaseCommand($"CREATE TEMP TABLE kb_invalid_{type} (v pg_catalog.{type.ToString().ToLowerInvariant()})", connection)) await create.ExecuteNonQueryAsync(token);
            var error = await Assert.ThrowsAsync<KingbaseException>(async () =>
            {
                using var input = connection.BeginBinaryImport($"COPY kb_invalid_{type} FROM STDIN (FORMAT BINARY)", type);
                await input.WriteRowAsync(["{invalid}"], token); await input.CompleteAsync(token);
            });
            Assert.Equal("22P02", error.SqlState);
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task MaximumClientBitsKeepTheirDeclaredLengthAndLastBit(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_bit_limit (v pg_catalog.varbit)", connection)) await create.ExecuteNonQueryAsync(token);
        var bits = new BitArray(1024 * 1024); bits[0] = true; bits[bits.Length - 1] = true;
        using (var input = connection.BeginBinaryImport("COPY kb_copy_bit_limit FROM STDIN (FORMAT BINARY)", KingbaseDbType.Varbit))
        { input.WriteRow(bits); Assert.Equal(1UL, input.Complete()); }
        using (var command = new KingbaseCommand("SELECT pg_catalog.bit_length(v) FROM kb_copy_bit_limit", connection)) Assert.Equal(bits.Length, await command.ExecuteScalarAsync(token));
        using var output = connection.BeginBinaryExport("COPY kb_copy_bit_limit TO STDOUT (FORMAT BINARY)", KingbaseDbType.Varbit);
        EqualBits(bits, output.ReadRow()![0]); Assert.Null(output.ReadRow()); Assert.Equal(1UL, output.RowsCopied);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task RollbackAndTwoPooledBorrowersPreserveCopyOwnership(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build(); KingbaseBinaryImporter? stale = null; int? pid = null;
        try
        {
            for (var lease = 0; lease < 2; lease++)
            {
                using var connection = await source.OpenConnectionAsync(token);
                using (var identity = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", connection))
                { var current = Assert.IsType<int>(await identity.ExecuteScalarAsync(token)); if (pid is null) pid = current; else Assert.Equal(pid, current); }
                stale?.Dispose();
                using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_json_owner (" + Definition + ")", connection)) await create.ExecuteNonQueryAsync(token);
                using (var transaction = connection.BeginTransaction())
                {
                    using (var input = connection.BeginBinaryImport("COPY kb_copy_json_owner FROM STDIN (FORMAT BINARY)", Types))
                    { input.WriteRow("{}", "[]", Bits("001011001"), Bits("1")); Assert.Equal(1UL, input.Complete()); }
                    transaction.Rollback();
                }
                using (var count = new KingbaseCommand("SELECT COUNT(*) FROM kb_copy_json_owner", connection)) Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture));
                stale = connection.BeginBinaryImport("COPY kb_copy_json_owner FROM STDIN (FORMAT BINARY)", Types);
                stale.WriteRow("{}", "[]", Bits("001011001"), Bits("1")); Assert.Equal(1UL, stale.Complete());
                using var output = connection.BeginBinaryExport("COPY kb_copy_json_owner TO STDOUT (FORMAT BINARY)", Types);
                var row = output.ReadRow()!; Assert.Equal("{}", row[0]); Assert.Equal("[]", row[1]); EqualBits(Bits("001011001"), row[2]); EqualBits(Bits("1"), row[3]); Assert.Null(output.ReadRow());
            }
        }
        finally { stale?.Dispose(); }
    });

    static BitArray Bits(string text) => new(text.Select(c => c == '1').ToArray());
    static void EqualBits(BitArray expected, object actual) => Assert.Equal(expected.Cast<bool>().ToArray(), Assert.IsType<BitArray>(actual).Cast<bool>().ToArray());
}
