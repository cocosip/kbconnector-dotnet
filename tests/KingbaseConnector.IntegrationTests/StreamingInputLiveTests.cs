using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class StreamingInputLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, "unnamed")]
    [InlineData(KingbaseCompatibilityMode.Oracle, "unnamed")]
    [InlineData(KingbaseCompatibilityMode.MySql, "unnamed")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "unnamed")]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, "explicit")]
    [InlineData(KingbaseCompatibilityMode.Oracle, "explicit")]
    [InlineData(KingbaseCompatibilityMode.MySql, "explicit")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "explicit")]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, "automatic")]
    [InlineData(KingbaseCompatibilityMode.Oracle, "automatic")]
    [InlineData(KingbaseCompatibilityMode.MySql, "automatic")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "automatic")]
    public Task NonseekableInputsRoundTripBeyondTheBufferedLimit(KingbaseCompatibilityMode mode, string preparation) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.MaxAutoPrepare = preparation == "automatic" ? 2 : 0; settings.AutoPrepareMinUsages = 1;
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_stream_input (b pg_catalog.bytea,t pg_catalog.text)", connection)) await create.ExecuteNonQueryAsync(token);
        var data = Enumerable.Range(0, 700013).Select(i => (byte)(i % 251)).ToArray(); var text = "\uFEFF" + new string('界', 360000) + "😀";
        using var input = new NonseekableStream(data); using var characters = new CountingReader(text);
        using (var insert = new KingbaseCommand("INSERT INTO kb_stream_input VALUES(:b,:t)", connection))
        {
            insert.Parameters.Add(new KingbaseParameter("b", input) { DbType = DbType.Binary, Size = data.Length });
            insert.Parameters.Add(new KingbaseParameter("t", characters) { DbType = DbType.String, Size = text.Length });
            if (preparation == "explicit") { await insert.PrepareAsync(token); Assert.Equal(0, input.ReadCalls); Assert.Equal(0, characters.ReadCalls); }
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(token)); Assert.True(input.CanRead); Assert.False(characters.WasDisposed);
        }
        using var select = new KingbaseCommand("SELECT b,t FROM kb_stream_input", connection); using var reader = await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        Assert.True(await reader.ReadAsync(token)); using (var value = reader.GetStream(0)) { using var actual = new MemoryStream(); await value.CopyToAsync(actual, token); Assert.Equal(data, actual.ToArray()); }
        using (var value = reader.GetTextReader(1)) Assert.Equal(text, await value.ReadToEndAsync(token)); Assert.False(await reader.ReadAsync(token));
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.Oracle, false)]
    [InlineData(KingbaseCompatibilityMode.MySql, false)]
    [InlineData(KingbaseCompatibilityMode.Oracle, true)]
    [InlineData(KingbaseCompatibilityMode.MySql, true)]
    public Task OrdinaryLobDomainInputsPreserveDeclaredOids(KingbaseCompatibilityMode mode, bool qualified) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        var data = Enumerable.Range(0, 600013).Select(i => (byte)(i % 251)).ToArray(); var text = "LOB_" + new string('界', 360000) + "😀";
        using var binary = new NonseekableStream(data); using var clob = new CountingReader(text); using var nclob = new CountingReader(text);
        using var command = new KingbaseCommand("SELECT :b,:c,:n", connection);
        var schema = mode == KingbaseCompatibilityMode.Oracle ? "pg_catalog." : "sys.";
        foreach (var (name, value, provider) in new (string, object, KingbaseDbType)[] { ("b", binary, KingbaseDbType.Blob), ("c", clob, KingbaseDbType.Clob), ("n", nclob, KingbaseDbType.NClob) })
        {
            var parameter = new KingbaseParameter(name, value);
            if (qualified) parameter.DataTypeName = schema + provider.ToString().ToLowerInvariant(); else parameter.KingbaseDbType = provider;
            command.Parameters.Add(parameter);
        }
        await command.PrepareAsync(token); Assert.Equal(0, binary.ReadCalls); Assert.Equal(0, clob.ReadCalls); Assert.Equal(0, nclob.ReadCalls);
        using (var schemaOnly = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, token)) { Assert.Equal(0, binary.ReadCalls); Assert.Equal(0, clob.ReadCalls); }
        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(await reader.ReadAsync(token));
        using (var value = reader.GetStream(0)) { using var actual = new MemoryStream(); await value.CopyToAsync(actual, token); Assert.Equal(data, actual.ToArray()); }
        foreach (var column in new[] { 1, 2 }) { using var value = reader.GetTextReader(column); Assert.Equal(text, await value.ReadToEndAsync(token)); }
        Assert.False(await reader.ReadAsync(token)); Assert.True(binary.CanRead); Assert.False(clob.WasDisposed); Assert.False(nclob.WasDisposed);
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task LocalInputRejectionKeepsThePhysicalSessionUsable(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var input = new CountingReader("bad\0text"); using var command = new KingbaseCommand("SELECT :p", connection);
        command.Parameters.Add(new KingbaseParameter("p", input) { DbType = DbType.String });
        await Assert.ThrowsAsync<System.Text.EncoderFallbackException>(() => command.ExecuteScalarAsync(token));
        Assert.Equal(ConnectionState.Open, connection.State); using var next = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await next.ExecuteScalarAsync(token));
    });

    sealed class NonseekableStream(byte[] data) : MemoryStream(data, writable: false)
    {
        internal int ReadCalls { get; private set; }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { ReadCalls++; return base.ReadAsync(buffer, offset, count, token); }
    }
    sealed class CountingReader(string text) : StringReader(text)
    {
        internal int ReadCalls { get; private set; }
        internal bool WasDisposed { get; private set; }
        public override Task<int> ReadAsync(char[] buffer, int index, int count) { ReadCalls++; return base.ReadAsync(buffer, index, count); }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
