using System.Buffers.Binary;
using System.Data;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class LargeObjectExecutionTests
{
    [Fact]
    public async Task RawReleaseCannotLeaveAStaleDuplicateSavepointMark()
    {
        await Run(async (connection, transaction, token) =>
        {
            await transaction.SaveAsync("s", token); using var stream = await new KingbaseLargeObjectManager(connection).OpenReadWriteAsync(123, token); await transaction.SaveAsync("s", token);
            using (var release = new KingbaseCommand("RELEASE SAVEPOINT \"s\"", connection) { Transaction = transaction }) await release.ExecuteNonQueryAsync(token);
            await transaction.RollbackAsync("s", token); Assert.False(stream.CanRead); await stream.DisposeAsync();
        }, async (stream, token) =>
        {
            await Control(stream, "SAVEPOINT", token); await Function(stream, "lo_open", ["123", "393216"], Reply("1", "int4"), token);
            await Control(stream, "SAVEPOINT", token); await Control(stream, "RELEASE", token); await Control(stream, "ROLLBACK", token);
        });
    }

    [Fact]
    public async Task AReusedDescriptorInvalidatesItsPreviousHandleBeforeItCanCloseTheReplacement()
    {
        await Run(async (connection, _, token) =>
        {
            var manager = new KingbaseLargeObjectManager(connection); var first = await manager.OpenReadWriteAsync(123, token); using var second = await manager.OpenReadWriteAsync(124, token);
            Assert.False(first.CanRead); first.Dispose(); Assert.Equal(7, second.Position);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "393216"], Reply("1", "int4"), token); await Function(stream, "lo_open", ["124", "393216"], Reply("1", "int4"), token);
            await Function(stream, "lo_tell64", ["1"], Reply("7", "int8"), token); await Function(stream, "lo_close", ["1"], Reply("0", "int4"), token);
        });
    }

    [Theory]
    [InlineData("lo_open", false)]
    [InlineData("lo_create", false)]
    [InlineData("lo_close", false)]
    [InlineData("lo_close", true)]
    public async Task UncertainFunctionResultsRetireTheSessionAndPreserveServerSqlState(string function, bool stateHandlerThrows)
    {
        await Run(async (connection, _, token) =>
        {
            if (stateHandlerThrows) connection.StateChange += (_, args) => { if (args.CurrentState == ConnectionState.Broken) throw new InvalidOperationException("secondary state handler"); };
            var manager = new KingbaseLargeObjectManager(connection); KingbaseException error;
            if (function == "lo_create") error = await Assert.ThrowsAsync<KingbaseException>(() => manager.CreateAsync(cancellationToken: token));
            else if (function == "lo_open") error = await Assert.ThrowsAsync<KingbaseException>(() => manager.OpenReadWriteAsync(123, token));
            else { var handle = await manager.OpenReadWriteAsync(123, token); error = await Assert.ThrowsAsync<KingbaseException>(async () => await handle.DisposeAsync()); }
            Assert.Equal("40003", error.SqlState); Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        {
            if (function == "lo_close") await Function(stream, "lo_open", ["123", "393216"], Reply("1", "int4"), token);
            var request = await Request(stream, token); Assert.Contains("pg_catalog." + function + "(", request.Sql);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, function == "lo_create" ? Oid("oid") : Oid("int4"), 4), Row([function == "lo_create" ? "123" : "0"]), Complete(), Error("40003")), token);
        }, rollback: false);
    }

    [Fact]
    public async Task ALengthRestoreFailureCannotReactivateTheHandleAfterSavepointRecovery()
    {
        await Run(async (connection, transaction, token) =>
        {
            using var stream = await new KingbaseLargeObjectManager(connection).OpenReadWriteAsync(123, token); await transaction.SaveAsync("s", token);
            var error = Assert.Throws<KingbaseException>(() => _ = stream.Length); Assert.Equal("22023", error.SqlState); await transaction.RollbackAsync("s", token); Assert.False(stream.CanRead);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "393216"], Reply("1", "int4"), token); await Control(stream, "SAVEPOINT", token);
            await Function(stream, "lo_tell64", ["1"], Reply("5", "int8"), token); await Function(stream, "lo_lseek64", ["1", "0", "2"], Reply("12", "int8"), token);
            await Function(stream, "lo_lseek64", ["1", "5", "0"], Error("22023", 'E'), token); await Control(stream, "ROLLBACK", token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownResultMetadataRetiresAnUnpublishedDescriptorInsteadOfLeavingItForCommit(bool stateHandlerThrows)
    {
        await Run(async (connection, _, token) =>
        {
            if (stateHandlerThrows) connection.StateChange += (_, args) => { if (args.CurrentState == ConnectionState.Broken) throw new InvalidOperationException("secondary state handler"); };
            await Assert.ThrowsAsync<IOException>(() => new KingbaseLargeObjectManager(connection).OpenReadWriteAsync(123, token)); Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        { await Function(stream, "lo_open", ["123", "393216"], Join(Frame('1'), Frame('2'), Description(1, 123456789, 4), Row(["1"]), Complete(), Frame('Z', [(byte)'T'])), token); }, rollback: false);
    }

    [Fact]
    public async Task PublicReleaseOfAnUntrackedRawSavepointPreservesExistingDescriptors()
    {
        await Run(async (connection, transaction, token) =>
        {
            using var stream = await new KingbaseLargeObjectManager(connection).OpenReadWriteAsync(123, token);
            using (var save = new KingbaseCommand("SAVEPOINT s", connection) { Transaction = transaction }) await save.ExecuteNonQueryAsync(token);
            await transaction.ReleaseAsync("s", token); Assert.True(stream.CanRead); Assert.Equal(7, stream.Position);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "393216"], Reply("1", "int4"), token); await Control(stream, "SAVEPOINT", token); await Control(stream, "RELEASE", token);
            await Function(stream, "lo_tell64", ["1"], Reply("7", "int8"), token); await Function(stream, "lo_close", ["1"], Reply("0", "int4"), token);
        });
    }

    internal static uint Oid(string type) => type switch { "int4" => uint.MaxValue, "oid" => 19000u, "bool" => 9000u, "int2" => 9001u, "int8" => 9003u, "text" => 9004u, "bytea" => 9005u, _ => throw new ArgumentException(type) };
    internal static byte[] Reply(string value, string type, byte[]? prefix = null) => Join(prefix ?? [], Frame('1'), Frame('2'), Description(1, Oid(type), type switch { "int8" => 8, "bytea" or "text" => -1, _ => 4 }), Row([value]), Complete(), Frame('Z', [(byte)'T']));
    internal static byte[] Error(string state, char status = 'T') => Join(Frame('E', Encoding.ASCII.GetBytes("SERROR\0C" + state + "\0Mprivate-lob-error\0\0")), Frame('Z', [(byte)status]));
    internal static async Task<(string Sql, string?[] Values)> Request(Stream stream, CancellationToken token)
    {
        var frames = await ReadExtended(stream, token); var parse = frames[0]; var end = Array.IndexOf(parse, (byte)0, 6); var sql = Encoding.UTF8.GetString(parse, 6, end - 6);
        var bind = frames[1]; var count = BinaryPrimitives.ReadInt16BigEndian(bind.AsSpan(9)); var values = new string?[count]; var offset = 11;
        for (var i = 0; i < count; i++) { var length = BinaryPrimitives.ReadInt32BigEndian(bind.AsSpan(offset)); offset += 4; if (length < 0) continue; values[i] = Encoding.UTF8.GetString(bind, offset, length); offset += length; }
        return (sql, values);
    }
    internal static async Task Function(Stream stream, string name, string?[] values, byte[] response, CancellationToken token)
    { var request = await Request(stream, token); Assert.StartsWith("SELECT pg_catalog." + name + "(", request.Sql); Assert.Equal(values, request.Values); await stream.WriteAsync(response, token); }
    internal static async Task Control(Stream stream, string tag, CancellationToken token)
    { var request = await Request(stream, token); Assert.StartsWith(tag, request.Sql); await stream.WriteAsync(TransactionExecutionTests.Result(tag, 'T'), token); }
    internal static async Task Run(Func<KingbaseConnection, KingbaseTransaction, CancellationToken, Task> action, Func<Stream, CancellationToken, Task> respond, bool rollback = true, int commandTimeout = 30, int cleanupTimeout = 5)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var settings = Settings(listener); settings.CommandTimeout = commandTimeout; settings.CleanupTimeout = cleanupTimeout; using var connection = new KingbaseConnection(settings.ConnectionString);
        var types = new[] { ("bool", (short)1), ("int2", (short)2), ("int4", (short)4), ("int8", (short)8), ("text", (short)-1), ("bytea", (short)-1), ("oid", (short)4) };
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream();
            await Initialize(stream, guard.Token, catalogTypes: types.Select(type => new PgTypeDescription(Oid(type.Item1), 90, type.Item1, type.Item2, 'b', 'U', 0, 0, 0, -1)));
            await SavepointExecutionTests.Limit(stream, guard.Token); var begin = await Request(stream, guard.Token); Assert.StartsWith("BEGIN", begin.Sql); await stream.WriteAsync(TransactionExecutionTests.Result("BEGIN", 'T'), guard.Token);
            await respond(stream, guard.Token);
            if (rollback) { var request = await Request(stream, guard.Token); Assert.Equal("ROLLBACK", request.Sql); await stream.WriteAsync(TransactionExecutionTests.Result("ROLLBACK", 'I'), guard.Token); }
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token)); }
            catch (IOException error) when (error.InnerException is System.Net.Sockets.SocketException socket && socket.SocketErrorCode is System.Net.Sockets.SocketError.ConnectionReset or System.Net.Sockets.SocketError.ConnectionAborted) { }
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var transaction = await connection.BeginTransactionAsync(guard.Token); await action(connection, transaction, guard.Token);
        if (rollback) await transaction.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }
}
