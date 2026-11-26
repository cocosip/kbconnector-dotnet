using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.PublicPreparedTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class BatchExecutionTests
{
    [Fact]
    public void BclConnectionAndFactoryExposeSequentialBatchCapability()
    {
        using DbConnection connection = new KingbaseConnection();
        Assert.True(connection.CanCreateBatch);
        using DbBatch batch = connection.CreateBatch();
        Assert.Same(connection, batch.Connection);
        Assert.Empty(batch.BatchCommands);
        Assert.True(KingbaseFactory.Instance.CanCreateBatch);
        using var factoryBatch = KingbaseFactory.Instance.CreateBatch();
        Assert.NotNull(factoryBatch);
        var factoryCommand = KingbaseFactory.Instance.CreateBatchCommand();
        factoryBatch.BatchCommands.Add(factoryCommand);
        foreach (var item in factoryBatch.BatchCommands) Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, item.ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, factoryCommand.ExecutionStatus);
    }

    [Fact]
    public void CollectionAndConfigurationKeepDistinctOwnershipAndBounds()
    {
        using var connection = new KingbaseConnection("Command Timeout=7");
        using var first = connection.CreateBatch(); using var other = new KingbaseBatch();
        Assert.Equal(7, first.Timeout); first.Timeout = 0; Assert.Equal(0, first.Timeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => first.Timeout = -1);
        var command = first.CreateBatchCommand();
        Assert.Equal(CommandType.Text, command.CommandType); Assert.Equal("", command.CommandText);
        Assert.Equal(-1, command.RecordsAffected); Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, command.ExecutionStatus);
        Assert.True(((DbBatchCommand)command).CanCreateParameter); Assert.IsType<KingbaseParameter>(((DbBatchCommand)command).CreateParameter());
        first.BatchCommands.Add(command);
        Assert.Throws<ArgumentException>(() => first.BatchCommands.Add(command));
        Assert.Throws<ArgumentException>(() => other.BatchCommands.Add(command));
        Assert.Throws<ArgumentException>(() => first.BatchCommands.Add(null!));
        first.BatchCommands[0] = command;
        Assert.Same(command, ((DbBatch)first).BatchCommands[0]);
        var copy = new DbBatchCommand[1]; ((DbBatch)first).BatchCommands.CopyTo(copy, 0); Assert.Same(command, copy[0]);
        Assert.True(first.BatchCommands.Remove(command)); other.BatchCommands.Add(command); other.BatchCommands.Clear();
        for (var index = 0; index < 256; index++) first.BatchCommands.Add(new("SELECT 1"));
        Assert.Throws<NotSupportedException>(() => first.BatchCommands.Add(new("SELECT 1")));
        first.BatchCommands.RemoveAt(0); Assert.Equal(255, first.BatchCommands.Count);
        first.Dispose(); Assert.Throws<ObjectDisposedException>(() => first.BatchCommands.Clear());
        Assert.Throws<ObjectDisposedException>(() => first.Timeout = 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SequentialResultsAndPerCommandParametersHoldReaderOwnership(bool sync) => Run(async (stream, token) =>
    {
        foreach (var value in new[] { "41", "42" })
        {
            var frames = await ReadExtended(stream, token);
            Assert.Contains("SELECT $1\0", Encoding.UTF8.GetString(frames[0]));
            Assert.Contains(value, Encoding.UTF8.GetString(frames[1]));
            await stream.WriteAsync(Value(value), token);
        }
    }, async (connection, token) =>
    {
        using var batch = connection.CreateBatch();
        foreach (var value in new[] { 41, 42 }) { var command = new KingbaseBatchCommand("SELECT @p"); command.Parameters.AddWithValue("p", value); batch.BatchCommands.Add(command); }
        using var reader = sync ? batch.ExecuteReader() : await batch.ExecuteReaderAsync(token);
        Assert.Equal(-1, reader.RecordsAffected);
        using var blocked = new KingbaseCommand("SELECT 99", connection); Assert.Throws<InvalidOperationException>(() => blocked.ExecuteScalar());
        Assert.True(reader.Read()); Assert.Equal(41, reader.GetInt32(0)); Assert.False(reader.Read());
        Assert.True(reader.NextResult()); Assert.True(reader.Read()); Assert.Equal(42, reader.GetInt32(0)); Assert.False(reader.NextResult());
        Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), c => Assert.Equal(KingbaseBatchCommandStatus.Succeeded, c.ExecutionStatus));
        Assert.Null(batch.FailedCommandIndex);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NonqueryCountsAndScalarExecuteEveryStatement(bool scalar) => Run(async (stream, token) =>
    {
        foreach (var response in new[] { Count("UPDATE 2"), Value("42"), Count("DELETE 3") })
        { await ReadExtended(stream, token); await stream.WriteAsync(response, token); }
    }, async (connection, token) =>
    {
        using DbBatch bcl = new KingbaseBatch(connection);
        foreach (var sql in new[] { "UPDATE t SET n=1", "SELECT 42", "DELETE FROM t" }) bcl.BatchCommands.Add(new KingbaseBatchCommand(sql));
        if (scalar) Assert.Equal(42, await bcl.ExecuteScalarAsync(token)); else Assert.Equal(5, await bcl.ExecuteNonQueryAsync(token));
        Assert.Equal(new[] { 2, -1, 3 }, bcl.BatchCommands.Select(c => c.RecordsAffected));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public Task SynchronizedServerFailureStopsPreciselyAndSessionRecovers(int failure) => Run(async (stream, token) =>
    {
        for (var index = 0; index <= failure; index++)
        { await ReadExtended(stream, token); await stream.WriteAsync(index == failure ? Error() : Count("UPDATE 1"), token); }
        await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 3);
        var error = await Assert.ThrowsAsync<KingbaseException>(() => batch.ExecuteNonQueryAsync(token));
        Assert.Equal("22012", error.SqlState); Assert.Same(batch.BatchCommands[failure], error.BatchCommand);
        Assert.Same(error.BatchCommand, ((DbException)error).BatchCommand);
        Assert.DoesNotContain("private", error.ToString()); Assert.Equal(failure, batch.FailedCommandIndex);
        for (var index = 0; index < 3; index++) Assert.Equal(index < failure ? KingbaseBatchCommandStatus.Succeeded : index == failure ? KingbaseBatchCommandStatus.Failed : KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[index].ExecutionStatus);
        Assert.Equal(ConnectionState.Open, connection.State);
        using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
    });

    [Theory]
    [InlineData("empty")]
    [InlineData("type")]
    [InlineData("null")]
    [InlineData("multiple")]
    public Task LocalMiddleFailurePreservesEarlierSuccessWithoutSendingInvalidCommand(string kind) => Run(async (stream, token) =>
    {
        await ReadExtended(stream, token); await stream.WriteAsync(Count("UPDATE 1"), token);
        await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 3);
        var bad = batch.BatchCommands[1];
        if (kind == "empty") bad.CommandText = "";
        if (kind == "type") bad.CommandType = CommandType.StoredProcedure;
        if (kind == "null") { bad.CommandText = "SELECT @p"; bad.Parameters.AddWithValue("p", DBNull.Value); }
        if (kind == "multiple") bad.CommandText = "SELECT 1; SELECT 2";
        await Assert.ThrowsAnyAsync<Exception>(() => batch.ExecuteNonQueryAsync(token));
        Assert.Equal(1, batch.FailedCommandIndex); Assert.Equal(1, batch.BatchCommands[0].RecordsAffected);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.Failed, bad.ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[2].ExecutionStatus);
        using var recovery = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await recovery.ExecuteScalarAsync(token));
    });

    [Fact]
    public Task LaterStatementUsesLexicalStatusReportedByEarlierSet() => Run(async (stream, token) =>
    {
        await ReadExtended(stream, token);
        await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('n'), Status("standard_conforming_strings", "off"), Tag("SET"), Ready()), token);
        var frames = await ReadExtended(stream, token);
        Assert.Contains("SELECT 'a\\'@hidden', $1", Encoding.UTF8.GetString(frames[0]));
        await stream.WriteAsync(Value("42"), token);
    }, async (connection, token) =>
    {
        using var batch = connection.CreateBatch(); batch.BatchCommands.Add(new("SET standard_conforming_strings=off"));
        var command = new KingbaseBatchCommand("SELECT 'a\\'@hidden', @p"); command.Parameters.AddWithValue("p", 42); batch.BatchCommands.Add(command);
        Assert.Equal(42, await batch.ExecuteScalarAsync(token));
    });

    [Theory]
    [InlineData("caller")]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("dispose")]
    public async Task InterruptionRetainsAcknowledgmentsAndMarksOnlyInFlightOutcomeUnknown(string cause)
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (stream, token) =>
        {
            await ReadExtended(stream, token); await stream.WriteAsync(Count("UPDATE 1"), token);
            await ReadExtended(stream, token); waiting.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        }, async (connection, token) =>
        {
            using var batch = Make(connection, 3); using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
            var task = batch.ExecuteNonQueryAsync(caller.Token); await waiting.Task.WaitAsync(token);
            Assert.Throws<InvalidOperationException>(() => batch.BatchCommands.Clear());
            Assert.Throws<InvalidOperationException>(() => batch.BatchCommands[0].CommandText = "SELECT 8");
            Assert.Throws<InvalidOperationException>(() => batch.Timeout = 9);
            if (cause == "caller") caller.Cancel(); else if (cause == "cancel") batch.Cancel(); else if (cause == "close") connection.Close(); else batch.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(1, batch.FailedCommandIndex);
            Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
            Assert.Equal(KingbaseBatchCommandStatus.OutcomeUnknown, batch.BatchCommands[1].ExecutionStatus);
            Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[2].ExecutionStatus);
        }, expectPeerEof: false);
    }

    [Fact]
    public Task OneTimeoutCoversEarlierAcknowledgedStatementsAndCurrentExchange() => Run(async (stream, token) =>
    {
        await ReadExtended(stream, token); await Task.Delay(600, token); await stream.WriteAsync(Count("UPDATE 1"), token);
        await ReadExtended(stream, token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 3); batch.Timeout = 1; var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => batch.ExecuteNonQueryAsync(token));
        Assert.InRange(elapsed.ElapsedMilliseconds, 800, 1500);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.OutcomeUnknown, batch.BatchCommands[1].ExecutionStatus);
    }, expectPeerEof: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExplicitPreparationReusesHandlesAndFreshValues(bool sync) => Run(async (stream, token) =>
    {
        await ExpectPrepare(stream, "kbp_1", "SELECT $1", token, uint.MaxValue);
        for (var run = 0; run < 2; run++) foreach (var value in new[] { "41", run == 0 ? "42" : "43" })
        { var frames = await ReadFrames(stream, "BDES", token); Assert.Contains(value, Encoding.UTF8.GetString(frames[0])); await stream.WriteAsync(PreparedValue(value), token); }
    }, async (connection, token) =>
    {
        using var batch = connection.CreateBatch();
        foreach (var value in new[] { 41, 42 }) { var command = new KingbaseBatchCommand("SELECT @p"); command.Parameters.AddWithValue("p", value); batch.BatchCommands.Add(command); }
        if (sync) batch.Prepare(); else await batch.PrepareAsync(token);
        Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), c => Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, c.ExecutionStatus));
        Assert.Equal(41, await batch.ExecuteScalarAsync(token)); batch.BatchCommands[1].Parameters[0].Value = 43;
        Assert.Equal(41, batch.ExecuteScalar()); Assert.Null(batch.FailedCommandIndex);
    });

    [Fact]
    public Task AggregateAffectedOverflowStopsBeforeNextCommand() => Run(async (stream, token) =>
    {
        for (var index = 0; index < 2; index++)
        { await ReadExtended(stream, token); await stream.WriteAsync(Count("UPDATE 2147483647"), token); }
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 3);
        await Assert.ThrowsAsync<OverflowException>(() => batch.ExecuteNonQueryAsync(token));
        Assert.Equal(1, batch.FailedCommandIndex);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[1].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[2].ExecutionStatus);
    });

    [Theory]
    [InlineData("rows")]
    [InlineData("columns")]
    [InlineData("bytes")]
    public Task AggregateBufferBoundsStopAfterAcknowledgmentWithoutNextStatement(string bound) => Run(async (stream, token) =>
    {
        for (var index = 0; index < 2; index++)
        {
            await ReadExtended(stream, token);
            var payload = bound switch
            {
                "rows" => Join(Frame('1'), Frame('2'), Description(1, uint.MaxValue, 4), Join(Enumerable.Range(0, 600).Select(_ => Row(["42"])).ToArray()), Complete(), Ready()),
                "columns" => Join(Frame('1'), Frame('2'), Description(600, uint.MaxValue, 4), Complete(), Ready()),
                _ => Join(Frame('1'), Frame('2'), Description(1, 9004, -1), Row([new string('a', 600000)]), Complete(), Ready())
            };
            await stream.WriteAsync(payload, token);
        }
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 3);
        await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteNonQueryAsync(token));
        Assert.Equal(1, batch.FailedCommandIndex);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[1].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[2].ExecutionStatus);
        Assert.Equal(ConnectionState.Open, connection.State);
    });

    [Fact]
    public Task RepeatedExecuteResetsPreviousFailureAndCounts() => Run(async (stream, token) =>
    {
        await ReadExtended(stream, token); await stream.WriteAsync(Count("UPDATE 2"), token);
        await ReadExtended(stream, token); await stream.WriteAsync(Error(), token);
        for (var index = 0; index < 3; index++) { await ReadExtended(stream, token); await stream.WriteAsync(Count("UPDATE 1"), token); }
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 3);
        await Assert.ThrowsAsync<KingbaseException>(() => batch.ExecuteNonQueryAsync(token));
        Assert.Equal(3, await batch.ExecuteNonQueryAsync(token));
        Assert.Null(batch.FailedCommandIndex);
        Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), c => { Assert.Equal(1, c.RecordsAffected); Assert.Equal(KingbaseBatchCommandStatus.Succeeded, c.ExecutionStatus); });
    });

    [Theory]
    [InlineData(CommandBehavior.SingleResult)]
    [InlineData(CommandBehavior.SingleRow)]
    [InlineData(CommandBehavior.CloseConnection)]
    public Task ReaderBehaviorDoesNotSkipLaterStatements(CommandBehavior behavior) => Run(async (stream, token) =>
    {
        foreach (var value in new[] { "41", "42" }) { await ReadExtended(stream, token); await stream.WriteAsync(Value(value), token); }
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 2);
        using (var reader = await batch.ExecuteReaderAsync(behavior, token))
        {
            Assert.True(reader.Read()); Assert.Equal(41, reader.GetInt32(0));
            Assert.Equal(behavior == CommandBehavior.CloseConnection, reader.NextResult());
            Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[1].ExecutionStatus);
        }
        Assert.Equal(behavior == CommandBehavior.CloseConnection ? ConnectionState.Closed : ConnectionState.Open, connection.State);
    });

    [Fact]
    public async Task ClosedEmptyCanceledAndUnsupportedBehaviorRejectBeforeNetwork()
    {
        using var batch = new KingbaseBatch();
        Assert.Throws<InvalidOperationException>(() => batch.ExecuteScalar());
        batch.BatchCommands.Add(new("SELECT 1"));
        Assert.Throws<InvalidOperationException>(() => batch.ExecuteScalar());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.ExecuteNonQueryAsync(cancel.Token));
        Assert.Throws<NotSupportedException>(() => batch.ExecuteReader(CommandBehavior.SequentialAccess | CommandBehavior.KeyInfo));
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[0].ExecutionStatus);
    }

    [Fact]
    public Task FailedAutomaticParseDoesNotClaimSqlExecutionOutcomeUnknown() => Run(async (stream, token) =>
    {
        await ReadFrames(stream, "PS", token);
        // The connection ends during preparation, before any named Bind/Execute was sent.
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 2);
        await Assert.ThrowsAnyAsync<Exception>(() => batch.ExecuteNonQueryAsync(token));
        Assert.Equal(0, batch.FailedCommandIndex);
        Assert.Equal(KingbaseBatchCommandStatus.Failed, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus);
    }, expectPeerEof: false, configure: settings => { settings.MaxAutoPrepare = 1; settings.AutoPrepareMinUsages = 1; });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AcknowledgedButUnsupportedResultsStopWithoutClaimingStatementFailure(bool identityChange) => Run(async (stream, token) =>
    {
        await ReadExtended(stream, token);
        var response = identityChange
            ? Join(Frame('1'), Frame('2'), Status("database_mode", "oracle"), Frame('n'), Tag("SET"), Ready())
            : Join(Frame('1'), Frame('2'), Description(1, 123456, 4), Row(["42"]), Complete(), Ready());
        await stream.WriteAsync(response, token);
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 2);
        await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteReaderAsync(token));
        Assert.Equal(0, batch.FailedCommandIndex);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus);
        Assert.Equal(identityChange ? ConnectionState.Broken : ConnectionState.Open, connection.State);
    });

    [Fact]
    public Task PreparedSqlEditsDetachAndDisposalDropsRetainedHandles() => Run(async (stream, token) =>
    {
        await ExpectPrepare(stream, "kbp_1", "SELECT 41", token);
        await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
    }, async (connection, token) =>
    {
        using var batch = connection.CreateBatch(); var item = new KingbaseBatchCommand("SELECT 41"); batch.BatchCommands.Add(item);
        await batch.PrepareAsync(token); Assert.NotNull(item.Prepared);
        item.CommandText = "SELECT 42"; Assert.Null(item.Prepared);
        Assert.Equal(42, await batch.ExecuteScalarAsync(token));
        // Reattaching a retained cache handle is independent of mutable command ownership.
        item.CommandText = "SELECT 41"; await batch.PrepareAsync(token); Assert.NotNull(item.Prepared);
        batch.Dispose(); Assert.Null(item.Prepared);
    });

    [Fact]
    public Task AssignedTransactionEndingStopsBeforeAutocommitStatement() => Run(async (stream, token) =>
    {
        await SavepointExecutionTests.Limit((System.Net.Sockets.NetworkStream)stream, token);
        await ReadExtended(stream, token); await stream.WriteAsync(TransactionExecutionTests.Result("BEGIN", 'T'), token);
        await ReadExtended(stream, token); await stream.WriteAsync(Count("ROLLBACK"), token);
        var frames = await ReadExtended(stream, token);
        await stream.WriteAsync(Value("42"), token);
        Assert.Contains("SELECT 42\0", Encoding.UTF8.GetString(frames[0]));
    }, async (connection, token) =>
    {
        using var tx = await connection.BeginTransactionAsync(token);
        using var batch = new KingbaseBatch(connection, tx);
        batch.BatchCommands.Add(new("ROLLBACK")); batch.BatchCommands.Add(new("INSERT INTO t VALUES (1)"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteNonQueryAsync(token));
        Assert.Equal(1, batch.FailedCommandIndex);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.Failed, batch.BatchCommands[1].ExecutionStatus);
        using var recovery = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await recovery.ExecuteScalarAsync(token));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AcknowledgedWriteCountSurvivesPostExecutionValidation(bool identityChange) => Run(async (stream, token) =>
    {
        await ReadExtended(stream, token);
        var metadata = identityChange ? Join(Status("database_mode", "oracle"), Frame('n')) : Join(Description(1, 123456, 4), Row(["{1}"]));
        await stream.WriteAsync(Join(Frame('1'), Frame('2'), metadata, Tag("UPDATE 1"), Ready()), token);
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 2);
        await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteNonQueryAsync(token));
        Assert.Equal(1, batch.BatchCommands[0].RecordsAffected);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus);
    });

    [Fact]
    public Task SynchronizedUncertainErrorRetainsUnknownOutcome() => Run(async (stream, token) =>
    {
        await ReadExtended(stream, token);
        await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('E', Encoding.UTF8.GetBytes("SERROR\0C40003\0Munknown\0\0")), Ready()), token);
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 2);
        var error = await Assert.ThrowsAsync<KingbaseException>(() => batch.ExecuteNonQueryAsync(token));
        Assert.Equal("40003", error.SqlState); Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(KingbaseBatchCommandStatus.OutcomeUnknown, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ScalarConversionStopsBeforeLaterWrites(bool sync) => Run(async (stream, token) =>
    {
        await ReadExtended(stream, token);
        await stream.WriteAsync(Join(Status("DateStyle", "ISO, MDY"), Frame('1'), Frame('2'), Description(1, 9007, 4), Row(["infinity"]), Complete(), Ready()), token);
        var frames = await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        Assert.Contains("SELECT 42\0", Encoding.UTF8.GetString(frames[0]));
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 2); batch.BatchCommands[0].CommandText = "SELECT 'infinity'::date";
        if (sync) Assert.Throws<OverflowException>(() => batch.ExecuteScalar());
        else await Assert.ThrowsAsync<OverflowException>(() => batch.ExecuteScalarAsync(token));
        Assert.Equal(0, batch.FailedCommandIndex);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus);
        using var recovery = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await recovery.ExecuteScalarAsync(token));
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("behavior")]
    [InlineData("connection")]
    public Task RejectedExecuteClearsEarlierReporting(string cause) => Run(async (stream, token) =>
    {
        await ReadExtended(stream, token); await stream.WriteAsync(Count("UPDATE 1"), token);
        await ReadExtended(stream, token); await stream.WriteAsync(Error(), token);
    }, async (connection, token) =>
    {
        using var batch = Make(connection, 2); await Assert.ThrowsAsync<KingbaseException>(() => batch.ExecuteNonQueryAsync(token));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        if (cause == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.ExecuteNonQueryAsync(canceled.Token));
        else if (cause == "behavior") Assert.Throws<NotSupportedException>(() => batch.ExecuteReader(CommandBehavior.SequentialAccess | CommandBehavior.KeyInfo));
        else { batch.Connection = null; Assert.Throws<InvalidOperationException>(() => batch.ExecuteNonQuery()); }
        Assert.Null(batch.FailedCommandIndex);
        foreach (KingbaseBatchCommand item in batch.BatchCommands) { Assert.Equal(-1, item.RecordsAffected); Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, item.ExecutionStatus); }
    });

    [Theory]
    [InlineData("remove")]
    [InlineData("replace")]
    [InlineData("clear")]
    [InlineData("connection")]
    public Task PreparedAttachmentsDropWhenDetached(string cause) => Run(async (stream, token) =>
    {
        await ExpectPrepare(stream, "kbp_1", "SELECT 41", token);
    }, async (connection, token) =>
    {
        using var batch = connection.CreateBatch(); var item = new KingbaseBatchCommand("SELECT 41"); batch.BatchCommands.Add(item);
        await batch.PrepareAsync(token); var handle = item.Prepared; Assert.NotNull(handle);
        if (cause == "remove") batch.BatchCommands.Remove(item);
        else if (cause == "replace") batch.BatchCommands[0] = new("SELECT 42");
        else if (cause == "clear") batch.BatchCommands.Clear();
        else batch.Connection = null;
        Assert.Null(item.Prepared); Assert.True(handle.IsValid);
    });

    static KingbaseBatch Make(KingbaseConnection connection, int count)
    { var batch = connection.CreateBatch(); for (var index = 0; index < count; index++) batch.BatchCommands.Add(new("UPDATE t SET n=1")); return batch; }
    static byte[] Tag(string tag) => Frame('C', Encoding.ASCII.GetBytes(tag + "\0"));
    static byte[] Count(string tag) => Join(Frame('1'), Frame('2'), Frame('n'), Tag(tag), Ready());
    static byte[] Error() => Join(Frame('1'), Frame('2'), Frame('E', Encoding.UTF8.GetBytes("SERROR\0C22012\0Mprivate-server-text\0\0")), Ready());
    static async Task Run(Func<Stream, CancellationToken, Task> server, Func<KingbaseConnection, CancellationToken, Task> client, bool expectPeerEof = true, Action<KingbaseConnectionStringBuilder>? configure = null)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var settings = Settings(listener); configure?.Invoke(settings);
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var peer = Task.Run(async () => { using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token); await server(stream, guard.Token); if (expectPeerEof) Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token)); }, guard.Token);
        await connection.OpenAsync(guard.Token);
        try { await client(connection, guard.Token); }
        finally { connection.Close(); }
        await peer.WaitAsync(guard.Token);
    }
}
