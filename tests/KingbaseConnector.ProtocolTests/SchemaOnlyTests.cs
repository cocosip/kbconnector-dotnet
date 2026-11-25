using System.Data;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.PublicPreparedTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class SchemaOnlyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PublicSchemaOnlyDescribesTypesWithoutSendingValuesOrExecuteAndRetainsReaderOwnership(bool sync, bool close)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
            await Initialize(stream, guard.Token);
            var frames = await ReadFrames(stream, "PDS", guard.Token);
            Assert.Equal(Frame('P', Join(Encoding.UTF8.GetBytes("\0SELECT $1\0"), [0, 1], Int32(-1))), frames[0]);
            Assert.Equal(Frame('D', [(byte)'S', 0]), frames[1]);
            await stream.WriteAsync(Join(Frame('1'), Parameters(uint.MaxValue), Description(1, uint.MaxValue, 4), Ready()), guard.Token);
            if (!close) { await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token); }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT @p", connection);
        command.Parameters.Add(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = KingbaseDbType.Integer });
        var behavior = CommandBehavior.SchemaOnly | CommandBehavior.SingleRow | (close ? CommandBehavior.CloseConnection : 0);
        using var reader = sync ? command.ExecuteReader(behavior) : await command.ExecuteReaderAsync(behavior, guard.Token);
        Assert.Equal(1, reader.FieldCount); Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.Equal(-1, reader.RecordsAffected); Assert.False(reader.HasRows); Assert.False(reader.Read());
        Assert.Single(reader.GetColumnSchema());
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteScalarAsync(guard.Token));
        reader.Close();
        Assert.Equal(close ? ConnectionState.Closed : ConnectionState.Open, connection.State);
        if (!close) { command.Parameters[0].Value = 42; Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token)); }
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task BatchSchemaOnlyReturnsAllDescriptionsWithoutExecutionCountsOrStatuses()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await ReadFrames(stream, "PDS", guard.Token);
            await stream.WriteAsync(Join(Frame('1'), Parameters(), Description(1, uint.MaxValue, 4), Ready()), guard.Token);
            await ReadFrames(stream, "PDS", guard.Token);
            await stream.WriteAsync(Join(Frame('1'), Parameters(), Frame('n'), Ready()), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var batch = connection.CreateBatch(); batch.BatchCommands.Add(new("SELECT 1")); batch.BatchCommands.Add(new("DELETE FROM t"));
        using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SchemaOnly, guard.Token);
        Assert.Equal(1, reader.FieldCount); Assert.False(reader.Read()); Assert.True(reader.NextResult());
        Assert.Equal(0, reader.FieldCount); Assert.False(reader.Read()); Assert.False(reader.NextResult());
        Assert.Equal(-1, reader.RecordsAffected); Assert.Null(batch.FailedCommandIndex);
        Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), item => { Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, item.ExecutionStatus); Assert.Equal(-1, item.RecordsAffected); });
        reader.Close(); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task SynchronizedDescribeErrorLeavesConnectionUsable()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await ReadFrames(stream, "PDS", guard.Token);
            await stream.WriteAsync(Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C42703\0Mprivate-error\0\0")), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT missing", connection);
        var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, guard.Token));
        Assert.Equal("42703", error.SqlState); Assert.DoesNotContain("private-error", error.Message);
        command.CommandText = "SELECT 42"; Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    internal static byte[] Parameters(params uint[] oids) => Frame('t', Join([(byte)(oids.Length >> 8), (byte)oids.Length],
        Join(oids.Select(oid => Int32(unchecked((int)oid))).ToArray())));

    public static TheoryData<byte[]> InvalidDescriptions => new()
    {
        Parameters(), Description(1), Frame('n'), Ready(), Frame('2'), Frame('1', [0]),
        Join(Frame('1'), Parameters()),
        Join(Frame('1'), Parameters(uint.MaxValue - 1)),
        Join(Frame('1'), Frame('t', [0])),
        Join(Frame('1'), Frame('t', [0, 1])),
        Join(Frame('1'), Frame('t', [0, 1, 255, 255, 255, 255, 0])),
        Join(Frame('1'), Parameters(uint.MaxValue), Ready()),
        Join(Frame('1'), Parameters(uint.MaxValue), Parameters(uint.MaxValue)),
        Join(Frame('1'), Parameters(uint.MaxValue), Frame('n', [0])),
        Join(Frame('1'), Parameters(uint.MaxValue), Frame('n'), Row(["42"])),
        Join(Frame('1'), Parameters(uint.MaxValue), Frame('n'), Complete()),
        Join(Frame('1'), Parameters(uint.MaxValue), Frame('n'), Frame('I')),
        Join(Frame('1'), Parameters(uint.MaxValue), Frame('n'), Frame('n')),
        Join(Frame('1'), Parameters(uint.MaxValue), Description(1, uint.MaxValue, 4), Description(1)),
        Join(Frame('1'), Parameters(uint.MaxValue), Description(1, uint.MaxValue, 4), Row(["42"]))
    };

    [Theory]
    [MemberData(nameof(InvalidDescriptions))]
    public async Task MalformedDescriptionOrderingOrTypeIdentityRetiresTheSocket(byte[] response)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await ReadFrames(stream, "PDS", guard.Token); await stream.WriteAsync(Join(response, Ready()), guard.Token);
            await AssertRetired(stream, guard.Token);
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT @p", connection);
        command.Parameters.AddWithValue("p", 42);
        await Assert.ThrowsAsync<IOException>(() => command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, guard.Token));
        Assert.Equal(ConnectionState.Broken, connection.State); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("close")]
    public async Task InterruptedDescriptionRetiresItsPhysicalSession(string reason)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await ReadFrames(stream, "PDS", guard.Token); await stream.WriteAsync(Join(Frame('1'), Parameters()), guard.Token); reached.SetResult();
            await AssertRetired(stream, guard.Token);
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT 42", connection) { CommandTimeout = reason == "timeout" ? 1 : 0 };
        var operation = command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, caller.Token); await reached.Task.WaitAsync(guard.Token);
        if (reason == "caller") caller.Cancel(); else if (reason == "cancel") command.Cancel(); else if (reason == "close") connection.Close();
        if (reason == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => operation);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(reason == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DescriptionsPreserveManualPreparationAndDoNotCountTowardAutomaticThresholds(bool automatic)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Settings(listener); settings.MaxAutoPrepare = automatic ? 1 : 0; settings.AutoPrepareMinUsages = 2;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            if (!automatic) await ExpectPrepare(stream, "kbp_1", "SELECT 42", guard.Token);
            for (var i = 0; i < 2; i++)
            {
                await ReadFrames(stream, "PDS", guard.Token);
                await stream.WriteAsync(Join(Frame('1'), Parameters(), Description(1, uint.MaxValue, 4), Ready()), guard.Token);
            }
            if (automatic)
            {
                await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token);
                await ExpectPrepare(stream, "kbp_1", "SELECT 42", guard.Token);
            }
            await ReadFrames(stream, "BDES", guard.Token); await stream.WriteAsync(PreparedValue("42"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT 42", connection);
        if (!automatic) await command.PrepareAsync(guard.Token);
        for (var i = 0; i < 2; i++) { using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, guard.Token); Assert.False(reader.Read()); }
        if (automatic) Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task DescribingCommitDoesNotCompleteTheLocalTransactionOwner()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await SavepointExecutionTests.Limit(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(TransactionExecutionTests.Result("BEGIN", 'T'), guard.Token);
            await ReadFrames(stream, "PDS", guard.Token);
            await stream.WriteAsync(Join(Frame('1'), Parameters(), Frame('n'), Frame('Z', [(byte)'T'])), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(TransactionExecutionTests.Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var transaction = await connection.BeginTransactionAsync(guard.Token);
        using var command = new KingbaseCommand("COMMIT", connection) { Transaction = transaction };
        using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, guard.Token)) Assert.Equal(0, reader.FieldCount);
        Assert.Same(connection, transaction.Connection); await transaction.RollbackAsync(guard.Token);
        Assert.Null(transaction.Connection); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public void DescribeEnvelopeParticipatesInTheEncodedByteAndParameterLimits()
    {
        var frames = PgExtendedQuery.EncodeDescribe(new string('x', 1024 * 1024 - 21), []);
        Assert.Equal(1024 * 1024, frames.Sum(frame => frame.Length));
        Assert.Equal("PDS", new string(frames.Select(frame => (char)frame[0]).ToArray()));
        Assert.Throws<ArgumentException>(() => PgExtendedQuery.EncodeDescribe(new string('x', 1024 * 1024 - 20), []));
        Assert.Throws<ArgumentException>(() => PgExtendedQuery.EncodeDescribe("SELECT 1", new uint[1025]));
    }

    static async Task AssertRetired(Stream stream, CancellationToken token)
    {
        // Retiring a socket with unread response bytes can produce either EOF or TCP reset.
        try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
        catch (IOException) { }
    }

    [Fact]
    public async Task DescribingAnotherInputTypeDoesNotDetachTheOriginalNamedHandle()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await ExpectPrepare(stream, "kbp_1", "SELECT $1", guard.Token, uint.MaxValue);
            await ReadFrames(stream, "PDS", guard.Token);
            await stream.WriteAsync(Join(Frame('1'), Parameters(9004), Description(1, 9004, -1), Ready()), guard.Token);
            await ReadFrames(stream, "BDES", guard.Token); await stream.WriteAsync(PreparedValue("42"), guard.Token);
            await ExpectClose(stream, "kbp_1", guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT @p", connection);
        command.Parameters.AddWithValue("p", 42); await command.PrepareAsync(guard.Token);
        command.Parameters[0].Value = "other type";
        using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, guard.Token)) Assert.Equal(typeof(string), reader.GetFieldType(0));
        command.Parameters[0].Value = 42; Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        await command.UnprepareAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("server")]
    [InlineData("codec")]
    [InlineData("cancel")]
    public async Task FailedBatchDescriptionsKeepAllStatementsNotExecutedAndRetainFailedIndex(string failure)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await ReadFrames(stream, "PDS", guard.Token);
            await stream.WriteAsync(Join(Frame('1'), Parameters(), Description(1, uint.MaxValue, 4), Ready()), guard.Token);
            await ReadFrames(stream, "PDS", guard.Token);
            if (failure == "server")
                await stream.WriteAsync(Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C42703\0Mprivate-error\0\0")), Ready()), guard.Token);
            else if (failure == "codec")
                await stream.WriteAsync(Join(Frame('1'), Parameters(), Description(1, 123456, -1), Ready()), guard.Token);
            else reached.SetResult();
            await AssertRetired(stream, guard.Token);
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new("SELECT 1")); batch.BatchCommands.Add(new("SELECT missing")); batch.BatchCommands.Add(new("SELECT 3"));
        var operation = batch.ExecuteReaderAsync(CommandBehavior.SchemaOnly, caller.Token);
        if (failure == "cancel") { await reached.Task.WaitAsync(guard.Token); caller.Cancel(); }
        if (failure == "server")
        {
            var error = await Assert.ThrowsAsync<KingbaseException>(() => operation);
            Assert.Equal("42703", error.SqlState); Assert.Same(batch.BatchCommands[1], error.BatchCommand);
        }
        else if (failure == "codec") await Assert.ThrowsAsync<NotSupportedException>(() => operation);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, batch.FailedCommandIndex);
        Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), item => { Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, item.ExecutionStatus); Assert.Equal(-1, item.RecordsAffected); });
        connection.Close(); await peer.WaitAsync(guard.Token);
    }
}
