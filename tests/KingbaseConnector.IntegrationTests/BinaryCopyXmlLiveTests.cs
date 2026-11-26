using System.Text;

namespace KingbaseConnector.IntegrationTests;

public sealed class BinaryCopyXmlLiveTests
{
    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task IndependentServerSendAndSqlExportEstablishUtf8Normalization(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        (string Input, string Output)[] cases = [
            ("<x>界😀é</x>", "<x>界😀é</x>"),
            ("<?xml version=\"1.0\" encoding=\"UTF-8\"?><x/>", "<x/>"),
            ("<?xml version='1.0' encoding='UTF-8' standalone='yes'?><x/>", "<?xml version=\"1.0\" standalone=\"yes\"?><x/>"),
            ("", ""), ("<x/>text<y/>", "<x/>text<y/>") ];
        foreach (var item in cases)
        {
            var expression = "'" + item.Input.Replace("'", "''") + "'::pg_catalog.xml";
            var nullLiteral = mode == KingbaseCompatibilityMode.Oracle && item.Input.Length == 0;
            using (var send = new KingbaseCommand($"SELECT pg_catalog.encode(pg_catalog.xml_send({expression}),'hex')", connection))
            {
                var value = await send.ExecuteScalarAsync(token);
                if (nullLiteral) Assert.IsType<DBNull>(value);
                else Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(item.Output)).ToLowerInvariant(), value);
            }
            using var output = connection.BeginBinaryExport($"COPY (SELECT {expression}) TO STDOUT (FORMAT BINARY)", KingbaseDbType.Xml);
            var row = (await output.ReadRowAsync(token))!;
            if (nullLiteral) Assert.IsType<DBNull>(row[0]); else Assert.Equal(item.Output, row[0]);
            Assert.Null(output.ReadRow()); Assert.Equal(1UL, output.RowsCopied);
        }
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task UnicodeInputMatchesIndependentReaderAndServerBinaryOutput(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_xml (id pg_catalog.int4,v pg_catalog.xml)", connection)) await create.ExecuteNonQueryAsync(token);
        (string Input, string Text, string Binary)[] cases = [
            ("<x>界😀é</x>", "<x>界😀é</x>", "<x>界😀é</x>"),
            ("\ufeff<?xml version='1.0' encoding='UTF-16'?><x>界😀é</x>", "<x>界😀é</x>", "<x>界😀é</x>"),
            ("<?xml version=\"1.0\" encoding=\"ISO-8859-1\" standalone='yes'?><x>界é</x>", "<?xml version=\"1.0\" standalone=\"yes\"?><x>界é</x>", "<?xml version=\"1.0\" standalone=\"yes\"?><x>界é</x>"),
            ("<?xml version='1.0' encoding='unknown-encoding'?><x/>", "<x/>", "<x/>"),
            ("", "", ""), ("<x/>text<y/>", "<x/>text<y/>", "<x/>text<y/>") ];
        using (var input = await connection.BeginBinaryImportAsync("COPY kb_copy_xml FROM STDIN (FORMAT BINARY)", [KingbaseDbType.Integer, KingbaseDbType.Xml], token))
        {
            for (var i = 0; i < cases.Length; i++) await input.WriteRowAsync([i, cases[i].Input], token);
            input.WriteRow(cases.Length, DBNull.Value); Assert.Equal((ulong)cases.Length + 1, await input.CompleteAsync(token));
        }
        using (var command = new KingbaseCommand("SELECT id,v,pg_catalog.encode(pg_catalog.xml_send(v),'hex') FROM kb_copy_xml ORDER BY id", connection))
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            for (var i = 0; i < cases.Length; i++)
            {
                Assert.True(reader.Read()); Assert.Equal(i, reader.GetInt32(0)); Assert.Equal(cases[i].Text, reader.GetString(1));
                Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(cases[i].Binary)).ToLowerInvariant(), reader.GetString(2));
            }
            Assert.True(reader.Read()); Assert.True(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(2)); Assert.False(reader.Read());
        }
        using var output = await connection.BeginBinaryExportAsync("COPY (SELECT v FROM kb_copy_xml ORDER BY id) TO STDOUT (FORMAT BINARY)", [KingbaseDbType.Xml], token);
        foreach (var item in cases) Assert.Equal(item.Binary, output.ReadRow()![0]);
        Assert.IsType<DBNull>(output.ReadRow()![0]); Assert.Null(output.ReadRow()); Assert.Equal((ulong)cases.Length + 1, output.RowsCopied);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task InvalidXmlRejectionSynchronizesAndDoesNotCommitPartialImport(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_xml_invalid (v pg_catalog.xml)", connection)) await create.ExecuteNonQueryAsync(token);
        foreach (var invalid in new[] { (Text: "<x>", State: "2200N"), (Text: "<?xml version='1.0' encoding='UTF-16' extra='bad'?><x/>", State: "22023") })
        {
            var error = await Assert.ThrowsAsync<KingbaseException>(async () =>
            {
                using var input = connection.BeginBinaryImport("COPY kb_copy_xml_invalid FROM STDIN (FORMAT BINARY)", KingbaseDbType.Xml);
                input.WriteRow("<x/>"); input.WriteRow(invalid.Text); await input.CompleteAsync(token);
            });
            Assert.Equal(invalid.State, error.SqlState);
            using (var count = new KingbaseCommand("SELECT COUNT(*) FROM kb_copy_xml_invalid", connection)) Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture));
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task RollbackAndTwoPooledBorrowersPreserveOwnership(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
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
                using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_xml_owner (v pg_catalog.xml)", connection)) await create.ExecuteNonQueryAsync(token);
                using (var transaction = connection.BeginTransaction())
                {
                    using (var input = connection.BeginBinaryImport("COPY kb_copy_xml_owner FROM STDIN (FORMAT BINARY)", KingbaseDbType.Xml))
                    { input.WriteRow("<?xml version='1.0' encoding='UTF-16'?><x>界</x>"); Assert.Equal(1UL, input.Complete()); }
                    transaction.Rollback();
                }
                using (var count = new KingbaseCommand("SELECT COUNT(*) FROM kb_copy_xml_owner", connection)) Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture));
                stale = connection.BeginBinaryImport("COPY kb_copy_xml_owner FROM STDIN (FORMAT BINARY)", KingbaseDbType.Xml);
                stale.WriteRow("<x>界</x>"); Assert.Equal(1UL, stale.Complete());
                using var output = connection.BeginBinaryExport("COPY kb_copy_xml_owner TO STDOUT (FORMAT BINARY)", KingbaseDbType.Xml);
                Assert.Equal("<x>界</x>", output.ReadRow()![0]); Assert.Null(output.ReadRow());
            }
        }
        finally { stale?.Dispose(); }
    });
}
