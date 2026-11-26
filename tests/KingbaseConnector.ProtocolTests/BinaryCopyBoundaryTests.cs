using System.Data;
using System.Reflection;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.CopyExecutionTests;
using static KingbaseConnector.ProtocolTests.BinaryCopyTests;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class BinaryCopyBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DispatchedRowsOwnTheirSnapshotAndCancellationCannotPublishAPartialRow(bool cancel) => Run(async (connection, token) =>
    {
        var selectors = new[] { KingbaseDbType.Bytea, KingbaseDbType.Text };
        using var input = await connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", selectors, token);
        selectors[0] = KingbaseDbType.Bigint;
        var physical = (PgPhysicalSession)typeof(KingbaseConnection).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        var wire = (PgWire)typeof(PgQuerySession).GetField("_wire", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(physical.Queries)!;
        var field = typeof(PgWire).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var gated = new RowGateStream((Stream)field.GetValue(wire)!); field.SetValue(wire, gated);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(token);
        var bytes = new byte[] { 1,2,3 }; var pending = input.WriteRowAsync([bytes, "snapshot"], call.Token);
        await gated.Dispatched.Task.WaitAsync(token); bytes[0] = 9;
        if (cancel)
        {
            call.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Null(input.RowsCopied); Assert.Equal(ConnectionState.Broken, connection.State);
        }
        else
        { gated.Release.SetResult(); await pending; Assert.Equal(1UL, await input.CompleteAsync(token)); }
    }, async (stream, token) =>
    {
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token);
        Assert.Equal(BinaryHeader(), (await Frontend(stream, token)).Payload);
        Assert.Equal(new byte[] { 0,2 }, (await Frontend(stream, token)).Payload);
        if (!cancel)
        {
            Assert.Equal(Join(Int32(3), [1,2,3], Int32(8), System.Text.Encoding.ASCII.GetBytes("snapshot"), [255,255]), await ReadImport(stream, token));
            await stream.WriteAsync(Tail(1), token);
        }
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    public Task ZeroAndMaximumColumnsHaveUnambiguousRows(int columns) => Run(async (connection, token) =>
    {
        await Assert.ThrowsAsync<ArgumentException>(() => connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", new KingbaseDbType[1025], token));
        var types = Enumerable.Repeat(KingbaseDbType.Integer, columns).ToArray();
        var row = Enumerable.Repeat<object?>(DBNull.Value, columns).ToArray();
        using (var input = await connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", types, token))
        { input.WriteRow(row); Assert.Equal(1UL, input.Complete()); }
        using var output = await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", types, token);
        Assert.Equal(row, output.ReadRow()); Assert.Null(output.ReadRow()); Assert.Equal(1UL, output.RowsCopied);
    }, async (stream, token) =>
    {
        var literal = Join(BinaryHeader(), [(byte)(columns >> 8), (byte)columns], Enumerable.Repeat((byte)255, columns * 4).ToArray(), [255,255]);
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true, columns), token);
        Assert.Equal(literal, await ReadImport(stream, token)); await stream.WriteAsync(Tail(1), token);
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(Response(false, true, columns), Frame('d', literal), Frame('c'), Tail(1)), token);
    });

    [Fact]
    public Task ExactRowBudgetAllowsCopiesWhoseCumulativeSizeExceedsIt() => Run(async (connection, token) =>
    {
        var bytes = new byte[1024 * 1024]; bytes[0] = 127; bytes[^1] = 255;
        var types = new[] { KingbaseDbType.Bytea, KingbaseDbType.Text };
        using (var input = await connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", types, token))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => input.WriteRowAsync([bytes, "x"], token));
            await input.WriteRowAsync([bytes, DBNull.Value], token); await input.WriteRowAsync([bytes, DBNull.Value], token);
            Assert.Equal(2UL, await input.CompleteAsync(token));
        }
        using var output = await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", types, token);
        for (var i = 0; i < 2; i++)
        { var row = Assert.IsType<object[]>(await output.ReadRowAsync(token)); Assert.Equal(bytes, Assert.IsType<byte[]>(row[0])); Assert.Equal(DBNull.Value, row[1]); }
        Assert.Null(await output.ReadRowAsync(token)); Assert.Equal(2UL, output.RowsCopied);
    }, async (stream, token) =>
    {
        var bytes = new byte[1048576]; bytes[0] = 127; bytes[^1] = 255;
        var row = Join([0,2], Int32(1048576), bytes, Int32(-1));
        var literal = Join(BinaryHeader(), row, row, [255,255]);
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token);
        Assert.Equal(literal, await ReadImport(stream, token)); await stream.WriteAsync(Tail(2), token);
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(Response(false, true), Frame('d', literal), Frame('c'), Tail(2)), token);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task StartupColumnMismatchCancelsImportOrRetiresExport(bool import) => Run(async (connection, token) =>
    {
        if (import)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", new[] { KingbaseDbType.Integer }, token));
            Assert.Equal(ConnectionState.Open, connection.State);
            using var next = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await next.ExecuteScalarAsync(token));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", new[] { KingbaseDbType.Integer }, token));
            Assert.Equal(ConnectionState.Broken, connection.State);
        }
    }, async (stream, token) =>
    {
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(import, true), token);
        if (import)
        { Assert.Equal('f', (await Frontend(stream, token)).Code); await stream.WriteAsync(Error("57014"), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token); }
    });

    [Fact]
    public Task ServerFilteringReportsAcknowledgedRowsRatherThanSentRows() => Run(async (connection, token) =>
    {
        using var input = await connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY) WHERE id > 0", new[] { KingbaseDbType.Integer, KingbaseDbType.Text }, token);
        input.WriteRow(-1, "filtered"); input.WriteRow(42, "kept"); Assert.Equal(1UL, await input.CompleteAsync(token));
        Assert.Equal(1UL, input.RowsCopied); Assert.Equal(ConnectionState.Open, connection.State);
        using var next = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await next.ExecuteScalarAsync(token));
    }, async (stream, token) =>
    {
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token);
        await ReadImport(stream, token); await stream.WriteAsync(Tail(1), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
    });

    [Fact]
    public Task ImportAcknowledgmentCannotHideAnIncorrectRowCount() => Run(async (connection, token) =>
    {
        using var input = await connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", new[] { KingbaseDbType.Integer, KingbaseDbType.Text }, token);
        input.WriteRow(42, "data"); await Assert.ThrowsAsync<IOException>(() => input.CompleteAsync(token));
        Assert.Null(input.RowsCopied); Assert.Equal(ConnectionState.Broken, connection.State);
    }, async (stream, token) =>
    { await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token); await ReadImport(stream, token); await stream.WriteAsync(Tail(2), token); });

    [Fact]
    public Task InvalidBooleanCannotBecomeAValidRow() => Run(async (connection, token) =>
    {
        using var output = await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", new[] { KingbaseDbType.Boolean, KingbaseDbType.Text }, token);
        await Assert.ThrowsAsync<IOException>(() => output.ReadRowAsync(token)); Assert.Null(output.RowsCopied); Assert.Equal(ConnectionState.Broken, connection.State);
    }, async (stream, token) =>
    {
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(Response(false, true), Frame('d', Join(BinaryHeader(), [0,2], Int32(1), [2], Int32(-1), [255,255])), Frame('c'), Tail(1)), token);
    });

    [Fact]
    public async Task PendingRowRejectsConcurrentReadAndDisposeWithoutPoisoningItsOwner()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            var output = await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", new[] { KingbaseDbType.Integer, KingbaseDbType.Text }, token);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pending = output.ReadRowAsync(call.Token); started.SetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => output.ReadRowAsync(token));
            Assert.Throws<InvalidOperationException>(() => output.Dispose());
            call.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); await output.DisposeAsync();
            Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        { await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(Response(false, true), Frame('d', BinaryHeader())), token); await started.Task.WaitAsync(token); });
    }

    [Theory]
    [InlineData(true, "close")]
    [InlineData(false, "close")]
    [InlineData(true, "cancel")]
    [InlineData(false, "cancel")]
    [InlineData(true, "deadline")]
    [InlineData(false, "deadline")]
    public Task BufferedRowAccessRespectsRevokedLifetime(bool import, string cause) => Run(async (connection, token) =>
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var types = new[] { KingbaseDbType.Integer, KingbaseDbType.Text };
        using var input = import ? await connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", types, lifetime.Token) : null;
        using var output = import ? null : await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", types, lifetime.Token);
        if (cause == "close") connection.Close(); else if (cause == "cancel") lifetime.Cancel(); else await Task.Delay(1200, token);
        Func<Task> operation = import ? () => input!.WriteRowAsync([42, "cached"], token) : async () => { await output!.ReadRowAsync(token); };
        if (cause == "deadline") await Assert.ThrowsAsync<TimeoutException>(operation); else await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);
    }, async (stream, token) =>
    {
        await ReadQueryAsync(stream, token); await stream.WriteAsync(import ? Response(true, true) : Join(Response(false, true), Frame('d', BinaryHeader())), token);
        if (import) Assert.Equal('d', (await Frontend(stream, token)).Code);
    }, commandTimeout: cause == "deadline" ? 1 : 30);

    [Fact]
    public async Task CallbackReentryRejectsButAcknowledgedFailureRetainsCountAndConnection()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("typed COPY callback");
        await Run(async (connection, token) =>
        {
            using var input = await connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", new[] { KingbaseDbType.Integer, KingbaseDbType.Text }, token);
            connection.Notice += (_, _) =>
            {
                Assert.Throws<InvalidOperationException>(() => input.WriteRow(42, "reentry"));
                Assert.Throws<InvalidOperationException>(() => input.Complete()); Assert.Throws<InvalidOperationException>(() => input.Cancel());
                Assert.Throws<InvalidOperationException>(() => input.Dispose()); observed.SetResult(); throw failure;
            };
            release.SetResult(); await observed.Task.WaitAsync(token); input.WriteRow(42, "ok");
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => input.CompleteAsync(token)));
            Assert.Equal(1UL, input.RowsCopied); Assert.Equal(ConnectionState.Open, connection.State);
            using var next = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await next.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token); await release.Task.WaitAsync(token);
            await stream.WriteAsync(NoticeTests.Notice(), token); await ReadImport(stream, token); await stream.WriteAsync(Tail(1), token);
            await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    sealed class RowGateStream(Stream inner) : Stream
    {
        internal TaskCompletionSource Dispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            await inner.WriteAsync(buffer, offset, count, token);
            if (count == 7 && buffer[offset] == (byte)'d') { Dispatched.TrySetResult(); await Release.Task.WaitAsync(token); }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => inner.ReadAsync(buffer, offset, count, token);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override bool CanRead => inner.CanRead; public override bool CanWrite => inner.CanWrite; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
