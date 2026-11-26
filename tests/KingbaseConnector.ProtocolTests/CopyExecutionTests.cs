using System.Data;
using KingbaseConnector.Protocol;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class CopyExecutionTests
{
    [Fact]
    public async Task BinaryImportStreamsBoundedFramesAndCompletesOnlyAfterCountAndReady()
    {
        var data = Enumerable.Range(0, 180013).Select(i => (byte)(i % 251)).ToArray();
        await Run(async (connection, token) =>
        {
            using var copy = await connection.BeginRawBinaryCopyAsync("/* lead */ COPY items FROM STDIN (FORMAT BINARY)", token);
            Assert.True(copy.CanWrite); Assert.False(copy.CanRead); Assert.True(copy.IsBinary); Assert.Equal(2, copy.ColumnCount); Assert.Null(copy.RowsCopied);
            Assert.Throws<InvalidOperationException>(() => connection.GetSchema()); using var command = new KingbaseCommand("SELECT 42", connection);
            await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteScalarAsync(token)); await Assert.ThrowsAsync<InvalidOperationException>(() => connection.WaitAsync(0, token));
            await copy.WriteAsync(data, 0, data.Length, token); Assert.Null(copy.RowsCopied); Assert.Equal(2UL, await copy.CompleteAsync(token)); Assert.Equal(2UL, copy.RowsCopied);
            Assert.Equal(2UL, copy.Complete()); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            Assert.Contains("COPY items FROM STDIN", await ReadQueryAsync(stream, token)); await stream.WriteAsync(Response(input: true, binary: true), token);
            var actual = new List<byte>();
            while (true) { var frame = await Frontend(stream, token); if (frame.Code == 'c') { Assert.Empty(frame.Payload); break; } Assert.Equal('d', frame.Code); Assert.InRange(frame.Payload.Length, 1, 65536); actual.AddRange(frame.Payload); }
            Assert.Equal(data, actual); await stream.WriteAsync(Tail(2), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Fact]
    public async Task BinaryExportStreamsADataFrameBeyondTheBufferedQueryLimitAndAcknowledgesEof()
    {
        var data = Enumerable.Range(0, 2 * 1024 * 1024 + 7).Select(i => (byte)(i % 251)).ToArray();
        await Run(async (connection, token) =>
        {
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY items TO STDOUT (FORMAT BINARY)", token); Assert.True(copy.CanRead); Assert.False(copy.CanWrite);
            Assert.Equal(0, await copy.ReadAsync(new byte[1], 0, 0, token)); Assert.Null(copy.RowsCopied);
            using var received = new MemoryStream(); await copy.CopyToAsync(received, 7919, token); Assert.Equal(data, received.ToArray()); Assert.Equal(3UL, copy.RowsCopied);
            Assert.Equal(0, copy.Read(new byte[1], 0, 1)); using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(Response(false, true), Frame('d'), Frame('d', data), Frame('c'), Tail(3)), token);
            await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteBinaryImportSendsCopyFailAndAllowsReuseAfterCancellation(bool explicitCancel)
    {
        await Run(async (connection, token) =>
        {
            var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token); await copy.WriteAsync(new byte[9], 0, 9, token);
            if (explicitCancel) await copy.CancelAsync(token); else copy.Dispose();
            Assert.Null(copy.RowsCopied); copy.Dispose(); await copy.DisposeAsync(); using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token); Assert.Equal('d', (await Frontend(stream, token)).Code);
            var fail = await Frontend(stream, token); Assert.Equal('f', fail.Code); Assert.Equal(0, fail.Payload[^1]);
            await stream.WriteAsync(Error("57014"), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Fact]
    public async Task TextWriterClosesWithExactStrictUtf8WithoutBomAndCompletesOnDispose()
    {
        var expected = "1\t文字😀\n2\t\\N\n";
        await Run(async (connection, token) =>
        {
            var writer = await connection.BeginTextImportAsync("COPY items FROM STDIN", token); writer.Write("1\t文字"); writer.Write('\uD83D'); writer.Write('\uDE00'); await writer.WriteAsync("\n2\t\\N\n");
            writer.Dispose(); Assert.Equal(2UL, writer.RowsCopied); await writer.DisposeAsync(); using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, false), token); var received = new List<byte>();
            while (true) { var frame = await Frontend(stream, token); if (frame.Code == 'c') break; Assert.Equal('d', frame.Code); received.AddRange(frame.Payload); }
            Assert.Equal(Encoding.UTF8.GetBytes(expected), received); await stream.WriteAsync(Tail(2), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Fact]
    public async Task TextExportPreservesBomDataAndDecodesUtf8SplitAcrossCopyMessages()
    {
        var text = "\uFEFF文字😀\t\\N\n"; var data = Encoding.UTF8.GetBytes(text);
        await Run(async (connection, token) =>
        {
            using var reader = await connection.BeginTextExportAsync("COPY items TO STDOUT", token); Assert.Equal(text, await reader.ReadToEndAsync());
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(false, false), token);
            foreach (var value in data) await stream.WriteAsync(Frame('d', [value]), token);
            await stream.WriteAsync(Join(Frame('c'), Tail(1)), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerErrorsBeforeOrDuringCopyDrainAndPermitTheNextCommand(bool afterStart)
    {
        await Run(async (connection, token) =>
        {
            if (afterStart) { using var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token); var error = await Assert.ThrowsAsync<KingbaseException>(() => copy.CompleteAsync(token)); Assert.Equal("22P02", error.SqlState); }
            else { var error = await Assert.ThrowsAsync<KingbaseException>(() => connection.BeginRawBinaryCopyAsync("COPY absent FROM STDIN (FORMAT BINARY)", token)); Assert.Equal("42P01", error.SqlState); }
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); if (afterStart) { await stream.WriteAsync(Response(true, true), token); Assert.Equal('c', (await Frontend(stream, token)).Code); }
            await stream.WriteAsync(Error(afterStart ? "22P02" : "42P01"), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackFailuresWaitForCopyAcknowledgmentAndKeepKnownCountsAndSession(bool notification)
    {
        var failure = new InvalidOperationException("COPY callback");
        await Run(async (connection, token) =>
        {
            if (notification) connection.Notification += (_, _) => throw failure; else connection.Notice += (_, _) => throw failure;
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => copy.CompleteAsync(token))); Assert.Equal(7UL, copy.RowsCopied); Assert.Equal(ConnectionState.Open, connection.State);
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(notification ? NotificationTests.Notify(42, "ch", "") : NoticeTests.Notice(), Response(true, true)), token);
            Assert.Equal('c', (await Frontend(stream, token)).Code); await stream.WriteAsync(Tail(7), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    internal static byte[] Response(bool input, bool binary, int columns = 2) => Frame(input ? 'G' : 'H', [binary ? (byte)1 : (byte)0, (byte)(columns >> 8), (byte)columns, .. Enumerable.Range(0, columns).SelectMany(_ => new byte[] { 0, binary ? (byte)1 : (byte)0 })]);
    internal static byte[] Tail(ulong rows, char status = 'I') => Join(Frame('C', Encoding.ASCII.GetBytes("COPY " + rows.ToString(System.Globalization.CultureInfo.InvariantCulture) + '\0')), Frame('Z', [(byte)status]));
    internal static byte[] Error(string state, char status = 'I') => Join(Frame('E', Encoding.ASCII.GetBytes("SERROR\0C" + state + "\0Mprivate-copy-error\0\0")), Frame('Z', [(byte)status]));
    internal static async Task<(char Code, byte[] Payload)> Frontend(Stream stream, CancellationToken token)
    {
        var header = new byte[5]; await stream.ReadExactlyAsync(header, token); var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)); Assert.InRange(length, 4, 65540);
        var payload = new byte[length - 4]; await stream.ReadExactlyAsync(payload, token); return ((char)header[0], payload);
    }
    internal static async Task Run(Func<KingbaseConnection, CancellationToken, Task> action, Func<Stream, CancellationToken, Task> respond, int commandTimeout = 30, int cleanupTimeout = 5, string? integerDatetimes = null, IEnumerable<PgTypeDescription>? catalogTypes = null)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var settings = Settings(listener); settings.CommandTimeout = commandTimeout; settings.CleanupTimeout = cleanupTimeout; using var connection = new KingbaseConnection(settings.ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token, integerDatetimes: integerDatetimes, catalogTypes: catalogTypes); await respond(stream, guard.Token);
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token)); }
            catch (IOException error) when (error.InnerException is System.Net.Sockets.SocketException socket && socket.SocketErrorCode is System.Net.Sockets.SocketError.ConnectionReset or System.Net.Sockets.SocketError.ConnectionAborted) { }
        }, guard.Token);
        await connection.OpenAsync(guard.Token); await action(connection, guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }
}
