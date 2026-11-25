namespace KingbaseConnector.IntegrationTests;

public sealed class OrdinaryLobLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    public Task PreparedLobColumnsRoundTripBinaryUnicodeAndBufferedPartialReads(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); using var transaction = await connection.BeginTransactionAsync(token);
        var schema = mode == KingbaseCompatibilityMode.Oracle ? "pg_catalog" : "sys";
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_ordinary_lob (b " + schema + ".blob,c " + schema + ".clob,n " + schema + ".nclob)", connection) { Transaction = transaction }) await create.ExecuteNonQueryAsync(token);
        var bytes = Enumerable.Range(0, 65553).Select(i => (byte)(i % 256)).ToArray(); var text = string.Concat(Enumerable.Repeat("LOB_文字😀\n'\\", 2000));
        using (var insert = new KingbaseCommand("INSERT INTO kb_ordinary_lob VALUES (:b,:c,:n)", connection) { Transaction = transaction })
        {
            insert.Parameters.Add(new KingbaseParameter("b", bytes) { KingbaseDbType = KingbaseDbType.Blob });
            insert.Parameters.Add(new KingbaseParameter("c", text) { KingbaseDbType = KingbaseDbType.Clob });
            insert.Parameters.Add(new KingbaseParameter("n", text) { KingbaseDbType = KingbaseDbType.NClob });
            await insert.PrepareAsync(token); Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
        }
        using (var select = new KingbaseCommand("SELECT b,c,n FROM kb_ordinary_lob", connection) { Transaction = transaction })
        using (var reader = await select.ExecuteReaderAsync(token))
        {
            Assert.True(await reader.ReadAsync(token)); Assert.Equal(typeof(byte[]), reader.GetFieldType(0)); Assert.Equal(typeof(string), reader.GetFieldType(1)); Assert.Equal(typeof(string), reader.GetFieldType(2));
            Assert.Equal(bytes.Length, reader.GetBytes(0, 0, null, 0, 0)); var partial = new byte[101]; Assert.Equal(50, reader.GetBytes(0, 65500, partial, 3, 50)); Assert.Equal(bytes.Skip(65500).Take(50), partial.Skip(3).Take(50));
            Assert.Equal(0, reader.GetBytes(0, bytes.Length + 1, partial, 0, 1)); using (var stream = reader.GetStream(0)) { using var copy = new MemoryStream(); await stream.CopyToAsync(copy, 7919, token); Assert.Equal(bytes, copy.ToArray()); }
            foreach (var column in new[] { 1, 2 })
            {
                Assert.Equal(text.Length, reader.GetChars(column, 0, null, 0, 0)); var chars = new char[13]; Assert.Equal(13, reader.GetChars(column, 4, chars, 0, chars.Length)); Assert.Equal(text.Substring(4, 13), new string(chars));
                using var value = reader.GetTextReader(column); Assert.Equal(text, await value.ReadToEndAsync()); Assert.Equal(text, reader.GetString(column));
            }
            Assert.False(await reader.ReadAsync(token));
        }
        await transaction.RollbackAsync(token);
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    public Task ExplicitLobNamesAndTypedNullKeepObservedModeSpecificEmptySemantics(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); var schema = mode == KingbaseCompatibilityMode.Oracle ? "pg_catalog." : "sys.";
        using var query = new KingbaseCommand("SELECT :b,:c,:n", connection);
        query.Parameters.Add(new KingbaseParameter("b", Array.Empty<byte>()) { DataTypeName = schema + "blob" });
        query.Parameters.Add(new KingbaseParameter("c", "") { DataTypeName = schema + "clob" });
        query.Parameters.Add(new KingbaseParameter("n", "") { DataTypeName = schema + "nclob" }); await query.PrepareAsync(token);
        using (var empty = await query.ExecuteReaderAsync(token))
        {
            Assert.True(empty.Read()); Assert.False(empty.IsDBNull(0)); Assert.Empty(empty.GetFieldValue<byte[]>(0));
            foreach (var column in new[] { 1, 2 }) { Assert.Equal(mode == KingbaseCompatibilityMode.Oracle, empty.IsDBNull(column)); if (!empty.IsDBNull(column)) Assert.Equal("", empty.GetString(column)); }
        }
        foreach (KingbaseParameter parameter in query.Parameters) parameter.Value = DBNull.Value;
        using var nulls = await query.ExecuteReaderAsync(token); Assert.True(nulls.Read()); for (var i = 0; i < 3; i++) Assert.True(nulls.IsDBNull(i));
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task UnavailableLobNamesRejectLocallyWithoutAByteaOrOidFallback(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); using var query = new KingbaseCommand("SELECT :v", connection);
        query.Parameters.Add(new KingbaseParameter("v", DBNull.Value) { KingbaseDbType = KingbaseDbType.Blob }); await Assert.ThrowsAsync<NotSupportedException>(() => query.ExecuteScalarAsync(token));
        query.Parameters.Clear(); query.CommandText = "SELECT 42"; Assert.Equal(42, await query.ExecuteScalarAsync(token));
    });
}
