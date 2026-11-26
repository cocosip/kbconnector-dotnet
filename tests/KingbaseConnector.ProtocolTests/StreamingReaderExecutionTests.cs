using System.Data;
using System.Text;
using System.Net.Sockets;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class StreamingReaderExecutionTests
{
    [Fact]
    public async Task LastBatchAcknowledgmentDuringIndependentCleanupKeepsANullStopIndex()
    {
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            using var batch = new KingbaseBatch(connection) { Timeout = 1 }; batch.BatchCommands.Add(new("UPDATE first RETURNING value"));
            var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); closing.SetResult(); await reader.CloseAsync();
            Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus); Assert.Equal(1, batch.BatchCommands[0].RecordsAffected); Assert.Null(batch.FailedCommandIndex);
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Header(1), token); await closing.Task.WaitAsync(token); await Task.Delay(1200, token);
            await stream.WriteAsync(Join(Frame('C', Encoding.ASCII.GetBytes("UPDATE 1\0")), Ready()), token);
        });
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task FourModeConsumerProbeStreamsLargeFieldsAcrossTwoPoolBorrowers(KingbaseCompatibilityMode mode) =>
        KingbaseConnector.Tests.PoolAssetProbe.RunAsync(mode, streaming: true);

    [Fact]
    public async Task AutomaticPreparationCannotSendExecuteAfterTheOriginalBatchDeadline()
    {
        await Run(async (connection, token) =>
        {
            using (var prime = new KingbaseCommand("UPDATE second", connection)) Assert.Equal(1, await prime.ExecuteNonQueryAsync(token));
            using var batch = new KingbaseBatch(connection) { Timeout = 1 }; batch.BatchCommands.Add(new("SELECT first")); batch.BatchCommands.Add(new("UPDATE second"));
            var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            await Assert.ThrowsAsync<TimeoutException>(reader.CloseAsync); Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
            Assert.Equal(KingbaseBatchCommandStatus.Failed, batch.BatchCommands[1].ExecutionStatus); Assert.Equal(1, batch.FailedCommandIndex);
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('n'), Frame('C', Encoding.ASCII.GetBytes("UPDATE 1\0")), Ready()), token);
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Tail()), token);
            await PublicPreparedTests.ReadFrames(stream, "PS", token); await Task.Delay(1200, token);
            try { await stream.WriteAsync(Join(Frame('1'), Ready()), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted }) { }
        }, settings => { settings.MaxAutoPrepare = 1; settings.AutoPrepareMinUsages = 2; });
    }

    [Fact]
    public async Task SuccessfulBatchKeepsANullFailureIndexWhenAStartupNoticeCallbackThrows()
    {
        await Run(async (connection, token) =>
        {
            connection.Notice += (_, _) => throw new InvalidOperationException("callback failure");
            using var batch = new KingbaseBatch(connection); batch.BatchCommands.Add(new("UPDATE first"));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token));
            Assert.Equal("callback failure", error.Message); Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
            Assert.Equal(1, batch.BatchCommands[0].RecordsAffected); Assert.Null(batch.FailedCommandIndex);
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('N', Encoding.ASCII.GetBytes("SNOTICE\0C00000\0Mnotice\0\0")), Frame('n'), Frame('C', Encoding.ASCII.GetBytes("UPDATE 1\0")), Ready()), token);
        });
    }

    [Fact]
    public async Task CompletedBatchCloseConnectionDoesNotRewriteItsSuccessfulReport()
    {
        await Run(async (connection, token) =>
        {
            using var batch = new KingbaseBatch(connection); batch.BatchCommands.Add(new("UPDATE first"));
            using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection, token);
            Assert.Null(batch.FailedCommandIndex); reader.Close(); Assert.Null(batch.FailedCommandIndex); Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
        }, async (stream, token) =>
        { await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('n'), Frame('C', Encoding.ASCII.GetBytes("UPDATE 1\0")), Ready()), token); });
    }

    [Fact]
    public async Task ConnectionCloseInterruptsAPendingReadAndInvalidatesTheReader()
    {
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection) { CommandTimeout = 0 };
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); var pending = reader.ReadAsync(token);
            await connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2), token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.True(reader.IsClosed); Assert.Equal(ConnectionState.Closed, connection.State);
        }, async (stream, token) =>
        { await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Header(1), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); });
    }

    [Fact]
    public async Task InvalidFieldTypeOrBackwardOffsetCannotInvalidateTheCurrentTextReader()
    {
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(reader.Read());
            using var first = reader.GetTextReader(0); Assert.Equal('a', first.Read());
            Assert.Throws<InvalidCastException>(() => reader.GetBytes(1, 0, null, 0, 0)); Assert.Equal('b', first.Read());
            Assert.Throws<InvalidOperationException>(() => reader.GetChars(0, 0, new char[1], 0, 1)); Assert.Equal("cdef", first.ReadToEnd());
            using var second = reader.GetTextReader(1); Assert.Equal('s', second.Read());
            Assert.Throws<InvalidOperationException>(() => reader.GetChars(0, 0, null, 0, 0)); Assert.Equal("econd", second.ReadToEnd()); Assert.False(reader.Read());
        }, async (stream, token) =>
        { await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(2), Row(["abcdef", "second"]), Tail()), token); });
    }

    [Fact]
    public async Task CleanupMayFinishAnExistingStatementButCannotStartAnotherAfterTheBatchDeadline()
    {
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            using var batch = new KingbaseBatch(connection) { Timeout = 1 }; batch.BatchCommands.Add(new("SELECT first")); batch.BatchCommands.Add(new("UPDATE unsent"));
            var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); closing.SetResult();
            await Assert.ThrowsAsync<TimeoutException>(reader.CloseAsync);
            Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus); Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus);
            Assert.Equal(1, batch.FailedCommandIndex); Assert.Equal(ConnectionState.Open, connection.State);
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Header(1), token);
            await closing.Task.WaitAsync(token); await Task.Delay(1200, token); await stream.WriteAsync(Tail(), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Fact]
    public async Task CancelThenCloseRecordsAnAlreadySentWriteAsUnknown()
    {
        await Run(async (connection, token) =>
        {
            using var batch = new KingbaseBatch(connection); batch.BatchCommands.Add(new("UPDATE first RETURNING value")); batch.BatchCommands.Add(new("UPDATE unsent"));
            using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); batch.Cancel(); reader.Close();
            Assert.Equal(KingbaseBatchCommandStatus.OutcomeUnknown, batch.BatchCommands[0].ExecutionStatus); Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus); Assert.Equal(0, batch.FailedCommandIndex);
        }, async (stream, token) =>
        { await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Header(1), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); });
    }

    [Fact]
    public async Task RowReturningAffectedCountOverflowTerminatesBeforeAnotherBatchWrite()
    {
        await Run(async (connection, token) =>
        {
            using var batch = new KingbaseBatch(connection); batch.BatchCommands.Add(new("UPDATE first RETURNING value")); batch.BatchCommands.Add(new("UPDATE unsent"));
            using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(reader.Read());
            await Assert.ThrowsAsync<OverflowException>(() => reader.ReadAsync(token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.NextResultAsync(token)); reader.Close();
            Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus); Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus);
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token);
            await stream.WriteAsync(Join(Header(1), Row(["value"]), Frame('C', Encoding.ASCII.GetBytes("UPDATE 2147483648\0")), Ready()), token);
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionCloseRetiresAnUnreadExchangeWithoutWaitingForTailFrames(bool tailAvailable)
    {
        await Run(async (connection, token) =>
        {
            using var batch = new KingbaseBatch(connection); batch.BatchCommands.Add(new("UPDATE first RETURNING value")); batch.BatchCommands.Add(new("UPDATE unsent"));
            using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            await connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2), token); Assert.True(reader.IsClosed);
            Assert.Equal(KingbaseBatchCommandStatus.OutcomeUnknown, batch.BatchCommands[0].ExecutionStatus); Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus);
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(tailAvailable ? Join(Header(1), Row(["value"]), Tail()) : Header(1), token);
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingReusesExplicitAndAutomaticPreparation(bool automatic)
    {
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT :p", connection); command.Parameters.AddWithValue("p", 42);
            if (!automatic) await command.PrepareAsync(token);
            for (var i = 0; i < 2; i++)
            {
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
                Assert.True(await reader.ReadAsync(token)); Assert.Equal(42, await reader.GetFieldValueAsync<int>(0, token)); Assert.False(await reader.ReadAsync(token));
            }
        }, async (stream, token) =>
        {
            if (automatic) { await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(PublicExecutionTests.Value("42"), token); }
            await PublicPreparedTests.ExpectPrepare(stream, "kbp_1", "SELECT $1", token, uint.MaxValue);
            for (var i = 0; i < (automatic ? 1 : 2); i++)
            { await PublicPreparedTests.ReadFrames(stream, "BDES", token); await stream.WriteAsync(PublicPreparedTests.PreparedValue("42"), token); }
        }, settings => { if (automatic) { settings.MaxAutoPrepare = 1; settings.AutoPrepareMinUsages = 2; } });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupHasAnIndependentBudgetAndPreservesLateSqlState(bool serverError)
    {
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection) { CommandTimeout = 1 };
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); closing.SetResult();
            if (serverError) { var error = await Assert.ThrowsAsync<KingbaseException>(reader.CloseAsync); Assert.Equal("23505", error.SqlState); }
            else await reader.CloseAsync();
            Assert.True(reader.IsClosed); Assert.Equal(ConnectionState.Open, connection.State);
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Header(1), token);
            await closing.Task.WaitAsync(token); await Task.Delay(1200, token);
            await stream.WriteAsync(serverError ? Join(Frame('E', Encoding.ASCII.GetBytes("SERROR\0C23505\0Mduplicate\0\0")), Ready()) : Tail(), token);
        });
    }

    [Fact]
    public async Task BrokenStateCallbackCannotHideWireFailureOrKeepCommandExecuting()
    {
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            connection.StateChange += (_, e) => { if (e.CurrentState == ConnectionState.Broken) throw new InvalidOperationException("secondary callback"); };
            await Assert.ThrowsAnyAsync<IOException>(() => reader.ReadAsync(token)); command.CommandText = "SELECT later";
        }, async (stream, token) =>
        { await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Header(1), token); });
    }

    [Fact]
    public async Task SequentialSmallSlicesReuseStagingInsteadOfAllocatingAFullSkipBufferPerCall()
    {
        var text = new string('x', 32768);
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(reader.Read());
            Assert.Equal(text.Length, reader.GetChars(0, 0, null, 0, 0)); var chars = new char[8];
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < text.Length; i += chars.Length) Assert.Equal(chars.Length, reader.GetChars(0, i, chars, 0, chars.Length));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 12 * 1024 * 1024); Assert.False(reader.Read());
        }, async (stream, token) =>
        { await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Row([text]), Tail()), token); });
    }

    [Fact]
    public async Task InvalidHeaderTakesPrecedenceOverHandshakeCallbackFailureAndDrains()
    {
        await Run(async (connection, token) =>
        {
            connection.Notice += (_, _) => throw new InvalidOperationException("callback failure");
            using var command = new KingbaseCommand("SELECT streamed", connection);
            await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token));
            Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal("after", await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('N', Encoding.ASCII.GetBytes("SNOTICE\0C00000\0Mnotice\0\0")), Description(1, 9004, 4), Tail()), token);
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Row(["after"]), Tail()), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcknowledgedAndUnknownBatchWritesKeepTheirDistinctStatuses(bool malformedValue)
    {
        await Run(async (connection, token) =>
        {
            using var batch = new KingbaseBatch(connection); batch.BatchCommands.Add(new("UPDATE data RETURNING value"));
            if (malformedValue)
            {
                using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(reader.Read());
                await Assert.ThrowsAsync<DecoderFallbackException>(() => reader.GetFieldValueAsync<string>(0, token));
                Assert.Equal(KingbaseBatchCommandStatus.OutcomeUnknown, batch.BatchCommands[0].ExecutionStatus);
            }
            else
            {
                await Assert.ThrowsAsync<OverflowException>(() => batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token));
                Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus); Assert.Equal(ConnectionState.Open, connection.State);
            }
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token);
            await stream.WriteAsync(malformedValue ? Join(Header(1), Frame('D', Join(new byte[] { 0, 1 }, Int32(1), new byte[] { 0xc3 }))) :
                Join(Frame('1'), Frame('2'), Frame('n'), Frame('C', Encoding.ASCII.GetBytes("UPDATE 2147483648\0")), Ready()), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrConnectionCloseCannotSendUnstartedBatchStatements(bool closeConnection)
    {
        await Run(async (connection, token) =>
        {
            using var batch = new KingbaseBatch(connection); batch.BatchCommands.Add(new("UPDATE first")); batch.BatchCommands.Add(new("UPDATE must_not_run"));
            using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
            if (closeConnection) connection.Close(); else { batch.Cancel(); reader.Close(); connection.Close(); }
            Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus);
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('n'), Frame('C', Encoding.ASCII.GetBytes("UPDATE 1\0")), Ready()), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Fact]
    public async Task LocalFailureInTheNextBatchItemTerminatesOnceAndKeepsLaterItemsUnsent()
    {
        await Run(async (connection, token) =>
        {
            using var batch = new KingbaseBatch(connection); foreach (var sql in new[] { "SELECT first", "SELECT :missing", "SELECT unsent" }) batch.BatchCommands.Add(new(sql));
            using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(reader.Read()); Assert.False(reader.Read());
            await Assert.ThrowsAsync<ArgumentException>(() => reader.NextResultAsync(token)); Assert.Equal(1, batch.FailedCommandIndex);
            Assert.Equal(KingbaseBatchCommandStatus.Failed, batch.BatchCommands[1].ExecutionStatus);
            Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[2].ExecutionStatus);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.NextResultAsync(token));
            reader.Close(); using var after = new KingbaseCommand("SELECT after", connection); Assert.Equal("after", await after.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Row(["first"]), Tail()), token);
            var request = await PublicExecutionTests.ReadExtended(stream, token); Assert.Contains("SELECT after\0", Encoding.UTF8.GetString(request[0]));
            await stream.WriteAsync(Join(Header(1), Row(["after"]), Tail()), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackFailureDrainsButServerErrorsTakePrecedence(bool serverError)
    {
        await Run(async (connection, token) =>
        {
            connection.Notice += (_, _) => throw new InvalidOperationException("callback failure");
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(reader.Read()); Assert.Equal("first", reader.GetString(0));
            if (serverError) { var error = await Assert.ThrowsAsync<KingbaseException>(() => reader.ReadAsync(token)); Assert.Equal("22012", error.SqlState); }
            else { var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(token)); Assert.Equal("callback failure", error.Message); }
            reader.Close(); using var after = new KingbaseCommand("SELECT after", connection); Assert.Equal("after", await after.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token);
            await stream.WriteAsync(Join(Header(1), Row(["first"]), Frame('N', Encoding.ASCII.GetBytes("SNOTICE\0C00000\0Mnotice\0\0")), Row(["second"]),
                serverError ? Join(Frame('E', Encoding.ASCII.GetBytes("SERROR\0C22012\0Mdivision\0\0")), Ready()) : Tail()), token);
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Row(["after"]), Tail()), token);
        });
    }

    [Fact]
    public async Task CallbacksCannotReenterCachedReaderOrChildState()
    {
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(reader.Read());
            var field = reader.GetTextReader(0); Assert.Equal('a', field.Peek()); var calls = 0;
            connection.Notice += (_, _) =>
            {
                calls++; Assert.Throws<InvalidOperationException>(() => field.Peek()); Assert.Throws<InvalidOperationException>(() => field.Dispose());
                Assert.Throws<InvalidOperationException>(() => reader.GetString(0)); Assert.Throws<InvalidOperationException>(() => reader.Close());
            };
            field.Dispose(); Assert.True(reader.Read()); Assert.Equal("second", reader.GetString(0)); Assert.Equal(1, calls); Assert.False(reader.Read());
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token);
            await stream.WriteAsync(Join(Header(1), Row(["abcdef"]), Frame('N', Encoding.ASCII.GetBytes("SNOTICE\0C00000\0Mnotice\0\0")), Row(["second"]), Tail()), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManyRowsAndLargeBinaryValuesDoNotUseAggregateBufferedLimits(bool manyRows)
    {
        var bytes = Enumerable.Range(0, 600_000).Select(i => (byte)(i % 251)).ToArray();
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            if (manyRows) { var count = 0; while (await reader.ReadAsync(token)) { Assert.Equal("value", reader.GetString(0)); count++; } Assert.Equal(1500, count); }
            else
            {
                Assert.True(reader.Read()); Assert.Equal(bytes, await reader.GetFieldValueAsync<byte[]>(0, token));
                Assert.False(reader.Read());
            }
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, manyRows ? 9004u : 9005u, -1)), token);
            if (manyRows) for (var i = 0; i < 1500; i++) await stream.WriteAsync(Row(["value"]), token);
            else await stream.WriteAsync(Row(["\\x" + Convert.ToHexString(bytes)]), token);
            await stream.WriteAsync(Tail(), token);
        });
    }

    [Theory]
    [InlineData(CommandBehavior.Default)]
    [InlineData(CommandBehavior.SingleRow)]
    [InlineData(CommandBehavior.SingleResult)]
    public async Task OrderedBatchesStreamLazilyAndCloseExecutesRemainingStatements(CommandBehavior flags)
    {
        var sent = 0;
        await Run(async (connection, token) =>
        {
            using var batch = new KingbaseBatch(connection); batch.BatchCommands.Add(new("SELECT first")); batch.BatchCommands.Add(new("UPDATE later"));
            using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess | flags, token);
            Assert.Equal(1, Volatile.Read(ref sent)); Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus);
            Assert.True(reader.Read()); Assert.Equal("first", reader.GetString(0));
            if (flags == CommandBehavior.Default)
            {
                Assert.False(reader.Read()); Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
                Assert.True(await reader.NextResultAsync(token)); Assert.Equal(0, reader.FieldCount); Assert.False(reader.Read()); Assert.False(reader.NextResult());
            }
            reader.Close(); Assert.Equal(2, sent); Assert.Null(batch.FailedCommandIndex);
            Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), c => Assert.Equal(KingbaseBatchCommandStatus.Succeeded, c.ExecutionStatus));
            Assert.Equal(2, batch.BatchCommands[1].RecordsAffected); Assert.Equal(2, reader.RecordsAffected);
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token); Interlocked.Increment(ref sent); await stream.WriteAsync(Join(Header(1), Row(["first"]), Tail()), token);
            await PublicExecutionTests.ReadExtended(stream, token); Interlocked.Increment(ref sent);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('n'), Frame('C', Encoding.ASCII.GetBytes("UPDATE 2\0")), Ready()), token);
        });
    }

    [Fact]
    public async Task LateBatchErrorsPreservePriorAcknowledgmentsAndFailedCommandIdentity()
    {
        await Run(async (connection, token) =>
        {
            using var batch = new KingbaseBatch(connection); foreach (var sql in new[] { "SELECT first", "SELECT failure", "SELECT unsent" }) batch.BatchCommands.Add(new(sql));
            using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(reader.Read()); Assert.False(reader.Read());
            Assert.True(reader.NextResult()); Assert.True(reader.Read()); Assert.Equal("partial", reader.GetString(0));
            var error = await Assert.ThrowsAsync<KingbaseException>(() => reader.ReadAsync(token)); Assert.Equal("22012", error.SqlState);
            Assert.Same(batch.BatchCommands[1], error.BatchCommand); Assert.Equal(1, batch.FailedCommandIndex);
            Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
            Assert.Equal(KingbaseBatchCommandStatus.Failed, batch.BatchCommands[1].ExecutionStatus); Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[2].ExecutionStatus);
            reader.Close(); using var after = new KingbaseCommand("SELECT after", connection); Assert.Equal("after", await after.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Row(["first"]), Tail()), token);
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Row(["partial"]), Frame('E', Encoding.ASCII.GetBytes("SERROR\0C22012\0Mdivision\0\0")), Ready()), token);
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Row(["after"]), Tail()), token);
        });
    }

    [Fact]
    public async Task LengthBeforePartialAccessKeepsTheMaterializedFieldReadableAndBytesIndependent()
    {
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(reader.Read());
            Assert.Equal(3, reader.GetBytes(0, 0, null, 0, 0)); var copy = reader.GetFieldValue<byte[]>(0); copy[0] = 255;
            Assert.Equal(new byte[] { 65, 0, 92 }, reader.GetFieldValue<byte[]>(0));
            var buffer = new byte[3]; Assert.Equal(3, reader.GetBytes(0, 0, buffer, 0, 3)); Assert.Equal(new byte[] { 65, 0, 92 }, buffer);
            Assert.Equal(4, reader.GetChars(1, 0, null, 0, 0)); var chars = new char[4]; Assert.Equal(4, reader.GetChars(1, 0, chars, 0, 4)); Assert.Equal("a\U0001F600z", new string(chars));
            Assert.False(reader.Read());
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token);
            var first = Description(1, 9005, -1); var second = Description(1, 9004, -1);
            var columns = Frame('T', Join(new byte[] { 0, 2 }, first[7..], second[7..]));
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), columns, Row(["A\\000\\\\", "a\U0001F600z"]), Tail()), token);
        });
    }

    [Fact]
    public async Task AdvancingColumnsInvalidatesEvenCachedFieldReaders()
    {
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(reader.Read());
            var field = reader.GetTextReader(0); Assert.Equal('a', field.Peek()); Assert.Equal('a', field.Read());
            Assert.Equal("second", reader.GetString(1)); Assert.Throws<InvalidOperationException>(() => field.Peek());
            Assert.Throws<InvalidOperationException>(() => field.Read()); field.Dispose(); field.Dispose(); Assert.False(reader.Read());
        }, async (stream, token) =>
        { await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(2), Row(["abcdef", "second"]), Tail()), token); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerCallCancellationBeforeReadingPreservesTheRow(bool fieldRead)
    {
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            if (fieldRead)
            {
                Assert.True(await reader.ReadAsync(token)); using var field = reader.GetTextReader(0);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await field.ReadAsync(new char[1].AsMemory(), canceled.Token));
                Assert.Equal("value", await field.ReadToEndAsync());
            }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(canceled.Token)); Assert.True(await reader.ReadAsync(token)); Assert.Equal("value", reader.GetString(0)); }
            Assert.False(await reader.ReadAsync(token)); Assert.Equal(ConnectionState.Open, connection.State);
        }, async (stream, token) =>
        { await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Row(["value"]), Tail()), token); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedUtf8AndNulRetireTheReader(bool nul)
    {
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(reader.Read());
            var field = reader.GetTextReader(0);
            if (nul) await Assert.ThrowsAsync<IOException>(() => field.ReadAsync(new char[2], 0, 2));
            else await Assert.ThrowsAsync<DecoderFallbackException>(() => field.ReadAsync(new char[2], 0, 2));
            Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        { await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Frame('D', Join(new byte[] { 0, 1 }, Int32(1), new byte[] { nul ? (byte)0 : (byte)0xc3 })), Tail()), token); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FieldReadersReturnAndReadAPrefixBeforeTheWholeFieldArrives(bool binary)
    {
        var prefixRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            Assert.True(await reader.ReadAsync(token));
            if (binary)
            {
                using var field = reader.GetStream(0); var bytes = new byte[1];
                Assert.Equal(1, await field.ReadAsync(bytes, 0, 1, token).WaitAsync(TimeSpan.FromSeconds(2))); Assert.Equal(65, bytes[0]);
                prefixRead.SetResult(); Assert.Equal(1, await field.ReadAsync(bytes, 0, 1, token)); Assert.Equal(66, bytes[0]);
                Assert.Equal(0, await field.ReadAsync(bytes, 0, 1, token));
            }
            else
            {
                using var field = reader.GetTextReader(0); var chars = new char[1];
                Assert.Equal(1, await field.ReadAsync(chars, 0, 1).WaitAsync(TimeSpan.FromSeconds(2))); Assert.Equal('\uFEFF', chars[0]);
                prefixRead.SetResult(); Assert.Equal("a\U0001F600z", await field.ReadToEndAsync());
            }
            Assert.False(await reader.ReadAsync(token));
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token);
            var first = binary ? Encoding.ASCII.GetBytes("\\x41") : Encoding.UTF8.GetBytes("\uFEFF");
            var rest = binary ? Encoding.ASCII.GetBytes("42") : Encoding.UTF8.GetBytes("a\U0001F600z");
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, binary ? 9005u : 9004u, -1),
                new byte[] { (byte)'D' }, Int32(10 + first.Length + rest.Length), new byte[] { 0, 1 }, Int32(first.Length + rest.Length), first), token);
            await prefixRead.Task.WaitAsync(token); await stream.WriteAsync(Join(rest, Tail()), token);
        });
    }

    [Fact]
    public async Task LargeScalarMaterializationDoesNotRetainTheBufferedExchangeLimit()
    {
        var text = new string('界', 400_000);
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            Assert.True(await reader.ReadAsync(token)); Assert.Equal(text, await reader.GetFieldValueAsync<string>(0, token));
            Assert.False(await reader.ReadAsync(token));
        }, async (stream, token) =>
        { await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Row([text]), Tail()), token); });
    }

    [Fact]
    public async Task PartialOffsetsAdvanceAndLengthAfterPartialReadConsumesTheRemainder()
    {
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); Assert.True(reader.Read());
            var buffer = new char[2]; Assert.Equal(2, reader.GetChars(0, 1, buffer, 0, 2)); Assert.Equal("bc", new string(buffer));
            Assert.Throws<InvalidOperationException>(() => reader.GetChars(0, 0, buffer, 0, 1));
            Assert.Equal(6, reader.GetChars(0, 0, null, 0, 0));
            Assert.Throws<InvalidOperationException>(() => reader.GetString(0));
            Assert.Equal(0, reader.GetChars(0, 6, buffer, 0, 1)); Assert.Equal("second", reader.GetString(1)); Assert.False(reader.Read());
        }, async (stream, token) =>
        { await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(2), Row(["abcdef", "second"]), Tail()), token); });
    }

    [Fact]
    public async Task ReturnsMetadataBeforeRowsAndRetainsForwardRowOwnership()
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(3, reader.FieldCount); published.SetResult();
            Assert.True(reader.HasRows); Assert.True(reader.HasRows);
            Assert.True(await reader.ReadAsync(token)); Assert.Equal("first", reader.GetString(0)); Assert.Equal("first", reader.GetString(0));
            Assert.True(await reader.IsDBNullAsync(1, token)); Assert.Equal("last", reader.GetString(2));
            Assert.Throws<InvalidOperationException>(() => reader.GetString(0));
            Assert.False(await reader.ReadAsync(token)); Assert.True(reader.HasRows);
            Assert.False(await reader.NextResultAsync(token)); Assert.Equal(0, reader.FieldCount);
            Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar());
            reader.Close(); Assert.Equal("after", await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Header(3), token);
            await published.Task.WaitAsync(token); await stream.WriteAsync(Join(Row(["first", null, "last"]), Tail()), token);
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Row(["after"]), Tail()), token);
        });
    }

    [Fact]
    public async Task DisposingPublishedCommandLeavesReaderUsableAndCloseDrainsUnreadRows()
    {
        await Run(async (connection, token) =>
        {
            var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token); command.Dispose();
            Assert.True(await reader.ReadAsync(token)); Assert.Equal("one", reader.GetString(0));
            reader.Close(); using var after = new KingbaseCommand("SELECT after", connection);
            Assert.Equal("after", await after.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token);
            await stream.WriteAsync(Join(Header(1), Row(["one"]), Row(["unread"]), Tail()), token);
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Join(Header(1), Row(["after"]), Tail()), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderLifetimeCancellationRetiresThePhysicalExchange(bool cancelCommand)
    {
        await Run(async (connection, token) =>
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var command = new KingbaseCommand("SELECT streamed", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, caller.Token);
            if (cancelCommand) command.Cancel(); else caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(token));
            Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Header(1), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Fact]
    public async Task DeadlineDuringApplicationPauseAllowsStateChangeCloseReentry()
    {
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT streamed", connection) { CommandTimeout = 1 };
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.StateChange += (_, e) => { if (e.CurrentState == ConnectionState.Broken) { reader.Close(); closed.TrySetResult(); } };
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(4), token); Assert.True(reader.IsClosed);
        }, async (stream, token) =>
        {
            await PublicExecutionTests.ReadExtended(stream, token); await stream.WriteAsync(Header(1), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    internal static byte[] Header(int columns) => Join(Frame('1'), Frame('2'), Description(columns, 9004, -1));
    internal static byte[] Tail() => Join(Frame('C', Encoding.ASCII.GetBytes("SELECT 1\0")), Ready());
    internal static async Task Run(Func<KingbaseConnection, CancellationToken, Task> clientAction, Func<Stream, CancellationToken, Task> serverAction,
        Action<KingbaseConnectionStringBuilder>? configure = null)
    {
        using var listener = PublicExecutionTests.Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var settings = PublicExecutionTests.Settings(listener); configure?.Invoke(settings);
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token); await serverAction(stream, guard.Token);
        }, guard.Token);
        try { await connection.OpenAsync(guard.Token); await clientAction(connection, guard.Token); }
        finally { connection.Close(); guard.CancelAfter(TimeSpan.FromSeconds(2)); }
        await peer;
    }
}
