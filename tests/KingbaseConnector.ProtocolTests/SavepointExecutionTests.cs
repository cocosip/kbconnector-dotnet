using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.TransactionExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class SavepointExecutionTests
{
    internal static byte[] LimitResult(string value = "63", char status = 'I') => Join(Description(1), Row([value]),
        Frame('C', Encoding.ASCII.GetBytes("SHOW\0")), Frame('Z', [(byte)status]));
    internal static async Task Limit(NetworkStream stream, CancellationToken token, byte[]? response = null)
    {
        Assert.Equal("SHOW max_identifier_length", await ReadQueryAsync(stream, token));
        await stream.WriteAsync(response ?? LimitResult(), token);
    }
    static async Task Expect(NetworkStream stream, string sql, byte[] response, CancellationToken token)
    {
        var frames = await ReadExtended(stream, token);
        Assert.Equal(Frame('P', Join([0], Encoding.UTF8.GetBytes(sql + "\0"), [0, 0])), frames[0]);
        await stream.WriteAsync(response, token);
    }
    static byte[] Error(string code, char status) => Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C" + code + "\0Mprivate-error\0\0")),
        Frame('Z', [(byte)status]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BclSavepointsEscapeNamesAndKeepTheTransactionOwned(bool sync)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Limit(stream, guard.Token);
            for (var i = 0; i < 2; i++) // Cached limit must not be queried on the second Begin.
            {
                await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
                await Expect(stream, "SAVEPOINT \"a\"\";--中文\"", Result("SAVEPOINT", 'T'), guard.Token);
                await Expect(stream, "SAVEPOINT \"a\"\";--中文\"", Result("SAVEPOINT", 'T'), guard.Token);
                await Expect(stream, "ROLLBACK TO SAVEPOINT \"a\"\";--中文\"", Result("ROLLBACK", 'T'), guard.Token);
                await Expect(stream, "RELEASE SAVEPOINT \"a\"\";--中文\"", Result("RELEASE", 'T'), guard.Token);
                await Expect(stream, "COMMIT", Result("COMMIT", 'I'), guard.Token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        for (var i = 0; i < 2; i++)
        {
            using var transaction = await connection.BeginTransactionAsync(guard.Token);
            Assert.True(transaction.SupportsSavepoints);
            if (sync)
            {
                transaction.Save("a\";--中文"); transaction.Save("a\";--中文");
                transaction.Rollback("a\";--中文"); transaction.Release("a\";--中文");
            }
            else
            {
                await transaction.SaveAsync("a\";--中文", guard.Token); await transaction.SaveAsync("a\";--中文", guard.Token);
                await transaction.RollbackAsync("a\";--中文", guard.Token); await transaction.ReleaseAsync("a\";--中文", guard.Token);
            }
            Assert.Same(connection, transaction.Connection); await transaction.CommitAsync(guard.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.SaveAsync("old", guard.Token));
        }
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task RawSavepointAndFailedTransactionRecoverWithoutALocalStackOrFurtherMetadataQueries()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Limit(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "SAVEPOINT raw", Result("SAVEPOINT", 'T'), guard.Token);
            await Expect(stream, "UPDATE t SET x=1", Error("23505", 'E'), guard.Token);
            await Expect(stream, "ROLLBACK TO SAVEPOINT \"missing\"", Error("3B001", 'E'), guard.Token);
            await Expect(stream, "ROLLBACK TO SAVEPOINT \"raw\"", Result("ROLLBACK", 'T'), guard.Token);
            await Expect(stream, "RELEASE SAVEPOINT \"raw\"", Result("RELEASE", 'T'), guard.Token);
            await Expect(stream, "COMMIT", Result("COMMIT", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var transaction = await connection.BeginTransactionAsync(guard.Token);
        using var command = connection.CreateCommand(); command.CommandText = "SAVEPOINT raw";
        await command.ExecuteNonQueryAsync(guard.Token); command.CommandText = "UPDATE t SET x=1";
        Assert.Equal("23505", (await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteNonQueryAsync(guard.Token))).SqlState);
        var error = await Assert.ThrowsAsync<KingbaseException>(() => transaction.RollbackAsync("missing", guard.Token));
        Assert.Equal("3B001", error.SqlState); Assert.DoesNotContain("private", error.ToString());
        Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync("raw", guard.Token); await transaction.ReleaseAsync("raw", guard.Token);
        await transaction.CommitAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ValidationUsesServerUtf8BytesAndRejectsBeforeWritingOrChangingTransactionState()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Limit(stream, guard.Token, LimitResult("6"));
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "SAVEPOINT \"中文\"", Result("SAVEPOINT", 'T'), guard.Token);
            await Expect(stream, "SAVEPOINT \"\"\"\"\"\"\"\"\"\"\"\"\"\"", Result("SAVEPOINT", 'T'), guard.Token);
            await Expect(stream, "SELECT 1", Join(Value("1")[..^6], Frame('Z', [(byte)'T'])), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var transaction = await connection.BeginTransactionAsync(guard.Token);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        foreach (var bad in new[] { "", "a\0b", "\ud800", "中文x", "1234567" })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => transaction.SaveAsync(bad, guard.Token));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => transaction.RollbackAsync(bad, guard.Token));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => transaction.ReleaseAsync(bad, guard.Token));
        }
        await Assert.ThrowsAsync<ArgumentNullException>(() => transaction.SaveAsync(null!, guard.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.SaveAsync("ok", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.RollbackAsync("ok", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.ReleaseAsync("ok", canceled.Token));
        await transaction.SaveAsync("中文", guard.Token); await transaction.SaveAsync("\"\"\"\"\"\"", guard.Token);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT 1";
        using var reader = await command.ExecuteReaderAsync(guard.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.SaveAsync("busy", guard.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync("busy", guard.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ReleaseAsync("busy", guard.Token));
        reader.Close(); Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync(guard.Token); transaction.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.SaveAsync("old", guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("save", "SAVEPOINT", 'I')]
    [InlineData("save", "RELEASE", 'T')]
    [InlineData("rollback", "ROLLBACK", 'I')]
    [InlineData("release", "RELEASE", 'E')]
    public async Task MalformedSavepointAcknowledgmentRetiresSession(string operation, string tag, char status)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Limit(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            var sql = operation switch { "save" => "SAVEPOINT", "release" => "RELEASE SAVEPOINT", _ => "ROLLBACK TO SAVEPOINT" };
            await Expect(stream, sql + " \"s\"", Result(tag, status), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var transaction = await connection.BeginTransactionAsync(guard.Token);
        await Assert.ThrowsAsync<IOException>(() => operation switch
        {
            "save" => transaction.SaveAsync("s", guard.Token), "release" => transaction.ReleaseAsync("s", guard.Token),
            _ => transaction.RollbackAsync("s", guard.Token)
        });
        Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(transaction.Connection);
        Assert.False(((KingbaseTransaction)transaction).OutcomeUnknown); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+63")]
    [InlineData(" 63")]
    [InlineData("2147483648")]
    [InlineData("private")]
    [InlineData("63\0")]
    public async Task InvalidServerIdentifierLimitRetiresBeforeBegin(string value)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Limit(stream, guard.Token, LimitResult(value));
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        var error = await Assert.ThrowsAsync<IOException>(async () => await connection.BeginTransactionAsync(guard.Token));
        Assert.DoesNotContain("private", error.ToString()); Assert.Equal(ConnectionState.Broken, connection.State);
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("cancel", "save")]
    [InlineData("cancel", "rollback")]
    [InlineData("cancel", "release")]
    [InlineData("timeout", "save")]
    [InlineData("timeout", "rollback")]
    [InlineData("timeout", "release")]
    [InlineData("eof", "save")]
    [InlineData("eof", "rollback")]
    [InlineData("eof", "release")]
    public async Task InterruptedSavepointRetiresWithoutReplayOrCommitUncertainty(string failure, string operation)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        var settings = Settings(listener); settings.CommandTimeout = failure == "timeout" ? 1 : 0;
        using DbConnection connection = new KingbaseConnection(settings.ConnectionString);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Limit(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            var frames = await ReadExtended(stream, guard.Token);
            var sql = operation switch { "save" => "SAVEPOINT", "release" => "RELEASE SAVEPOINT", _ => "ROLLBACK TO SAVEPOINT" };
            Assert.Equal(Frame('P', Join([0], Encoding.UTF8.GetBytes(sql + " \"s\"\0"), [0, 0])), frames[0]);
            received.SetResult();
            if (failure != "eof") Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var transaction = await connection.BeginTransactionAsync(guard.Token);
        var execution = operation switch
        {
            "save" => transaction.SaveAsync("s", caller.Token), "release" => transaction.ReleaseAsync("s", caller.Token),
            _ => transaction.RollbackAsync("s", caller.Token)
        };
        await received.Task.WaitAsync(guard.Token);
        if (failure == "cancel")
        {
            caller.Cancel();
            Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution)).CancellationToken);
        }
        else if (failure == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => execution);
        else await Assert.ThrowsAnyAsync<IOException>(() => execution);
        Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(transaction.Connection);
        Assert.False(((KingbaseTransaction)transaction).OutcomeUnknown);
        await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }

    public static IEnumerable<object[]> InvalidLimitLayouts()
    {
        yield return [Join(Description(1), Row(["63"]), Complete(), Ready())]; // Wrong tag.
        yield return [Join(Description(1), Frame('C', Encoding.ASCII.GetBytes("SHOW\0")), Ready())]; // No row.
        yield return [Join(Description(1), Row(["63"]), Row(["63"]), Frame('C', Encoding.ASCII.GetBytes("SHOW\0")), Ready())];
        yield return [Join(Description(2), Row(["63", "63"]), Frame('C', Encoding.ASCII.GetBytes("SHOW\0")), Ready())];
        yield return [Join(Description(1), Frame('D', [0, 1, 255, 255, 255, 255]), Frame('C', Encoding.ASCII.GetBytes("SHOW\0")), Ready())];
        yield return [LimitResult("63", 'T')];
    }

    [Theory]
    [MemberData(nameof(InvalidLimitLayouts))]
    public async Task InvalidLimitResultShapeRetiresBeforeBegin(byte[] response)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Limit(stream, guard.Token, response);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        await Assert.ThrowsAsync<IOException>(async () => await connection.BeginTransactionAsync(guard.Token));
        Assert.Equal(ConnectionState.Broken, connection.State); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task SynchronizedLimitQueryRejectionDoesNotStartTransactionOrCacheInvalidFacts()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Limit(stream, guard.Token, Error("42501", 'I'));
            await Limit(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        Assert.Equal("42501", (await Assert.ThrowsAsync<KingbaseException>(async () => await connection.BeginTransactionAsync(guard.Token))).SqlState);
        Assert.Equal(ConnectionState.Open, connection.State);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        await transaction.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ReopeningRefreshesIdentifierLimitsAndRejectsStaleSavepointOwners()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(guard.Token);
                using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Limit(stream, guard.Token, LimitResult(i == 0 ? "6" : "3"));
                await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
                await Expect(stream, i == 0 ? "SAVEPOINT \"four\"" : "SAVEPOINT \"new\"", Result("SAVEPOINT", 'T'), guard.Token);
                if (i == 1) await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var old = await connection.BeginTransactionAsync(guard.Token);
        await old.SaveAsync("four", guard.Token); connection.Close();
        await connection.OpenAsync(guard.Token); using var current = await connection.BeginTransactionAsync(guard.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.SaveAsync("old", guard.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.RollbackAsync("old", guard.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.ReleaseAsync("old", guard.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => current.SaveAsync("four", guard.Token));
        await current.SaveAsync("new", guard.Token); old.Dispose(); Assert.Same(connection, current.Connection);
        await current.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }
}
