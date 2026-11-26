using System.Buffers.Binary;
using System.Data;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.CopyExecutionTests;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class StreamingInputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonseekableBinaryAndFragmentedTextBindBeyondTheScalarLimit(bool sequential)
    {
        var binary = Enumerable.Range(0, 700013).Select(i => (byte)(i % 251)).ToArray();
        var text = "\uFEFF" + new string('界', 360000) + "😀";
        using var input = new FragmentedStream(binary); using var reader = new FragmentedReader(text);
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT :b, :t", connection);
            command.Parameters.Add(new KingbaseParameter("b", input) { DbType = DbType.Binary, Size = binary.Length });
            command.Parameters.Add(new KingbaseParameter("t", reader) { DbType = DbType.String, Size = text.Length });
            using var result = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default, token);
            Assert.True(await result.ReadAsync(token)); Assert.Equal(42, result.GetInt32(0)); Assert.False(await result.ReadAsync(token));
            Assert.False(input.WasDisposed); Assert.False(reader.WasDisposed);
            Assert.InRange(input.MaxRequest, 1, 16384); Assert.InRange(reader.MaxRequest, 1, 16384);
        }, async (stream, token) =>
        {
            var frames = await ReadFrames(stream, "PBDES", token); var values = BindValues(frames[1]);
            Assert.Equal("\\x" + Convert.ToHexString(binary).ToLowerInvariant(), Encoding.UTF8.GetString(values[0]!));
            Assert.Equal(text, new UTF8Encoding(false, true).GetString(values[1]!)); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Fact]
    public async Task PrepareAndSchemaOnlyDoNotReadInputAndPreparedExecutionConsumesItOnce()
    {
        using var input = new FragmentedReader("文字😀");
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT :p", connection); command.Parameters.Add(new KingbaseParameter("p", input) { DbType = DbType.String });
            await command.PrepareAsync(token); Assert.Equal(0, input.ReadCalls);
            using (var schema = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, token)) { Assert.Equal(0, input.ReadCalls); }
            Assert.Equal(42, await command.ExecuteScalarAsync(token)); Assert.Equal(4, input.UnitsRead); Assert.False(input.WasDisposed);
        }, async (stream, token) =>
        {
            await ReadFrames(stream, "PS", token); await stream.WriteAsync(Join(Frame('1'), Ready()), token);
            await ReadFrames(stream, "PDS", token); await stream.WriteAsync(Join(Frame('1'), Frame('t', [0, 1, 0, 0, 35, 44]), Description(1, 9004), Ready()), token);
            var frames = await ReadFrames(stream, "BDES", token); Assert.Equal("文字😀", Encoding.UTF8.GetString(BindValues(frames[0])[0]!));
            await stream.WriteAsync(Join(Frame('2'), Description(1, uint.MaxValue, 4), Row(["42"]), Complete(), Ready()), token);
        });
    }

    [Theory]
    [InlineData("nul")]
    [InlineData("surrogate")]
    [InlineData("size")]
    [InlineData("binary-size")]
    public async Task InvalidInputFailsBeforeSqlAndLeavesTheSessionUsable(string reason)
    {
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT :p", connection);
            using var input = new FragmentedReader(reason == "nul" ? "bad\0text" : reason == "surrogate" ? "bad\uD800" : "😀");
            using var binary = new FragmentedStream([1, 2]);
            command.Parameters.Add(new KingbaseParameter("p", reason == "binary-size" ? binary : input)
            { DbType = reason == "binary-size" ? DbType.Binary : DbType.String, Size = reason is "size" or "binary-size" ? 1 : 0 });
            if (reason is "size" or "binary-size") await Assert.ThrowsAsync<ArgumentException>(() => command.ExecuteScalarAsync(token));
            else await Assert.ThrowsAsync<EncoderFallbackException>(() => command.ExecuteScalarAsync(token));
            Assert.Equal(ConnectionState.Open, connection.State); Assert.False(input.WasDisposed); Assert.False(binary.WasDisposed);
            using var next = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await next.ExecuteScalarAsync(token));
        }, async (stream, token) => { var frames = await ReadFrames(stream, "PBDES", token); Assert.Equal("SELECT 42\0", Encoding.UTF8.GetString(frames[0], 6, frames[0].Length - 8)); await stream.WriteAsync(Value("42"), token); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidationOfOtherParametersPrecedesAnyInputRead(bool duplicate)
    {
        using var input = new FragmentedReader("untouched");
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT :p, :q", connection);
            command.Parameters.Add(new KingbaseParameter("p", input) { DbType = DbType.String });
            command.Parameters.Add(new KingbaseParameter("q", duplicate ? input : "bad") { DbType = duplicate ? DbType.String : DbType.Int32 });
            await Assert.ThrowsAnyAsync<Exception>(() => command.ExecuteScalarAsync(token)); Assert.Equal(0, input.ReadCalls);
            Assert.Equal(ConnectionState.Open, connection.State);
        }, (_, _) => Task.CompletedTask);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("\uFEFF文字😀", "\uFEFF文字😀")]
    public async Task TextEncoderCarriesSplitSurrogatesAndPreservesEmptyAndBom(string input, string expected)
    {
        await Run(async (connection, token) =>
        {
            using var text = new FragmentedReader(input, 1); using var binary = new MemoryStream([0, 255]); binary.Position = 1;
            using var command = new KingbaseCommand("SELECT :t, :b, :n", connection);
            command.Parameters.Add(new KingbaseParameter("t", text) { DbType = DbType.String });
            command.Parameters.Add(new KingbaseParameter("b", binary) { DbType = DbType.Binary });
            command.Parameters.Add(new KingbaseParameter("n", DBNull.Value) { DbType = DbType.String });
            Assert.Equal(42, await command.ExecuteScalarAsync(token)); Assert.Equal(2, binary.Position); Assert.True(binary.CanRead);
        }, async (stream, token) =>
        {
            var frames = await ReadFrames(stream, "PBDES", token); var values = BindValues(frames[1]); Assert.Equal(expected, Encoding.UTF8.GetString(values[0]!));
            Assert.Equal("\\xff", Encoding.UTF8.GetString(values[1]!)); Assert.Null(values[2]); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Theory]
    [InlineData("caller", false)]
    [InlineData("cancel", false)]
    [InlineData("close", false)]
    [InlineData("timeout", false)]
    [InlineData("caller", true)]
    [InlineData("cancel", true)]
    [InlineData("close", true)]
    [InlineData("timeout", true)]
    public async Task StubbornCallerReadCannotHoldCancellationOrWriteIntoTheNextExecution(string termination, bool sequential)
    {
        using var input = new ControlledReader();
        await Run(async (connection, token) =>
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var command = new KingbaseCommand("SELECT :p", connection); command.Parameters.Add(new KingbaseParameter("p", input) { DbType = DbType.String });
            var work = command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default, lifetime.Token);
            await input.Started.Task.WaitAsync(token);
            if (termination == "caller") lifetime.Cancel(); else if (termination == "cancel") command.Cancel(); else if (termination == "close") connection.Close();
            if (termination == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => work.WaitAsync(token));
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(token));
            Assert.False(input.WasDisposed); Assert.Equal(termination == "close" ? ConnectionState.Closed : ConnectionState.Open, connection.State);
            if (termination != "close")
            {
                using var next = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await next.ExecuteScalarAsync(token));
            }
            input.Complete(); await input.Finished.Task.WaitAsync(token);
        }, async (stream, token) =>
        {
            if (termination != "close") { await ReadFrames(stream, "PBDES", token); await stream.WriteAsync(Value("42"), token); }
        }, commandTimeout: termination == "timeout" ? 1 : 0);
    }

    [Fact]
    public async Task StreamingRequestOwnsItsTemporaryBodyAndClosesItAfterSend()
    {
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql);
        using var input = new FragmentedReader("body");
        var parameter = new PgQueryParameter(catalog.ByName["pg_catalog.text"].Oid, PgParameterInput.Create(input, "text", 0));
        using var request = await PgExtendedRequest.CreateAsync("SELECT $1", catalog, [parameter], CancellationToken.None);
        var path = request.TemporaryPath; Assert.NotNull(path); Assert.True(File.Exists(path));
        Assert.Throws<IOException>(() => File.OpenRead(path));
        using var output = new MemoryStream(); using var wire = new PgWire(output); long attempts = 0;
        await request.SendAsync(wire, count => attempts += count, CancellationToken.None);
        Assert.Equal(1, attempts); Assert.False(File.Exists(path)); Assert.False(input.WasDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => request.SendAsync(wire, count => attempts += count, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedAdapterRowsRejectStreamingInputsBeforeReading(bool binary)
    {
        using var text = new FragmentedReader("data"); using var bytes = new FragmentedStream([1, 2]);
        await Run((connection, _) =>
        {
            using var table = new DataTable(); table.Columns.Add("v", typeof(object)); table.Rows.Add(binary ? bytes : text); table.Rows.Add(binary ? bytes : text);
            using var command = new KingbaseCommand("INSERT INTO items VALUES(:p)", connection) { UpdatedRowSource = UpdateRowSource.None };
            command.Parameters.Add(new KingbaseParameter("p", null) { DbType = binary ? DbType.Binary : DbType.String, SourceColumn = "v" });
            using var adapter = new KingbaseDataAdapter { InsertCommand = command, UpdateBatchSize = 0 };
            Assert.Throws<NotSupportedException>(() => adapter.Update(table)); Assert.Equal(0, text.ReadCalls); Assert.Equal(0, bytes.ReadCalls);
            return Task.CompletedTask;
        }, async (stream, token) =>
        {
            try
            {
                for (var i = 0; i < 2; i++) { await ReadFrames(stream, "PBDES", token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('n'), Frame('C', Encoding.ASCII.GetBytes("INSERT 0 1\0")), Ready()), token); }
            }
            catch (EndOfStreamException) { }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLaterBatchInputPreservesAcknowledgmentAndNeverReadsUnsentInput(bool sequential)
    {
        using var first = new FragmentedReader("first"); using var invalid = new FragmentedReader("\0"); using var last = new FragmentedReader("untouched");
        await Run(async (connection, token) =>
        {
            using var batch = new KingbaseBatch(connection);
            foreach (var input in new[] { first, invalid, last })
            { var item = new KingbaseBatchCommand("SELECT :p"); item.Parameters.Add(new KingbaseParameter("p", input) { DbType = DbType.String }); batch.BatchCommands.Add(item); }
            if (sequential)
            {
                using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(await reader.ReadAsync(token)); Assert.Equal(42, reader.GetInt32(0));
                Assert.False(await reader.ReadAsync(token)); await Assert.ThrowsAsync<EncoderFallbackException>(() => reader.NextResultAsync(token));
            }
            else await Assert.ThrowsAsync<EncoderFallbackException>(() => batch.ExecuteNonQueryAsync(token));
            Assert.Equal(1, batch.FailedCommandIndex); Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
            Assert.Equal(KingbaseBatchCommandStatus.Failed, batch.BatchCommands[1].ExecutionStatus); Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[2].ExecutionStatus);
            Assert.Equal(0, last.ReadCalls); Assert.Equal(ConnectionState.Open, connection.State);
            using var next = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await next.ExecuteScalarAsync(token));
        }, async (stream, token) => { await ReadFrames(stream, "PBDES", token); await stream.WriteAsync(Value("42"), token); await ReadFrames(stream, "PBDES", token); await stream.WriteAsync(Value("42"), token); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticPreparationStagesFreshInputsOnceAndDoesNotReplayServerErrors(bool sequential)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var token = guard.Token;
        var settings = Settings(listener); settings.MaxAutoPrepare = 1; settings.AutoPrepareMinUsages = 1;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(token); using var stream = client.GetStream(); await Initialize(stream, token);
            await ReadFrames(stream, "PS", token); await stream.WriteAsync(Join(Frame('1'), Ready()), token);
            for (var i = 0; i < 2; i++)
            {
                var frames = await ReadFrames(stream, "BDES", token); Assert.Equal("input" + i, Encoding.UTF8.GetString(BindValues(frames[0])[0]!));
                await stream.WriteAsync(i == 0 ? Join(Frame('2'), Description(1, uint.MaxValue, 4), Row(["42"]), Complete(), Ready()) : Error("22012"), token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        }, token);
        await connection.OpenAsync(token);
        for (var i = 0; i < 2; i++)
        {
            using var input = new FragmentedReader("input" + i); using var command = new KingbaseCommand("SELECT :p", connection);
            command.Parameters.Add(new KingbaseParameter("p", input) { DbType = DbType.String });
            if (i == 0) { using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default, token); Assert.True(await reader.ReadAsync(token)); Assert.False(await reader.ReadAsync(token)); }
            else { Assert.Equal("22012", (await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default, token))).SqlState); }
            Assert.Equal(2, input.ReadCalls); Assert.Equal(6, input.UnitsRead); Assert.False(input.WasDisposed);
        }
        connection.Close(); await peer.WaitAsync(token);
    }

    [Fact]
    public async Task BindFrameLimitIncludesHexExpansionAndNamedStatementOverhead()
    {
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql); var oid = catalog.ByName["pg_catalog.bytea"].Oid;
        using var input = new GeneratedStream((PgWire.MaxMessageLength - 18) / 2);
        using (var request = await PgExtendedRequest.CreateAsync("SELECT $1", catalog, [new PgQueryParameter(oid, PgParameterInput.Create(input, "bytea", 0))], CancellationToken.None))
        {
            var path = request.TemporaryPath; Assert.NotNull(path); Assert.Throws<ArgumentException>(() => request.UsePrepared("x", catalog, []));
            Assert.True(File.Exists(path)); request.Dispose(); Assert.False(File.Exists(path));
        }
        using var overflow = new GeneratedStream((PgWire.MaxMessageLength - 18) / 2 + 1);
        await Assert.ThrowsAsync<ArgumentException>(() => PgExtendedRequest.CreateAsync("SELECT $1", catalog, [new PgQueryParameter(oid, PgParameterInput.Create(overflow, "bytea", 0))], CancellationToken.None));
        Assert.InRange(input.MaxRequest, 1, 16384); Assert.InRange(overflow.MaxRequest, 1, 16384);
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(6, 1)]
    public async Task TransmissionFailureClosesTheSpoolAndRetiresTheWireWithoutReplaying(int failAtWrite, int attemptedExecutions)
    {
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql); using var input = new FragmentedReader("body");
        using var request = await PgExtendedRequest.CreateAsync("SELECT $1", catalog, [new PgQueryParameter(catalog.ByName["pg_catalog.text"].Oid, PgParameterInput.Create(input, "text", 0))], CancellationToken.None);
        var path = request.TemporaryPath; using var output = new FailingOutput(failAtWrite); using var wire = new PgWire(output); long executions = 0;
        await Assert.ThrowsAsync<IOException>(() => request.SendAsync(wire, count => executions += count, CancellationToken.None));
        Assert.True(wire.IsDisposed); Assert.Equal(attemptedExecutions, executions); Assert.False(File.Exists(path)); Assert.False(input.WasDisposed); Assert.Equal(2, input.ReadCalls);
    }

    [Theory]
    [InlineData("untyped")]
    [InlineData("wrong-type")]
    [InlineData("pre-canceled")]
    public async Task UnsupportedOrCanceledInputsRejectBeforeReading(string reason)
    {
        using var input = new FragmentedReader("untouched");
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT :p", connection); var parameter = new KingbaseParameter("p", input);
            if (reason != "untyped") parameter.DbType = reason == "wrong-type" ? DbType.Binary : DbType.String;
            command.Parameters.Add(parameter);
            if (reason == "pre-canceled") { using var canceled = new CancellationTokenSource(); canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteScalarAsync(canceled.Token)); }
            else if (reason == "wrong-type") await Assert.ThrowsAsync<InvalidCastException>(() => command.ExecuteScalarAsync(token));
            else await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteScalarAsync(token));
            Assert.Equal(0, input.ReadCalls); Assert.Equal(ConnectionState.Open, connection.State);
        }, (_, _) => Task.CompletedTask);
    }

    sealed class FailingOutput(int failAt) : MemoryStream
    {
        int _writes;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ++_writes == failAt ? Task.FromException(new IOException("simulated write failure")) : base.WriteAsync(buffer, offset, count, token);
    }

    sealed class GeneratedStream(long remaining) : Stream
    {
        internal int MaxRequest { get; private set; }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { MaxRequest = Math.Max(MaxRequest, count); var read = (int)Math.Min(count, remaining); Array.Clear(buffer, offset, read); remaining -= read; return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static async Task<byte[][]> ReadFrames(Stream stream, string codes, CancellationToken token)
    {
        var frames = new List<byte[]>();
        foreach (var code in codes)
        {
            var header = new byte[5]; await stream.ReadExactlyAsync(header, token); Assert.Equal((byte)code, header[0]);
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)); Assert.InRange(length, 4, 64 * 1024 * 1024);
            var body = new byte[length - 4]; await stream.ReadExactlyAsync(body, token); frames.Add(Join(header, body));
        }
        return frames.ToArray();
    }
    internal static byte[]?[] BindValues(byte[] frame)
    {
        var offset = 5; while (frame[offset++] != 0) { } while (frame[offset++] != 0) { }
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(offset))); offset += 2;
        var count = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(offset)); offset += 2; var values = new byte[]?[count];
        for (var i = 0; i < count; i++)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(offset)); offset += 4;
            if (length < 0) { Assert.Equal(-1, length); continue; } values[i] = frame.AsSpan(offset, length).ToArray(); offset += length;
        }
        Assert.Equal(new byte[2], frame[offset..]); return values;
    }
    internal sealed class FragmentedStream(byte[] data) : Stream
    {
        int _position;
        internal bool WasDisposed { get; private set; }
        internal int MaxRequest { get; private set; }
        internal int ReadCalls { get; private set; }
        public override bool CanRead => !WasDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { ReadCalls++; MaxRequest = Math.Max(MaxRequest, count); var length = Math.Min(Math.Min(count, 8191), data.Length - _position); data.AsSpan(_position, length).CopyTo(buffer.AsSpan(offset)); _position += length; return length; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    internal sealed class FragmentedReader(string text, int fragmentSize = 8191) : TextReader
    {
        internal bool WasDisposed { get; private set; }
        internal int MaxRequest { get; private set; }
        internal int ReadCalls { get; private set; }
        internal int UnitsRead { get; private set; }
        public override Task<int> ReadAsync(char[] buffer, int index, int count)
        { ReadCalls++; MaxRequest = Math.Max(MaxRequest, count); var size = Math.Min(Math.Min(count, fragmentSize), text.Length - UnitsRead); text.AsSpan(UnitsRead, size).CopyTo(buffer.AsSpan(index)); UnitsRead += size; return Task.FromResult(size); }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
    sealed class ControlledReader : TextReader
    {
        readonly TaskCompletionSource<int> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasDisposed { get; private set; }
        public override async Task<int> ReadAsync(char[] buffer, int index, int count)
        { Started.TrySetResult(); var result = await _gate.Task; buffer[index] = 'x'; Finished.TrySetResult(); return result; }
        internal void Complete() => _gate.TrySetResult(1);
        protected override void Dispose(bool disposing) { WasDisposed = true; Complete(); base.Dispose(disposing); }
    }
}
