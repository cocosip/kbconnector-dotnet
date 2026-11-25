using System.Data;
using System.Data.Common;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class PublicPreparedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitPrepareReusesNamedStatementAcrossValueChanges(bool sync)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ExpectPrepare(stream, "kbp_1", "SELECT $1", guard.Token, uint.MaxValue);
            foreach (var value in new[] { "42", "43" })
            {
                var frames = await ReadFrames(stream, "BDES", guard.Token);
                Assert.Equal(Frame('B', Join(Encoding.UTF8.GetBytes("\0kbp_1\0"), [0, 0, 0, 1], Int32(2), Encoding.UTF8.GetBytes(value), [0, 0])), frames[0]);
                await stream.WriteAsync(PreparedValue(value), guard.Token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using DbCommand command = new KingbaseCommand("SELECT :p", connection);
        command.Parameters.Add(new KingbaseParameter("p", 42));
        if (sync) command.Prepare(); else await command.PrepareAsync(guard.Token);
        // A second Prepare must use the same physical cache entry without another Parse.
        await command.PrepareAsync(guard.Token);
        Assert.Equal(42, sync ? command.ExecuteScalar() : await command.ExecuteScalarAsync(guard.Token));
        command.Parameters[0].Value = 43;
        Assert.Equal(43, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedUnprepareClosesOnceAndOtherCommandDetaches(bool sync)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ExpectPrepare(stream, "kbp_1", "SELECT 42", guard.Token);
            await ExpectClose(stream, "kbp_1", guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token);
            await ExpectPrepare(stream, "kbp_2", "SELECT 42", guard.Token);
            await ReadFrames(stream, "BDES", guard.Token); await stream.WriteAsync(PreparedValue("42"), guard.Token);
            await ExpectClose(stream, "kbp_2", guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var first = new KingbaseCommand("SELECT 42", connection);
        using var second = new KingbaseCommand("SELECT 42", connection);
        await first.PrepareAsync(guard.Token); await second.PrepareAsync(guard.Token);
        if (sync) first.Unprepare(); else await first.UnprepareAsync(guard.Token);
        await first.UnprepareAsync(guard.Token);
        Assert.Equal(42, await second.ExecuteScalarAsync(guard.Token));
        await second.PrepareAsync(guard.Token);
        Assert.Equal(42, await second.ExecuteScalarAsync(guard.Token));
        await second.UnprepareAsync(guard.Token);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("sql")]
    [InlineData("type")]
    [InlineData("lexical")]
    [InlineData("deallocate")]
    [InlineData("command-type")]
    [InlineData("connection")]
    public async Task ChangedSignatureOrOwnerDetachesBeforeBinding(string change)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ExpectPrepare(stream, "kbp_1", "SELECT $1", guard.Token, uint.MaxValue);
            if (change is "lexical" or "deallocate")
            {
                await ReadExtended(stream, guard.Token);
                var response = change == "lexical" ? Join(Status("standard_conforming_strings", "off"), Value("1"))
                    : TransactionExecutionTests.Result("DEALLOCATE ALL", 'I');
                await stream.WriteAsync(response, guard.Token);
            }
            var frames = await ReadExtended(stream, guard.Token);
            Assert.Contains(change == "sql" ? "SELECT $1 + 0\0" : "SELECT $1\0", Encoding.UTF8.GetString(frames[0]));
            if (change == "type") Assert.Equal(Int32(9003), frames[0][^4..]); // synthetic int8
            await stream.WriteAsync(Value("42"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT :p", connection);
        command.Parameters.AddWithValue("p", 42);
        await command.PrepareAsync(guard.Token);
        switch (change)
        {
            case "sql": command.CommandText = "SELECT :p + 0"; break;
            case "type": command.Parameters[0].Value = 42L; break;
            case "command-type": command.CommandType = CommandType.StoredProcedure; command.CommandType = CommandType.Text; break;
            case "connection": command.Connection = null; command.Connection = connection; break;
            default:
                using (var other = new KingbaseCommand(change == "lexical" ? "SET standard_conforming_strings=off" : "DEALLOCATE ALL", connection))
                    await other.ExecuteNonQueryAsync(guard.Token);
                break;
        }
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ReopenedConnectionNeverClosesOrBindsOldHandle()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(guard.Token);
                using var stream = client.GetStream(); await Initialize(stream, guard.Token);
                if (i == 0) await ExpectPrepare(stream, "kbp_1", "SELECT 42", guard.Token);
                else { await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token); }
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
        }, guard.Token);
        using var command = new KingbaseCommand("SELECT 42", connection);
        await connection.OpenAsync(guard.Token); await command.PrepareAsync(guard.Token); connection.Close();
        await connection.OpenAsync(guard.Token);
        await command.UnprepareAsync(guard.Token);
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ControlsHonorReaderOwnershipAndDisposeLeavesBufferedReaderUsable()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ExpectPrepare(stream, "kbp_1", "SELECT 42", guard.Token);
            await ReadFrames(stream, "BDES", guard.Token); await stream.WriteAsync(PreparedValue("42"), guard.Token);
            await ExpectClose(stream, "kbp_1", guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 42", connection);
        using var other = new KingbaseCommand("SELECT 42", connection);
        await command.PrepareAsync(guard.Token); await other.PrepareAsync(guard.Token);
        using var reader = await command.ExecuteReaderAsync(guard.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.PrepareAsync(guard.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.UnprepareAsync(guard.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.PrepareAsync(guard.Token));
        command.Dispose(); Assert.True(reader.Read()); Assert.Equal(42, reader.GetInt32(0)); reader.Close();
        await other.UnprepareAsync(guard.Token);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => command.UnprepareAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("prepare", "42601")]
    [InlineData("execute", "26000")]
    [InlineData("execute", "0A000")]
    [InlineData("execute", "22012")]
    public async Task SynchronizedErrorsMapSafelyWithoutReplay(string stage, string state)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            if (stage == "execute") { await ExpectPrepare(stream, "kbp_1", "SELECT 42", guard.Token); await ReadFrames(stream, "BDES", guard.Token); }
            else await ReadFrames(stream, "PS", guard.Token);
            await stream.WriteAsync(Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C" + state + "\0Mprivate-error\0\0")), Ready()), guard.Token);
            // The next user request, never an implicit replay, must appear next.
            if (stage == "execute" && state == "22012")
            { await ReadFrames(stream, "BDES", guard.Token); await stream.WriteAsync(PreparedValue("42"), guard.Token); }
            else { await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token); }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 42", connection);
        KingbaseException error;
        if (stage == "prepare") error = await Assert.ThrowsAsync<KingbaseException>(() => command.PrepareAsync(guard.Token));
        else { await command.PrepareAsync(guard.Token); error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(guard.Token)); }
        Assert.Equal(state, error.SqlState); Assert.DoesNotContain("private-error", error.ToString());
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("prepare", "caller")]
    [InlineData("prepare", "timeout")]
    [InlineData("prepare", "cancel")]
    [InlineData("prepare", "dispose")]
    [InlineData("prepare", "close")]
    [InlineData("unprepare", "caller")]
    [InlineData("unprepare", "timeout")]
    [InlineData("execute", "caller")]
    public async Task InterruptedControlsAndPreparedExecutionRetireWithoutReplay(string stage, string reason)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            if (stage != "prepare") await ExpectPrepare(stream, "kbp_1", "SELECT 42", guard.Token);
            await ReadFrames(stream, stage == "prepare" ? "PS" : stage == "unprepare" ? "CS" : "BDES", guard.Token);
            received.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 42", connection) { CommandTimeout = reason == "timeout" ? 1 : 0 };
        if (stage != "prepare") await command.PrepareAsync(guard.Token);
        var pending = stage == "prepare" ? command.PrepareAsync(caller.Token) : stage == "unprepare"
            ? command.UnprepareAsync(caller.Token) : command.ExecuteScalarAsync(caller.Token);
        await received.Task.WaitAsync(guard.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.PrepareAsync(guard.Token));
        Assert.Throws<InvalidOperationException>(() => command.CommandText = "SELECT 43");
        switch (reason)
        {
            case "caller": caller.Cancel(); break;
            case "cancel": command.Cancel(); break;
            case "dispose": command.Dispose(); break;
            case "close": connection.Close(); break;
        }
        if (reason == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => pending);
        else
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            if (reason == "caller") Assert.Equal(caller.Token, error.CancellationToken);
        }
        Assert.Equal(reason == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task LocalControlValidationAndPreCancellationLeaveReadySessionUntouched()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ExpectPrepare(stream, "kbp_1", "SELECT $1", guard.Token, uint.MaxValue);
            await ExpectClose(stream, "kbp_1", guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT :missing", connection);
        await Assert.ThrowsAsync<ArgumentException>(() => command.PrepareAsync(guard.Token));
        command.CommandText = "SELECT :p"; command.Parameters.Add(new KingbaseParameter("p", DBNull.Value));
        await Assert.ThrowsAsync<ArgumentException>(() => command.PrepareAsync(guard.Token));
        command.Parameters[0].KingbaseDbType = KingbaseDbType.Integer;
        command.Parameters[0].Direction = ParameterDirection.Output;
        await Assert.ThrowsAsync<NotSupportedException>(() => command.PrepareAsync(guard.Token));
        command.Parameters[0].Direction = ParameterDirection.Input;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.PrepareAsync(canceled.Token));
        await command.PrepareAsync(guard.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.UnprepareAsync(canceled.Token));
        await command.UnprepareAsync(guard.Token);
        Assert.Equal(ConnectionState.Open, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
        command.Unprepare(); // Closed detached commands need no physical session.
    }

    [Fact]
    public async Task EvictedCommandDetachesBeforeItsNextExecution()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            for (var i = 0; i < 65; i++)
            {
                if (i == 64) await ExpectClose(stream, "kbp_1", guard.Token);
                await ExpectPrepare(stream, "kbp_" + (i + 1), "SELECT " + i, guard.Token);
            }
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("0"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var first = new KingbaseCommand("SELECT 0", connection); await first.PrepareAsync(guard.Token);
        for (var i = 1; i < 65; i++)
        {
            using var other = new KingbaseCommand("SELECT " + i, connection);
            await other.PrepareAsync(guard.Token);
        }
        Assert.Equal(0, await first.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task PoolResetRevokesPublicHandleAndUsesRefreshedOids()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(PoolExecutionTests.Configuration(listener).ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await PoolExecutionTests.InitializePool(stream, guard.Token);
            await ExpectPrepare(stream, "kbp_1", "SELECT $1", guard.Token, uint.MaxValue);
            await PoolExecutionTests.Reset(stream, guard.Token); await PoolExecutionTests.Ping(stream, guard.Token);
            var frames = await ReadExtended(stream, guard.Token);
            Assert.Equal(Int32(12002), frames[0][^4..]);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 12002, 4), Row(["42"]), Complete(), Ready()), guard.Token);
            await PoolExecutionTests.Reset(stream, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var connection = await source.OpenConnectionAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT :p", connection); command.Parameters.AddWithValue("p", 42);
        await command.PrepareAsync(guard.Token); await connection.CloseAsync();
        await connection.OpenAsync(guard.Token);
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        await connection.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task PreparedControlsPreserveFailedTransactionOwnershipUntilRollback()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await SavepointExecutionTests.Limit(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(TransactionExecutionTests.Result("BEGIN", 'T'), guard.Token);
            await ReadFrames(stream, "PS", guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('Z', [(byte)'T'])), guard.Token);
            await ReadFrames(stream, "BDES", guard.Token);
            await stream.WriteAsync(Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C22012\0Mprivate-error\0\0")), Frame('Z', [(byte)'E'])), guard.Token);
            await ReadFrames(stream, "CS", guard.Token); await stream.WriteAsync(Join(Frame('3'), Frame('Z', [(byte)'E'])), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(TransactionExecutionTests.Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 1/0", connection) { Transaction = transaction };
        await command.PrepareAsync(guard.Token);
        Assert.Same(connection, transaction.Connection);
        Assert.Equal("22012", (await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(guard.Token))).SqlState);
        await command.UnprepareAsync(guard.Token);
        Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync(guard.Token); Assert.Null(transaction.Connection);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    internal static async Task ExpectClose(Stream stream, string name, CancellationToken token)
    {
        var frames = await ReadFrames(stream, "CS", token);
        Assert.Equal(Frame('C', Join([(byte)'S'], Encoding.UTF8.GetBytes(name + "\0"))), frames[0]);
        await stream.WriteAsync(Join(Frame('3'), Ready()), token);
    }

    internal static byte[] PreparedValue(string value) => Value(value).Skip(5).ToArray();
    internal static async Task ExpectPrepare(Stream stream, string name, string sql, CancellationToken token, params uint[] oids)
    {
        var frames = await ReadFrames(stream, "PS", token);
        Assert.Equal(Frame('P', Join(Encoding.UTF8.GetBytes(name + "\0" + sql + "\0"),
            [(byte)(oids.Length >> 8), (byte)oids.Length], Join(oids.Select(x => Int32(unchecked((int)x))).ToArray()))), frames[0]);
        await stream.WriteAsync(Join(Frame('1'), Ready()), token);
    }
    internal static async Task<byte[][]> ReadFrames(Stream stream, string codes, CancellationToken token)
    {
        var result = new List<byte[]>();
        foreach (var code in codes)
        {
            var header = new byte[5]; await stream.ReadExactlyAsync(header, token);
            Assert.Equal((byte)code, header[0]);
            var length = (header[1] << 24) | (header[2] << 16) | (header[3] << 8) | header[4];
            Assert.InRange(length, 4, 1024 * 1024);
            var body = new byte[length - 4]; await stream.ReadExactlyAsync(body, token);
            result.Add(Join(header, body));
        }
        return result.ToArray();
    }
}
