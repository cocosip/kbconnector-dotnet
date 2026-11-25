using System.Data;
using System.Text;
using static KingbaseConnector.ProtocolTests.CopyExecutionTests;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class CopyFailureTests
{
    [Theory]
    [InlineData("format")]
    [InlineData("columns")]
    [InlineData("both")]
    [InlineData("premature-ready")]
    [InlineData("count")]
    [InlineData("done-payload")]
    public async Task MalformedCopyExchangesRetireWithoutClaimingCompletedRows(string kind)
    {
        await Run(async (connection, token) =>
        {
            if (kind is "count" or "done-payload")
            {
                using var copy = await connection.BeginRawBinaryCopyAsync("COPY items TO STDOUT (FORMAT BINARY)", token);
                await Assert.ThrowsAsync<IOException>(() => copy.ReadAsync(new byte[9], 0, 9, token)); Assert.Null(copy.RowsCopied);
            }
            else await Assert.ThrowsAsync<IOException>(() => connection.BeginRawBinaryCopyAsync("COPY items TO STDOUT (FORMAT BINARY)", token));
            Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token);
            var frames = kind switch
            {
                "format" => Frame('H', [2, 0, 0]), "columns" => Frame('H', [1, 0, 2, 0, 1]), "both" => Frame('W', [1, 0, 0]),
                "premature-ready" => Ready(), "done-payload" => Join(Response(false, true), Frame('c', [1])),
                _ => Join(Response(false, true), Frame('c'), Frame('C', Encoding.ASCII.GetBytes("COPY -1\0")), Ready())
            };
            await stream.WriteAsync(frames, token);
        });
    }

    [Fact]
    public async Task AWrongImportFormatIsCancelledAndKeepsTheSessionUsable()
    {
        await Run(async (connection, token) =>
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN", token)); Assert.Equal(ConnectionState.Open, connection.State);
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, false), token); Assert.Equal('f', (await Frontend(stream, token)).Code);
            await stream.WriteAsync(Error("57014"), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Fact]
    public async Task TextEncodingFailureCancelsInsteadOfCompletingTruncatedData()
    {
        await Run(async (connection, token) =>
        {
            using var writer = await connection.BeginTextImportAsync("COPY items FROM STDIN", token); writer.Write('\uD800');
            await Assert.ThrowsAsync<EncoderFallbackException>(() => writer.CompleteAsync(token)); Assert.Null(writer.RowsCopied);
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, false), token); Assert.Equal('f', (await Frontend(stream, token)).Code);
            await stream.WriteAsync(Error("57014"), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCanceledReadsAndCancelLeaveExportAvailableForLaterEof(bool cancelOperation)
    {
        await Run(async (connection, token) =>
        {
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY items TO STDOUT (FORMAT BINARY)", token); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            if (cancelOperation) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.CancelAsync(cancel.Token));
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.ReadAsync(new byte[1], 0, 1, cancel.Token));
            using var bytes = new MemoryStream(); await copy.CopyToAsync(bytes, 19, token); Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray()); Assert.Equal(1UL, copy.RowsCopied);
        }, async (stream, token) =>
        { await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(Response(false, true), Frame('d', [1, 2, 3]), Frame('c'), Tail(1)), token); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightCancellationAndConnectionCloseAbortPendingExportWithoutDirtyReuse(bool close)
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY items TO STDOUT (FORMAT BINARY)", token); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var read = copy.ReadAsync(new byte[9], 0, 9, cancel.Token); await requested.Task.WaitAsync(token);
            if (close) connection.Close(); else cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read); Assert.Equal(close ? ConnectionState.Closed : ConnectionState.Broken, connection.State); Assert.Null(copy.RowsCopied);
        }, async (stream, token) => { await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(false, true), token); requested.SetResult(); });
    }

    [Fact]
    public async Task CommandTimeoutCoversCopyLifetimeAndRetiresIdleExport()
    {
        await Run(async (connection, token) =>
        {
            var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.StateChange += (_, args) => { if (args.CurrentState == ConnectionState.Broken) expired.TrySetResult(); };
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY items TO STDOUT (FORMAT BINARY)", token);
            await expired.Task.WaitAsync(token); await Assert.ThrowsAsync<TimeoutException>(() => copy.ReadAsync(new byte[1], 0, 1, token)); Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) => { await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(false, true), token); }, commandTimeout: 1);
    }

    [Fact]
    public async Task ACopyCallbackCannotCompleteOrCancelItsOwnImport()
    {
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var callbackDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token);
            connection.Notice += (_, _) => { Assert.Throws<InvalidOperationException>(() => copy.Complete()); Assert.Throws<InvalidOperationException>(() => copy.Cancel()); callbackDone.SetResult(); };
            dispatch.SetResult(); await callbackDone.Task.WaitAsync(token); Assert.Equal(1UL, await copy.CompleteAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token); await dispatch.Task.WaitAsync(token); await stream.WriteAsync(NoticeTests.Notice(), token);
            Assert.Equal('c', (await Frontend(stream, token)).Code); await stream.WriteAsync(Tail(1), token);
        });
    }

    [Fact]
    public async Task CopyCancellationCleanupHasItsOwnDeadlineWhenCommandsAreUnlimited()
    {
        await Run(async (connection, token) =>
        {
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token);
            await Assert.ThrowsAsync<TimeoutException>(() => copy.CancelAsync(token)); Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) => { await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token); Assert.Equal('f', (await Frontend(stream, token)).Code); }, commandTimeout: 0, cleanupTimeout: 1);
    }

    [Fact]
    public async Task ImportCleanupCanOutliveTheStoppedApplicationDeadline()
    {
        await Run(async (connection, token) =>
        {
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token); await copy.CancelAsync(token); Assert.Equal(ConnectionState.Open, connection.State);
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token); Assert.Equal('f', (await Frontend(stream, token)).Code);
            await Task.Delay(1300, token); await stream.WriteAsync(Error("57014"), token);
        }, commandTimeout: 1, cleanupTimeout: 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedCallbackDisposalCannotPoisonRawOrTextExport(bool text)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            using var raw = text ? null : await connection.BeginRawBinaryCopyAsync("COPY items TO STDOUT (FORMAT BINARY)", token);
            using var reader = text ? await connection.BeginTextExportAsync("COPY items TO STDOUT", token) : null;
            connection.Notice += (_, _) => Assert.Throws<InvalidOperationException>(() => { if (reader is not null) reader.Dispose(); else raw!.Dispose(); }); release.SetResult();
            if (reader is not null) Assert.Equal("abc", await reader.ReadToEndAsync());
            else { var buffer = new byte[1]; Assert.Equal(1, await raw!.ReadAsync(buffer, 0, 1, token)); using var remaining = new MemoryStream(); await raw.CopyToAsync(remaining, 19, token); Assert.Equal(2, remaining.Length); }
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(false, !text), token); await release.Task.WaitAsync(token);
            await stream.WriteAsync(Join(NoticeTests.Notice(), Frame('d', Encoding.ASCII.GetBytes("abc")), Frame('c'), Tail(1)), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        }, commandTimeout: 0);
    }

    [Fact]
    public async Task CleanupServerErrorsAreNotReplacedByTheStoppedApplicationDeadline()
    {
        await Run(async (connection, token) =>
        {
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token); var error = await Assert.ThrowsAsync<KingbaseException>(() => copy.CancelAsync(token)); Assert.Equal("23505", error.SqlState);
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token); await Frontend(stream, token); await Task.Delay(1300, token); await stream.WriteAsync(Error("23505"), token);
            await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        }, commandTimeout: 1);
    }

    [Fact]
    public async Task KnownFormatMismatchPrecedesCallbackFailureAfterSuccessfulCleanup()
    {
        await Run(async (connection, token) =>
        {
            connection.Notice += (_, _) => throw new InvalidOperationException("callback");
            await Assert.ThrowsAsync<NotSupportedException>(() => connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN", token));
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(NoticeTests.Notice(), Response(true, false)), token); Assert.Equal('f', (await Frontend(stream, token)).Code);
            await stream.WriteAsync(Error("57014"), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Fact]
    public async Task APreexistingServerCancellationIsNotSwallowedWhenNoCopyFailWasSent()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token); release.SetResult();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var exchange = typeof(KingbaseCopyStream).GetField("_exchange", flags)!.GetValue(copy)!; var completion = (Task)exchange.GetType().GetField("_completion", flags)!.GetValue(exchange)!;
            await Assert.ThrowsAsync<KingbaseConnector.Protocol.PgQueryException>(() => completion);
            var error = await Assert.ThrowsAsync<KingbaseException>(() => copy.CancelAsync(token)); Assert.Equal("57014", error.SqlState);
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token); await release.Task.WaitAsync(token); await stream.WriteAsync(Error("57014"), token);
            await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Fact]
    public async Task AnErrorReceivedBeforeClientCancelKeepsItsIdentityWhileReadyIsPending()
    {
        var errorRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            connection.Notice += (_, _) => errorRead.TrySetResult(); using var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token);
            await errorRead.Task.WaitAsync(token); var cancel = copy.CancelAsync(token); ready.SetResult(); var error = await Assert.ThrowsAsync<KingbaseException>(() => cancel); Assert.Equal("57014", error.SqlState);
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(Response(true, true), Frame('E', Encoding.ASCII.GetBytes("SERROR\0C57014\0Mserver-timeout\0\0")), NoticeTests.Notice()), token);
            await ready.Task.WaitAsync(token); await stream.WriteAsync(Ready(), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownReceiveFaultsCannotBeReportedAsSuccessfulImportCancellation(bool dispose)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token); release.SetResult();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic; var exchange = typeof(KingbaseCopyStream).GetField("_exchange", flags)!.GetValue(copy)!;
            var completion = (Task)exchange.GetType().GetField("_completion", flags)!.GetValue(exchange)!; var receive = await Assert.ThrowsAsync<IOException>(() => completion);
            var error = dispose ? Assert.Throws<IOException>(() => copy.Dispose()) : await Assert.ThrowsAsync<IOException>(() => copy.CancelAsync(token));
            Assert.Same(receive, error); Assert.Equal(ConnectionState.Broken, connection.State); copy.Dispose();
        }, async (stream, token) => { await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token); await release.Task.WaitAsync(token); await stream.WriteAsync(Frame('w'), token); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCannotClaimSuccessWhileImportCompletionIsUnacknowledged(bool text)
    {
        var completing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            if (text)
            {
                using var writer = await connection.BeginTextImportAsync("COPY items FROM STDIN", token); var result = writer.CompleteAsync(token); await completing.Task.WaitAsync(token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CancelAsync(token)); release.SetResult(); Assert.Equal(1UL, await result);
            }
            else
            {
                using var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token); var result = copy.CompleteAsync(token); await completing.Task.WaitAsync(token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => copy.CancelAsync(token)); Assert.Throws<InvalidOperationException>(() => copy.Dispose()); release.SetResult(); Assert.Equal(1UL, await result);
            }
            Assert.Equal(ConnectionState.Open, connection.State);
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, !text), token); Assert.Equal('c', (await Frontend(stream, token)).Code); completing.SetResult();
            await release.Task.WaitAsync(token); await stream.WriteAsync(Tail(1), token);
        });
    }

    [Fact]
    public async Task TextReaderRejectedConcurrentDisposePreservesItsPendingReadAndCleanupPath()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            using var reader = await connection.BeginTextExportAsync("COPY items TO STDOUT", token); var buffer = new char[1]; var reading = reader.ReadAsync(buffer, 0, 1); Assert.False(reading.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => reader.Dispose()); release.SetResult(); Assert.Equal(1, await reading); Assert.Equal('a', buffer[0]); Assert.Equal("bc", await reader.ReadToEndAsync());
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(false, false), token); await release.Task.WaitAsync(token); await stream.WriteAsync(Join(Frame('d', Encoding.ASCII.GetBytes("abc")), Frame('c'), Tail(1)), token);
            await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        }, commandTimeout: 0);
    }
}
