using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class PublicExecutionTests
{
    const string Product = "KingbaseES V009R001C001B0001";
    static readonly (string Name, short Size)[] Types = [("bool", 1), ("int2", 2), ("int4", 4), ("int8", 8), ("text", -1), ("bytea", -1), ("numeric", -1)];
    static uint Oid(string name) => name == "int4" ? uint.MaxValue : 9000 + (uint)Array.FindIndex(Types, t => t.Name == name);

    [Fact]
    public void FactoryAndClosedBclConfigurationExposeUsableTypedObjects()
    {
        DbProviderFactory factory = KingbaseFactory.Instance;
        using DbConnection connection = factory.CreateConnection()!;
        connection.ConnectionString = "Host=localhost;Database=db;Username=u;Password=private-password;Pooling=false;Command Timeout=7";
        Assert.IsType<KingbaseConnection>(connection);
        Assert.Same(factory, DbProviderFactories.GetFactory(connection));
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal("db", connection.Database);
        Assert.Equal("localhost", connection.DataSource);
        Assert.Equal(15, connection.ConnectionTimeout);
        Assert.Contains("private-password", connection.ConnectionString);
        Assert.Throws<InvalidOperationException>(() => connection.ServerVersion);
        using var command = connection.CreateCommand();
        Assert.IsType<KingbaseCommand>(command);
        Assert.Same(connection, command.Connection);
        Assert.Equal(7, command.CommandTimeout);
        Assert.Equal(CommandType.Text, command.CommandType);
        Assert.Equal(UpdateRowSource.Both, command.UpdatedRowSource);
        Assert.IsType<KingbaseParameterCollection>(command.Parameters);
        Assert.IsType<KingbaseParameter>(command.CreateParameter());
        Assert.IsType<KingbaseCommand>(factory.CreateCommand());
        Assert.IsType<KingbaseParameter>(factory.CreateParameter());
        Assert.IsType<KingbaseConnectionStringBuilder>(factory.CreateConnectionStringBuilder());
        command.CommandTimeout = 0;
        Assert.Equal(0, command.CommandTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => command.CommandTimeout = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => command.CommandType = (CommandType)999);
        Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar());
        Assert.Throws<NotSupportedException>(() => command.Prepare());
        command.Cancel();
        connection.Close();
        connection.Dispose();
        Assert.Throws<ObjectDisposedException>(() => connection.Open());
        Assert.Throws<ObjectDisposedException>(() => connection.ConnectionString = "");
    }

    [Fact]
    public async Task PoolingAndAmbientTransactionsRejectBeforeNetworkUse()
    {
        using var listener = Listen();
        var settings = Settings(listener); settings.Pooling = true;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await Assert.ThrowsAsync<NotSupportedException>(() => connection.OpenAsync());
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.False(listener.Pending());
        settings.Pooling = false; connection.ConnectionString = settings.ConnectionString;
        using var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<NotSupportedException>(() => connection.OpenAsync());
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicOpenCloseAndReopenConfirmIdentityAndDoNotRetainCreationCancellation(bool sync)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var states = new List<(ConnectionState, ConnectionState)>();
        connection.StateChange += (_, e) => states.Add((e.OriginalState, e.CurrentState));
        var peer = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(guard.Token);
                using var stream = client.GetStream();
                await Initialize(stream, guard.Token);
                var frames = await ReadExtended(stream, guard.Token);
                Assert.Contains("SELECT 42\0", Encoding.UTF8.GetString(frames[0]));
                await stream.WriteAsync(Value("42"), guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
        }, guard.Token);
        for (var i = 0; i < 2; i++)
        {
            if (sync) await Task.Run(connection.Open, guard.Token);
            else await connection.OpenAsync(i == 0 ? caller.Token : guard.Token);
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(Product, connection.ServerVersion);
            Assert.DoesNotContain("private-password", connection.ConnectionString);
            Assert.Throws<InvalidOperationException>(() => connection.ConnectionString = "Host=other");
            if (i == 0) caller.Cancel();
            using var command = connection.CreateCommand(); command.CommandText = "SELECT 42";
            Assert.Equal(42, sync ? command.ExecuteScalar() : await command.ExecuteScalarAsync(guard.Token));
            connection.Close(); connection.Close();
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
        Assert.Equal(new[] { (ConnectionState.Closed, ConnectionState.Connecting), (ConnectionState.Connecting, ConnectionState.Open),
            (ConnectionState.Open, ConnectionState.Closed), (ConnectionState.Closed, ConnectionState.Connecting),
            (ConnectionState.Connecting, ConnectionState.Open), (ConnectionState.Open, ConnectionState.Closed) }, states);
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task NamedPublicBindingOwnsReaderAndCloseConnectionReleasesOnce()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream();
            await Initialize(stream, guard.Token);
            var frames = await ReadExtended(stream, guard.Token);
            Assert.Equal(Frame('P', Join([0], Encoding.UTF8.GetBytes("SELECT $1\0"), [0, 1], Int32(-1))), frames[0]);
            Assert.Equal(Frame('B', Join([0, 0, 0, 0, 0, 1], Int32(2), Encoding.UTF8.GetBytes("42"), [0, 0])), frames[1]);
            await stream.WriteAsync(Value("42"), guard.Token);
            await ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Value("43"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT :p", connection);
        command.Parameters.AddWithValue("p", 42);
        using var reader = await command.ExecuteReaderAsync(guard.Token);
        Assert.True(reader.Read()); Assert.Equal(42, reader.GetInt32(0));
        using var other = new KingbaseCommand("SELECT 43", connection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.ExecuteScalarAsync(guard.Token));
        Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase("other"));
        command.Dispose();
        Assert.Equal(42, reader.GetInt32(0));
        reader.Close();
        using var last = await other.ExecuteReaderAsync(CommandBehavior.CloseConnection, guard.Token);
        Assert.True(last.Read()); Assert.Equal(43, last.GetInt32(0));
        last.Close(); last.Close();
        Assert.Equal(ConnectionState.Closed, connection.State);
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task LocalValidationAndPreCancellationDoNotWriteOrBreakConnection()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            var frames = await ReadExtended(stream, guard.Token);
            Assert.Contains("SELECT 1\0", Encoding.UTF8.GetString(frames[0]));
            await stream.WriteAsync(Value("1"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT :missing", connection);
        await Assert.ThrowsAsync<ArgumentException>(() => command.ExecuteScalarAsync(guard.Token));
        command.CommandText = "SELECT 1; SELECT 2";
        await Assert.ThrowsAsync<ArgumentException>(() => command.ExecuteNonQueryAsync(guard.Token));
        command.CommandText = "SELECT 1";
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteScalarAsync(canceled.Token));
        Assert.Equal(canceled.Token, e.CancellationToken);
        foreach (var behavior in new[] { CommandBehavior.SchemaOnly, CommandBehavior.KeyInfo, CommandBehavior.SequentialAccess })
            await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteReaderAsync(behavior, guard.Token));
        command.CommandType = CommandType.StoredProcedure;
        await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteScalarAsync(guard.Token));
        command.CommandType = CommandType.Text;
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task SynchronizedServerErrorsArePublicSafeAndAllowReuse()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C23505\0Mprivate-server-text\0Dprivate-detail\0\0")), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("1"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using DbCommand command = new KingbaseCommand("SELECT 1", connection);
        var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(guard.Token));
        Assert.Equal("23505", error.SqlState);
        Assert.Equal("private-server-text", error.MessageText);
        Assert.Equal("private-detail", error.Detail);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-", error.ToString());
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("close")]
    [InlineData("malformed")]
    public async Task InterruptedCommandsRetireWithoutReplayAndDistinguishFailureKinds(string reason)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token); received.SetResult();
            if (reason == "malformed") await stream.WriteAsync(Frame('?', []), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 1", connection) { CommandTimeout = reason == "timeout" ? 1 : 0 };
        var executing = command.ExecuteScalarAsync(caller.Token);
        await received.Task.WaitAsync(guard.Token);
        if (reason is not "malformed" and not "timeout")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteScalarAsync());
            using var other = connection.CreateCommand(); other.CommandText = "SELECT 2";
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.ExecuteScalarAsync());
            Assert.Throws<InvalidOperationException>(() => command.CommandText = "SELECT 2");
        }
        if (reason == "caller") caller.Cancel();
        else if (reason == "cancel") command.Cancel();
        else if (reason == "close") connection.Close();
        if (reason == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => executing);
        else if (reason == "malformed") await Assert.ThrowsAnyAsync<IOException>(() => executing);
        else
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing);
            if (reason == "caller") Assert.Equal(caller.Token, error.CancellationToken);
        }
        Assert.Equal(reason == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ClosingAnOpeningConnectionCancelsPublicationAndDisposingIsTerminal()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await ReadStartupAsync(stream, guard.Token); received.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var opening = connection.OpenAsync(guard.Token);
        await received.Task.WaitAsync(guard.Token);
        Assert.Equal(ConnectionState.Connecting, connection.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.OpenAsync());
        connection.Close();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(ConnectionState.Closed, connection.State);
        connection.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.OpenAsync());
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ScalarNullNoRowsAndNonqueryReturningKeepDistinctContracts()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            foreach (var response in new[] {
                Join(Frame('1'), Frame('2'), Description(1, Oid("int4"), 4), Frame('D', [0, 1, 255, 255, 255, 255]), Complete(), Ready()),
                Join(Frame('1'), Frame('2'), Description(1, Oid("int4"), 4), Tag("SELECT 0"), Ready()),
                Join(Frame('1'), Frame('2'), Frame('n'), Tag("UPDATE 3"), Ready()),
                Join(Frame('1'), Frame('2'), Description(1, Oid("int4"), 4), Row(["1"]), Tag("INSERT 0 1"), Ready()) })
            { await ReadExtended(stream, guard.Token); await stream.WriteAsync(response, guard.Token); }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using DbCommand command = new KingbaseCommand("SELECT NULL::int4", connection);
        Assert.Same(DBNull.Value, await command.ExecuteScalarAsync(guard.Token));
        command.CommandText = "SELECT 1 WHERE false";
        Assert.Null(command.ExecuteScalar());
        command.CommandText = "UPDATE t SET n=1";
        Assert.Equal(3, await command.ExecuteNonQueryAsync(guard.Token));
        command.CommandText = "INSERT INTO t(n) VALUES(1) RETURNING n";
        Assert.Equal(1, command.ExecuteNonQuery());
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("database_mode", "unknown")]
    [InlineData("client_encoding", "LATIN1")]
    public async Task ChangedCriticalSessionFactsRetireBeforePublishingResults(string key, string value)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Join(Status(key, value), Value("1")), guard.Token);
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token)); }
            catch (IOException error) when (error.InnerException is SocketException socket &&
                socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { }
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 1", connection);
        if (key == "client_encoding") await Assert.ThrowsAnyAsync<IOException>(() => command.ExecuteScalarAsync(guard.Token));
        else await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteScalarAsync(guard.Token));
        Assert.Equal(ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ClosingAnOldOwnerCancelsOnlyItsCapturedOperation()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var oldOperation = new CancellationTokenSource();
        using var oldConnection = new KingbaseConnection(Settings(listener).ConnectionString);
        using var currentConnection = new KingbaseConnection(Settings(listener).ConnectionString);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var respond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var oldClient = await listener.AcceptTcpClientAsync(guard.Token);
            using var oldStream = oldClient.GetStream(); await Initialize(oldStream, guard.Token);
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token); received.SetResult();
            Assert.Equal(0, await oldStream.ReadAsync(new byte[1], guard.Token));
            await respond.Task.WaitAsync(guard.Token);
            await stream.WriteAsync(Value("42"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await oldConnection.OpenAsync(guard.Token);
        await currentConnection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 42", currentConnection);
        // Model the old execution owner before its delayed Close cleanup, without a timing-dependent race.
        oldConnection.BeginExecution(command, oldOperation);
        var executing = command.ExecuteScalarAsync(guard.Token);
        await received.Task.WaitAsync(guard.Token);
        oldConnection.Close();
        respond.SetResult();
        Assert.True(oldOperation.IsCancellationRequested);
        Assert.Equal(42, await executing);
        Assert.Equal(ConnectionState.Open, currentConnection.State);
        currentConnection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(CommandBehavior.SingleRow, 1)]
    [InlineData(CommandBehavior.SingleResult, 2)]
    public async Task BehaviorFlagsLimitRowsAndDetachLateCancellation(CommandBehavior behavior, int count)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, Oid("int4"), 4), Row(["1"]), Row(["2"]), Tag("SELECT 2"), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("3"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT n FROM t", connection) { CommandTimeout = int.MaxValue };
        using var reader = await command.ExecuteReaderAsync(behavior, caller.Token);
        caller.Cancel(); command.Cancel();
        for (var i = 1; i <= count; i++) { Assert.True(reader.Read()); Assert.Equal(i, reader.GetInt32(0)); }
        Assert.False(reader.Read()); Assert.False(reader.NextResult());
        reader.Close();
        Assert.Equal(3, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ConnectionCloseInvalidatesReaderAndOldReaderCannotCloseReopenedSession()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(guard.Token);
                using var stream = client.GetStream(); await Initialize(stream, guard.Token);
                await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("1"), guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 1", connection);
        using var old = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, guard.Token);
        connection.Close();
        Assert.True(old.IsClosed);
        Assert.Throws<InvalidOperationException>(() => old.Read());
        await connection.OpenAsync(guard.Token);
        using var current = await command.ExecuteReaderAsync(guard.Token);
        old.Close(); old.Dispose();
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.True(current.Read()); Assert.Equal(1, current.GetInt32(0));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task StartupServerFailureIsSafeAndAllowsRetry()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using (var client = await listener.AcceptTcpClientAsync(guard.Token))
            {
                using var stream = client.GetStream(); await ReadStartupAsync(stream, guard.Token);
                await stream.WriteAsync(Frame('E', Encoding.UTF8.GetBytes("SFATAL\0C28P01\0Mprivate-startup-text\0\0")), guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
            using var retry = await listener.AcceptTcpClientAsync(guard.Token);
            using var retryStream = retry.GetStream(); await Initialize(retryStream, guard.Token);
            Assert.Equal(0, await retryStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var error = await Assert.ThrowsAsync<KingbaseException>(() => connection.OpenAsync(guard.Token));
        Assert.Equal("28P01", error.SqlState); Assert.DoesNotContain("private-", error.ToString());
        Assert.Equal(ConnectionState.Closed, connection.State);
        await connection.OpenAsync(guard.Token);
        Assert.Equal(ConnectionState.Open, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangeDatabaseReinitializesOrLeavesClosedWithNewConfiguration(bool fail)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using (var client = await listener.AcceptTcpClientAsync(guard.Token))
            {
                using var stream = client.GetStream(); await Initialize(stream, guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
            using var next = await listener.AcceptTcpClientAsync(guard.Token);
            using var nextStream = next.GetStream();
            var startup = await ReadStartupAsync(nextStream, guard.Token);
            Assert.Contains("database\0other\0", Encoding.UTF8.GetString(startup));
            if (fail) await nextStream.WriteAsync(Frame('E', Encoding.UTF8.GetBytes("SFATAL\0C3D000\0Mmissing\0\0")), guard.Token);
            else await Initialize(nextStream, guard.Token, readStartup: false);
            Assert.Equal(0, await nextStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        if (fail) Assert.Throws<KingbaseException>(() => connection.ChangeDatabase("other"));
        else connection.ChangeDatabase("other");
        Assert.Equal("other", connection.Database);
        Assert.Equal(fail ? ConnectionState.Closed : ConnectionState.Open, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task SynchronizedMalformedAffectedCountPreservesReadySession()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('n'), Tag("UPDATE nope"), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("1"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("UPDATE t SET n=1", connection);
        await Assert.ThrowsAsync<FormatException>(() => command.ExecuteNonQueryAsync(guard.Token));
        Assert.Equal(ConnectionState.Open, connection.State);
        command.CommandText = "SELECT 1";
        Assert.Equal(1, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task NextCommandUsesUpdatedConfirmedLexicalStatus()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Join(Status("standard_conforming_strings", "off"), Value("1")), guard.Token);
            var frames = await ReadExtended(stream, guard.Token);
            Assert.Contains("SELECT '\\'@hidden', $1\0", Encoding.UTF8.GetString(frames[0]));
            await stream.WriteAsync(Value("42"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SET standard_conforming_strings=off", connection);
        Assert.Equal(1, await command.ExecuteScalarAsync(guard.Token));
        command.CommandText = "SELECT '\\'@hidden', :p";
        command.Parameters.AddWithValue("p", 42);
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    static TcpListener Listen() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return listener; }
    static KingbaseConnectionStringBuilder Settings(TcpListener listener) => new()
    {
        Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "user", Database = "db",
        Password = "private-password", SslMode = KingbaseSslMode.Disable, Pooling = false
    };
    static byte[] Tag(string tag) => Frame('C', Encoding.ASCII.GetBytes(tag + "\0"));
    static byte[] Value(string value) => Join(Frame('1'), Frame('2'), Description(1, Oid("int4"), 4), Row([value]), Complete(), Ready());
    static async Task Initialize(NetworkStream stream, CancellationToken token, bool readStartup = true)
    {
        if (readStartup) await ReadStartupAsync(stream, token);
        await stream.WriteAsync(Join(Frame('R', Int32(0)), Status("client_encoding", "UTF8"), Status("server_version", "14"),
            Status("database_mode", "pg"), Status("standard_conforming_strings", "on"), Frame('K', Join(Int32(1), Int32(2))), Ready()), token);
        Assert.Equal("SELECT version();", await ReadQueryAsync(stream, token));
        await stream.WriteAsync(Scalar(Product), token);
        Assert.Contains("FROM sys_catalog.sys_type", await ReadQueryAsync(stream, token));
        await stream.WriteAsync(Catalog(Types.Select(t => new PgTypeDescription(Oid(t.Name), 90, t.Name, t.Size, 'b', 'U', 0, 0, 0, -1))), token);
    }
    static async Task<byte[][]> ReadExtended(NetworkStream stream, CancellationToken token)
    {
        var frames = new List<byte[]>();
        foreach (var code in "PBDES")
        {
            var header = new byte[5]; await stream.ReadExactlyAsync(header, token);
            Assert.Equal((byte)code, header[0]);
            var length = (header[1] << 24) | (header[2] << 16) | (header[3] << 8) | header[4];
            Assert.InRange(length, 4, 1024 * 1024);
            var body = new byte[length - 4]; await stream.ReadExactlyAsync(body, token);
            frames.Add(Join(header, body));
        }
        return frames.ToArray();
    }
}
