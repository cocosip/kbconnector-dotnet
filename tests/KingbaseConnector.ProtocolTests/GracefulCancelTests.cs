using System.Data;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector;
using Xunit;

namespace KingbaseConnector.ProtocolTests;

/// <summary>Graceful cancel: the caller token sends an out-of-band cancel request and drains to ReadyForQuery.</summary>
public sealed class GracefulCancelTests
{
    [Fact]
    public async Task CallerCancellationSendsACancelRequestKeepsTheSessionUsableAndRethrowsTheCallerToken()
    {
        using var listener = PublicExecutionTests.Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(PublicExecutionTests.Settings(listener).ConnectionString);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelPacket = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var main = await listener.AcceptTcpClientAsync(guard.Token);
            using var mainStream = main.GetStream();
            await PublicExecutionTests.Initialize(mainStream, guard.Token);
            await PublicExecutionTests.ReadExtended(mainStream, guard.Token);
            executed.SetResult();
            using var cancel = await listener.AcceptTcpClientAsync(guard.Token);
            using var cancelStream = cancel.GetStream();
            var packet = new byte[16];
            await cancelStream.ReadExactlyAsync(packet, guard.Token);
            cancelPacket.SetResult(packet);
            await mainStream.WriteAsync(Join(ServerError("57014", "ERROR"), Ready('I')), guard.Token);
            // The drained session must accept the next ordinary command.
            await PublicExecutionTests.ReadExtended(mainStream, guard.Token);
            await mainStream.WriteAsync(PublicExecutionTests.Value("7"), guard.Token);
            Assert.Equal(0, await mainStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);

        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT pg_sleep(8)", connection) { CommandTimeout = 0 };
        var running = command.ExecuteScalarAsync(caller.Token);
        await executed.Task.WaitAsync(guard.Token);
        caller.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(caller.Token, canceled.CancellationToken);
        Assert.Equal(ConnectionState.Open, connection.State);
        var packet = await cancelPacket.Task.WaitAsync(guard.Token);
        Assert.Equal(16, packet.Length);
        Assert.Equal(80877102, ReadInt32(packet, 4));
        Assert.Equal(1, ReadInt32(packet, 8));
        Assert.Equal(2, ReadInt32(packet, 12));
        using var after = new KingbaseCommand("SELECT 7", connection);
        Assert.Equal(7, await after.ExecuteScalarAsync(guard.Token));
        Assert.False(listener.Pending());
        connection.Close();
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task CommandCancelStillRetiresPromptlyAndSendsNoCancelRequest()
    {
        using var listener = PublicExecutionTests.Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var connection = new KingbaseConnection(PublicExecutionTests.Settings(listener).ConnectionString);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var main = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = main.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token);
            await PublicExecutionTests.ReadExtended(stream, guard.Token);
            executed.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT pg_sleep(8)", connection) { CommandTimeout = 0 };
        var running = command.ExecuteScalarAsync(guard.Token);
        await executed.Task.WaitAsync(guard.Token);
        command.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(ConnectionState.Broken, connection.State);
        Assert.False(listener.Pending());
        connection.Close();
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task DrainGraceExpiryRetiresWhenTheServerNeverCompletesTheCanceledStatement()
    {
        using var listener = PublicExecutionTests.Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var caller = new CancellationTokenSource();
        var settings = PublicExecutionTests.Settings(listener);
        settings.CleanupTimeout = 1;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var main = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = main.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token);
            await PublicExecutionTests.ReadExtended(stream, guard.Token);
            executed.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT pg_sleep(8)", connection) { CommandTimeout = 0 };
        var running = command.ExecuteScalarAsync(caller.Token);
        await executed.Task.WaitAsync(guard.Token);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        caller.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(caller.Token, canceled.CancellationToken);
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectionState.Broken, connection.State);
        connection.Close();
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task CanceledStatementInsideATransactionReportsFailedStatusAndRollbackRecovers()
    {
        using var listener = PublicExecutionTests.Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(PublicExecutionTests.Settings(listener).ConnectionString);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var main = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = main.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token);
            await SavepointExecutionTests.Limit(stream, guard.Token);
            await PublicExecutionTests.ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Result("BEGIN", 'T'), guard.Token);
            await PublicExecutionTests.ReadExtended(stream, guard.Token);
            executed.SetResult();
            using var cancel = await listener.AcceptTcpClientAsync(guard.Token);
            using var cancelStream = cancel.GetStream();
            var packet = new byte[16];
            await cancelStream.ReadExactlyAsync(packet, guard.Token);
            Assert.Equal(80877102, ReadInt32(packet, 4));
            // A canceled statement inside an explicit transaction leaves the transaction failed.
            await stream.WriteAsync(Join(ServerError("57014", "ERROR"), Ready('E')), guard.Token);
            await PublicExecutionTests.ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT pg_sleep(8)", connection) { CommandTimeout = 0 };
        command.Transaction = (KingbaseTransaction)transaction;
        var running = command.ExecuteScalarAsync(caller.Token);
        await executed.Task.WaitAsync(guard.Token);
        caller.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(caller.Token, canceled.CancellationToken);
        Assert.Equal(ConnectionState.Open, connection.State);
        await transaction.RollbackAsync(guard.Token);
        Assert.Equal(ConnectionState.Open, connection.State);
        connection.Close();
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ACancelThatLosesTheRaceToACompletedStatementRetiresTheSession()
    {
        using var listener = PublicExecutionTests.Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(PublicExecutionTests.Settings(listener).ConnectionString);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var main = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = main.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token);
            await PublicExecutionTests.ReadExtended(stream, guard.Token);
            executed.SetResult();
            using var cancel = await listener.AcceptTcpClientAsync(guard.Token);
            using var cancelStream = cancel.GetStream();
            var packet = new byte[16];
            await cancelStream.ReadExactlyAsync(packet, guard.Token);
            // The statement already completed: the cancel delivery is ambiguous, so the driver must retire.
            await stream.WriteAsync(PublicExecutionTests.Value("7"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT pg_sleep(8)", connection) { CommandTimeout = 0 };
        var running = command.ExecuteScalarAsync(caller.Token);
        await executed.Task.WaitAsync(guard.Token);
        caller.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(caller.Token, canceled.CancellationToken);
        Assert.Equal(ConnectionState.Broken, connection.State);
        connection.Close();
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task APreparationCancelRetiresWithoutSendingACancelRequest()
    {
        using var listener = PublicExecutionTests.Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(PublicExecutionTests.Settings(listener).ConnectionString);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var main = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = main.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token);
            // Parse/Describe/Sync of an explicit preparation: the session is not executing a statement here.
            var buffer = new byte[8192];
            Assert.NotEqual(0, await stream.ReadAsync(buffer, guard.Token));
            sent.SetResult();
            while (await stream.ReadAsync(buffer, guard.Token) != 0) { }
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 1", connection);
        var preparing = command.PrepareAsync(caller.Token);
        await sent.Task.WaitAsync(guard.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        Assert.Equal(ConnectionState.Broken, connection.State);
        Assert.False(listener.Pending());
        connection.Close();
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ARealServerErrorArrivingBeforeTheCancelKeepsItsIdentity()
    {
        using var listener = PublicExecutionTests.Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(PublicExecutionTests.Settings(listener).ConnectionString);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var main = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = main.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token);
            await PublicExecutionTests.ReadExtended(stream, guard.Token);
            executed.SetResult();
            using var cancel = await listener.AcceptTcpClientAsync(guard.Token);
            using var cancelStream = cancel.GetStream();
            await cancelStream.ReadExactlyAsync(new byte[16], guard.Token);
            // A genuine failure that was already decided server-side wins over the caller's later cancel.
            await stream.WriteAsync(Join(ServerError("23505", "ERROR"), Ready('E')), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT pg_sleep(8)", connection) { CommandTimeout = 0 };
        var running = command.ExecuteScalarAsync(caller.Token);
        await executed.Task.WaitAsync(guard.Token);
        caller.Cancel();
        var error = await Assert.ThrowsAsync<KingbaseException>(() => running);
        Assert.Equal("23505", error.SqlState);
        Assert.Equal("23505", (error as KingbaseException)?.SqlState);
        connection.Close();
        await peer.WaitAsync(guard.Token);
    }

    static int ReadInt32(byte[] bytes, int offset) => (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
    static byte[] Frame(char code, byte[] payload)
    {
        var message = new byte[payload.Length + 5];
        message[0] = (byte)code;
        var length = payload.Length + 4;
        message[1] = (byte)(length >> 24); message[2] = (byte)(length >> 16); message[3] = (byte)(length >> 8); message[4] = (byte)length;
        payload.CopyTo(message, 5);
        return message;
    }
    static byte[] Join(params byte[][] parts)
    {
        var length = 0;
        foreach (var part in parts) length += part.Length;
        var joined = new byte[length];
        var offset = 0;
        foreach (var part in parts) { part.CopyTo(joined, offset); offset += part.Length; }
        return joined;
    }
    static byte[] Ready(char status) => Frame('Z', [(byte)status]);
    static byte[] Tag(string tag) => Frame('C', Encoding.ASCII.GetBytes(tag + "\0"));
    static byte[] Result(string tag, char status) => Join(Frame('1', []), Frame('2', []), Frame('n', []), Tag(tag), Ready(status));
    static byte[] ServerError(string sqlState, string severity)
    {
        var payload = new List<byte>();
        void Field(char code, string value)
        {
            payload.Add((byte)code);
            payload.AddRange(Encoding.UTF8.GetBytes(value));
            payload.Add(0);
        }
        Field('S', severity);
        Field('V', severity);
        Field('C', sqlState);
        Field('M', "private-message");
        payload.Add(0);
        return Frame('E', payload.ToArray());
    }
}
