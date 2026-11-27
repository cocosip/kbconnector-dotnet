using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.IntegrationTests;

// E72 LIVE: sys.binary/sys.varbinary read and binary COPY contracts in the two modes that define the types.
public sealed class BinaryTypeLiveTests
{
    const string Columns = "(id int, b binary(4), v varbinary(16), e varbinary(16))";

    [LiveTheory(true)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task BinaryColumnsReadAsByteArrays(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        await using var connection = await Open(settings, token);
        await Execute(connection, "CREATE TEMPORARY TABLE kb_binary_read" + Columns, token);
        await Execute(connection, "INSERT INTO kb_binary_read VALUES(1, CAST('ab' AS binary(4)), CAST('ab' AS varbinary(16)), CAST('' AS varbinary(16)))", token);
        await Execute(connection, "INSERT INTO kb_binary_read(id, b, v) VALUES(2, NULL, NULL)", token);
        await using var command = new KingbaseCommand(
            "SELECT b, v, e, pg_typeof(b)::text, pg_typeof(v)::text FROM kb_binary_read ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(new byte[] { 0x61, 0x62, 0x00, 0x00 }, (byte[])reader.GetValue(0));
        Assert.Equal(new byte[] { 0x61, 0x62 }, (byte[])reader.GetValue(1));
        Assert.Empty((byte[])reader.GetValue(2));
        Assert.Equal("binary", reader.GetString(3));
        Assert.Equal("varbinary", reader.GetString(4));
        Assert.True(await reader.ReadAsync(token));
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
        Assert.False(await reader.ReadAsync(token));
    });

    [LiveTheory(true)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ScalarWritesAreRejectedInsteadOfStoringText(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        await using var connection = await Open(settings, token);
        await Execute(connection, "CREATE TEMPORARY TABLE kb_binary_write" + Columns, token);
        await using var insert = new KingbaseCommand("INSERT INTO kb_binary_write(v) VALUES(:v)", connection);
        insert.Parameters.Add(new KingbaseParameter("v", new byte[] { 0x00, 0xFF }) { KingbaseDbType = KingbaseDbType.VarBinary });
        // The server accepts literal bytes as text and rejects the PG binary Bind format, so the driver refuses
        // the write rather than storing the ASCII of the bytes.
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => insert.ExecuteNonQueryAsync(token));
        Assert.Contains("text parameter format", error.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Open, connection.State);
        await using var count = new KingbaseCommand("SELECT count(*) FROM kb_binary_write", connection);
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture));
    });

    [LiveTheory(true)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task BinaryCopyRoundTripsTheNativeTypes(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        await using var connection = await Open(settings, token);
        await Execute(connection, "CREATE TEMPORARY TABLE kb_binary_copy(v varbinary(16), n varbinary(16))", token);
        await Execute(connection, "INSERT INTO kb_binary_copy VALUES(CAST('ab' AS varbinary(16)), NULL)", token);
        await Execute(connection, "INSERT INTO kb_binary_copy VALUES(CAST('' AS varbinary(16)), CAST('z' AS varbinary(16)))", token);
        await using (var export = await connection.BeginBinaryExportAsync(
            "COPY (SELECT v, n FROM kb_binary_copy) TO STDOUT (FORMAT binary)", [KingbaseDbType.VarBinary, KingbaseDbType.VarBinary], token))
        {
            var first = await export.ReadRowAsync(token);
            Assert.NotNull(first);
            Assert.Equal(new byte[] { 0x61, 0x62 }, (byte[])first![0]);
            Assert.Equal(DBNull.Value, first[1]);
            var second = await export.ReadRowAsync(token);
            Assert.NotNull(second);
            Assert.Empty((byte[])second![0]);
            Assert.Equal(new byte[] { 0x7A }, (byte[])second[1]);
            Assert.Null(await export.ReadRowAsync(token));
            Assert.Equal(2UL, export.RowsCopied);
        }
        await Execute(connection, "CREATE TEMPORARY TABLE kb_binary_import(id int, v varbinary(16), n varbinary(16))", token);
        await using (var import = await connection.BeginBinaryImportAsync(
            "COPY kb_binary_import(id, v, n) FROM STDIN (FORMAT binary)", [KingbaseDbType.Integer, KingbaseDbType.VarBinary, KingbaseDbType.VarBinary], token))
        {
            await import.WriteRowAsync([1, new byte[] { 0xFF, 0x00 }, null], token);
            await import.WriteRowAsync([2, Array.Empty<byte>(), new byte[] { 0x7A }], token);
            Assert.Equal(2UL, await import.CompleteAsync(token));
        }
        await using var check = new KingbaseCommand("SELECT v, n FROM kb_binary_import ORDER BY id", connection);
        await using var reader = await check.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(new byte[] { 0xFF, 0x00 }, (byte[])reader.GetValue(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(await reader.ReadAsync(token));
        Assert.Empty((byte[])reader.GetValue(0));
        Assert.Equal(new byte[] { 0x7A }, (byte[])reader.GetValue(1));
        Assert.False(await reader.ReadAsync(token));
    });

    [LiveTheory(true)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task BinaryCopyAlsoHandlesFixedWidthBinary(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        await using var connection = await Open(settings, token);
        await Execute(connection, "CREATE TEMPORARY TABLE kb_binary_fixed(id int, b binary(4))", token);
        await Execute(connection, "INSERT INTO kb_binary_fixed(id, b) VALUES(1, CAST('ab' AS binary(4)))", token);
        await using var export = await connection.BeginBinaryExportAsync(
            "COPY (SELECT b FROM kb_binary_fixed) TO STDOUT (FORMAT binary)", [KingbaseDbType.Binary], token);
        var row = await export.ReadRowAsync(token);
        Assert.NotNull(row);
        Assert.Equal(new byte[] { 0x61, 0x62, 0x00, 0x00 }, (byte[])row![0]);
        Assert.Null(await export.ReadRowAsync(token));
    });

    static async Task Execute(KingbaseConnection connection, string sql, CancellationToken token)
    {
        await using var command = new KingbaseCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    static async Task<KingbaseConnection> Open(KingbaseConnectionStringBuilder settings, CancellationToken token)
    {
        var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        return connection;
    }
}
