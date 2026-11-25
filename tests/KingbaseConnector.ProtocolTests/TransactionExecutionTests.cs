using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class TransactionExecutionTests
{
    [Theory]
    [InlineData("ROLLBACK TRANSACTION nested_savepoint", "ROLLBACK")]
    [InlineData("COMMIT TRANSACTION nested", "COMMIT")]
    public async Task TextControlWithActiveReadyRetainsTransactionOwnership(string sql, string tag)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, sql, Result(tag, 'T'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        using var command = new KingbaseCommand(sql, connection) { Transaction = transaction };
        await command.ExecuteNonQueryAsync(guard.Token);
        Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync(guard.Token);
        await peer.WaitAsync(guard.Token);
    }

    static async Task Initialize(NetworkStream stream, CancellationToken token)
    {
        await PublicExecutionTests.Initialize(stream, token);
        await SavepointExecutionTests.Limit(stream, token);
    }
    internal static byte[] Result(string tag, char status) => Join(Frame('1'), Frame('2'), Frame('n'),
        Frame('C', Encoding.UTF8.GetBytes(tag + "\0")), Frame('Z', [(byte)status]));
    static byte[] Error(string code, char status) => Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C" + code + "\0Mprivate-error\0\0")),
        Frame('Z', [(byte)status]));
    static async Task Expect(NetworkStream stream, string sql, byte[] response, CancellationToken token)
    {
        var frames = await ReadExtended(stream, token);
        Assert.Equal(Frame('P', Join([0], Encoding.UTF8.GetBytes(sql + "\0"), [0, 0])), frames[0]);
        await stream.WriteAsync(response, token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BclTransactionsConfirmControlTagsAndReleaseOwnership(bool sync, bool commit)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            for (var i = 0; i < 2; i++)
            {
                await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
                await Expect(stream, "UPDATE t SET x=1", Result("UPDATE 1", 'T'), guard.Token);
                var sql = commit ? "COMMIT" : "ROLLBACK";
                await Expect(stream, sql, Result(sql, 'I'), guard.Token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        for (var i = 0; i < 2; i++)
        {
            using var transaction = sync ? connection.BeginTransaction() : await connection.BeginTransactionAsync(guard.Token);
            Assert.Equal(IsolationLevel.ReadCommitted, transaction.IsolationLevel);
            Assert.Same(connection, transaction.Connection);
            using var command = connection.CreateCommand(); command.CommandText = "UPDATE t SET x=1";
            if (i == 0) command.Transaction = transaction; // Null also participates in the session transaction.
            Assert.Equal(1, await command.ExecuteNonQueryAsync(guard.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.BeginTransactionAsync(guard.Token));
            Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase("other"));
            if (sync) { if (commit) transaction.Commit(); else transaction.Rollback(); }
            else { if (commit) await transaction.CommitAsync(guard.Token); else await transaction.RollbackAsync(guard.Token); }
            Assert.Null(transaction.Connection);
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(guard.Token));
            command.Transaction = transaction;
            await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteNonQueryAsync(guard.Token));
        }
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(IsolationLevel.ReadUncommitted)]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.Chaos)]
    public async Task UnsupportedIsolationNeverSendsOrSilentlySubstitutes(IsolationLevel isolation)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await connection.BeginTransactionAsync(isolation, guard.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await connection.BeginTransactionAsync((IsolationLevel)123, guard.Token));
        using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, guard.Token);
        await transaction.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task FailedTransactionCommitReportingRollbackIsNeverReportedAsCommitted()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "UPDATE t SET x=1", Error("23505", 'E'), guard.Token);
            await Expect(stream, "COMMIT", Result("ROLLBACK", 'I'), guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        using var command = connection.CreateCommand(); command.CommandText = "UPDATE t SET x=1";
        var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteNonQueryAsync(guard.Token));
        Assert.Equal("23505", error.SqlState); Assert.Same(connection, transaction.Connection);
        var commitError = await Assert.ThrowsAnyAsync<DbException>(() => transaction.CommitAsync(guard.Token));
        Assert.Equal("KingbaseTransactionException", commitError.GetType().Name);
        Assert.Equal(false, commitError.GetType().GetProperty("OutcomeUnknown")!.GetValue(commitError));
        Assert.DoesNotContain("private", commitError.ToString()); Assert.Null(transaction.Connection);
        using var next = await connection.BeginTransactionAsync(guard.Token);
        await next.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task LostCommitAcknowledgmentReportsUncertaintyAndRetiresWithoutReplay()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            var frames = await ReadExtended(stream, guard.Token);
            Assert.Contains("COMMIT\0", Encoding.UTF8.GetString(frames[0]));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        var error = await Assert.ThrowsAnyAsync<DbException>(() => transaction.CommitAsync(guard.Token));
        Assert.Equal("KingbaseTransactionException", error.GetType().Name);
        Assert.Equal(true, error.GetType().GetProperty("OutcomeUnknown")!.GetValue(error));
        Assert.DoesNotContain("private", error.ToString()); Assert.Null(transaction.Connection);
        Assert.Equal(ConnectionState.Broken, connection.State);
        await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }

    [Fact]
    public async Task PrecancellationLocalValidationAndReaderExclusionPreserveTransaction()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "SELECT 1", Join(Value("1")[..^6], Frame('Z', [(byte)'T'])), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connection.BeginTransactionAsync(canceled.Token));
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.CommitAsync(canceled.Token));
        using var command = connection.CreateCommand(); command.CommandText = "SELECT :missing";
        await Assert.ThrowsAsync<ArgumentException>(() => command.ExecuteScalarAsync(guard.Token));
        command.CommandText = "SELECT 1";
        using var reader = await command.ExecuteReaderAsync(guard.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync(guard.Token));
        Assert.Same(connection, transaction.Connection); reader.Close();
        await transaction.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("COMMIT", "COMMIT", 'I')]
    [InlineData("ROLLBACK", "ROLLBACK", 'I')]
    [InlineData("/* outer /* nested */ */ CoMmIt AND CHAIN", "COMMIT", 'T')]
    [InlineData("\vCOMMIT AND CHAIN", "COMMIT", 'T')]
    [InlineData("-- boundary\nROLLBACK /* comment */ WORK AND CHAIN", "ROLLBACK", 'T')]
    public async Task RawTransactionEndInvalidatesManagedOwnerIncludingChainedTransactions(string sql, string tag, char status)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, sql, Result(tag, status), guard.Token);
            if (status == 'T') await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync(guard.Token); Assert.Null(transaction.Connection);
        if (status == 'T')
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.BeginTransactionAsync(guard.Token));
            command.CommandText = "ROLLBACK"; await command.ExecuteNonQueryAsync(guard.Token);
        }
        using var next = await connection.BeginTransactionAsync(guard.Token);
        transaction.Dispose(); // An old owner cannot roll back or close the new one.
        await next.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalRollsBackOrClosesIfReaderOwnsConnection(bool activeReader)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            if (activeReader) await Expect(stream, "SELECT 1", Join(Value("1")[..^6], Frame('Z', [(byte)'T'])), guard.Token);
            else await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        var transaction = await connection.BeginTransactionAsync(guard.Token);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT 1";
        using var reader = activeReader ? await command.ExecuteReaderAsync(guard.Token) : null;
        await transaction.DisposeAsync(); transaction.Dispose(); Assert.Null(transaction.Connection);
        Assert.Equal(activeReader ? ConnectionState.Closed : ConnectionState.Open, connection.State);
        if (activeReader) Assert.True(reader!.IsClosed);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("BEGIN", 'I')]
    [InlineData("SELECT 1", 'T')]
    public async Task InvalidBeginAcknowledgmentRetiresSession(string tag, char status)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result(tag, status), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        await Assert.ThrowsAsync<IOException>(async () => await connection.BeginTransactionAsync(guard.Token));
        Assert.Equal(ConnectionState.Broken, connection.State); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("commit")]
    [InlineData("rollback")]
    public async Task SynchronizedControlRejectionPreservesOnlyReportedTransactionState(string rejected)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            if (rejected == "begin") await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Error("25001", 'I'), guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            if (rejected != "begin") await Expect(stream, rejected.ToUpperInvariant(), Error("40001", 'E'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        if (rejected == "begin") await Assert.ThrowsAsync<KingbaseException>(async () => await connection.BeginTransactionAsync(guard.Token));
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        if (rejected != "begin")
        {
            var error = await Assert.ThrowsAsync<KingbaseException>(() => rejected == "commit" ? transaction.CommitAsync(guard.Token) : transaction.RollbackAsync(guard.Token));
            Assert.Equal("40001", error.SqlState); Assert.Same(connection, transaction.Connection);
        }
        await transaction.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task CloseReopenAndCrossConnectionCommandsRejectStaleTransactions()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        using var other = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(guard.Token);
                using var stream = client.GetStream(); await Initialize(stream, guard.Token);
                await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
                if (i > 0) await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        var old = await connection.BeginTransactionAsync(guard.Token);
        connection.Close(); Assert.Null(old.Connection);
        await connection.OpenAsync(guard.Token);
        using var current = connection.BeginTransaction();
        using var command = new KingbaseCommand("UPDATE t SET x=1", connection) { Transaction = old };
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteNonQueryAsync(guard.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.CommitAsync(guard.Token));
        old.Dispose(); Assert.Same(connection, current.Connection);
        await current.RollbackAsync(guard.Token); connection.Close();
        await other.OpenAsync(guard.Token);
        using var foreign = await other.BeginTransactionAsync(guard.Token);
        command.Connection = other; command.Transaction = current;
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteNonQueryAsync(guard.Token));
        await foreign.RollbackAsync(guard.Token); other.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("ROLLBACK TO SAVEPOINT s")]
    [InlineData("ROLLBACK WORK /* gap */ TO s")]
    [InlineData("ROLLBACK TRANSACTION TO SAVEPOINT s")]
    [InlineData("ROLLBACK\vTO SAVEPOINT s")]
    public async Task RawRollbackToPreservesManagedTransaction(string sql)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, sql, Result("ROLLBACK", 'T'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        using var command = new KingbaseCommand(sql, connection) { Transaction = transaction };
        await command.ExecuteNonQueryAsync(guard.Token); Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task CommitCancellationAfterSendingPreservesCancellationAndMarksUncertainOutcome()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            var frames = await ReadExtended(stream, guard.Token);
            Assert.Contains("COMMIT\0", Encoding.UTF8.GetString(frames[0])); received.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        var commit = transaction.CommitAsync(caller.Token);
        await received.Task.WaitAsync(guard.Token); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit);
        Assert.Equal(caller.Token, error.CancellationToken); Assert.True(transaction.OutcomeUnknown);
        Assert.Null(transaction.Connection); Assert.Equal(ConnectionState.Broken, connection.State);
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    public async Task CloseAfterCommitAcknowledgmentDoesNotEraseKnownOutcome(string tag)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await ReadExtended(stream, guard.Token); await send.Task.WaitAsync(guard.Token);
            await stream.WriteAsync(Result(tag, 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        var sync = typeof(KingbaseConnection).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        var physical = (KingbaseConnector.Protocol.PgPhysicalSession)typeof(KingbaseConnection)
            .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        var queries = physical.Queries;
        var busy = typeof(KingbaseConnector.Protocol.PgQuerySession).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var commit = transaction.CommitAsync(guard.Token);
        await Task.Run(() =>
        {
            lock (sync)
            {
                send.SetResult();
                Assert.True(SpinWait.SpinUntil(() => queries.TransactionStatus == KingbaseConnector.Protocol.PgTransactionStatus.Idle &&
                    (int)busy.GetValue(queries)! == 0, 5000));
                connection.Close();
            }
        }, guard.Token);
        if (tag == "COMMIT") await commit;
        else
        {
            var error = await Assert.ThrowsAsync<KingbaseTransactionException>(() => commit);
            Assert.False(error.OutcomeUnknown);
        }
        Assert.False(transaction.OutcomeUnknown); Assert.Null(transaction.Connection);
        Assert.Equal(ConnectionState.Closed, connection.State); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("23505", "ERROR", false)]
    [InlineData("40001", "ERROR", false)]
    [InlineData("40003", "ERROR", true)]
    [InlineData("08007", "ERROR", true)]
    [InlineData("57P01", "FATAL", true)]
    public async Task CommitErrorBeforeEofPreservesOnlyProvenRejections(string sqlState, string severity, bool unknown)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Frame('E', Encoding.UTF8.GetBytes("S" + severity + "\0C" + sqlState + "\0Mprivate-error\0\0")), guard.Token);
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        var error = await Assert.ThrowsAnyAsync<DbException>(() => transaction.CommitAsync(guard.Token));
        if (unknown) Assert.True(Assert.IsType<KingbaseTransactionException>(error).OutcomeUnknown);
        else Assert.Equal(sqlState, Assert.IsType<KingbaseException>(error).SqlState);
        Assert.Equal(unknown, transaction.OutcomeUnknown); Assert.Null(transaction.Connection);
        Assert.DoesNotContain("private", error.ToString()); Assert.Equal(ConnectionState.Broken, connection.State);
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task StatementCompletionUnknownRemainsUnknownEvenWithReady()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "COMMIT", Error("40003", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        var error = await Assert.ThrowsAsync<KingbaseTransactionException>(() => transaction.CommitAsync(guard.Token));
        Assert.True(error.OutcomeUnknown); Assert.True(transaction.OutcomeUnknown);
        Assert.Null(transaction.Connection); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDisposalCleanupClosesTheOwnedSessionEvenWithUnlimitedApplicationTimeout(bool silent)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var settings = Settings(listener); settings.CommandTimeout = 0;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            var frames = await ReadExtended(stream, guard.Token);
            Assert.Contains("ROLLBACK\0", Encoding.UTF8.GetString(frames[0]));
            if (!silent) await stream.WriteAsync(Error("40001", 'E'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        var transaction = await connection.BeginTransactionAsync(guard.Token);
        await transaction.DisposeAsync().AsTask().WaitAsync(guard.Token);
        Assert.Null(transaction.Connection);
        Assert.Equal(silent ? ConnectionState.Broken : ConnectionState.Closed, connection.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.CommitAsync(guard.Token));
        await peer.WaitAsync(guard.Token);
    }
}
