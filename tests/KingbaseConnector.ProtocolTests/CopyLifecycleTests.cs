using System.Data;
using System.Text;
using static KingbaseConnector.ProtocolTests.CopyExecutionTests;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class CopyLifecycleTests
{
    [Theory]
    [InlineData("close")]
    [InlineData("cancel")]
    [InlineData("timeout")]
    public async Task BufferedTextExportRejectsAccessAfterItsLifetimeEnds(string termination)
    {
        await Run(async (connection, token) =>
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var reader = await connection.BeginTextExportAsync("COPY items TO STDOUT", lifetime.Token);
            Assert.Equal('a', reader.Read());
            await Terminate(connection, lifetime, termination, token);
            AssertTerminated(termination, () => reader.Peek());
            AssertTerminated(termination, () => reader.Read());
            AssertTerminated(termination, () => reader.Read(new char[1], 0, 1));
            AssertTerminated(termination, () => reader.ReadLine());
            AssertTerminated(termination, () => reader.ReadToEnd());
            await AssertTerminatedAsync(termination, () => reader.ReadAsync(new char[1], 0, 1));
            await AssertTerminatedAsync(termination, () => reader.ReadAsync(new char[1].AsMemory(), token).AsTask());
            Assert.Equal(termination == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token);
            await stream.WriteAsync(Join(Response(false, false), Frame('d', Encoding.UTF8.GetBytes("ab\n"))), token);
        }, commandTimeout: termination == "timeout" ? 1 : 0);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("cancel")]
    [InlineData("timeout")]
    public async Task BufferedTextImportRejectsAccessAfterItsLifetimeEnds(string termination)
    {
        await Run(async (connection, token) =>
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var writer = await connection.BeginTextImportAsync("COPY items FROM STDIN", lifetime.Token);
            writer.Write("pending");
            await Terminate(connection, lifetime, termination, token);
            AssertTerminated(termination, () => writer.Write('a'));
            AssertTerminated(termination, () => writer.Write("b"));
            AssertTerminated(termination, () => writer.Write(new[] { 'c' }, 0, 1));
            await AssertTerminatedAsync(termination, () => writer.WriteAsync("d"));
            await AssertTerminatedAsync(termination, () => writer.WriteAsync("e".AsMemory(), token));
            await AssertTerminatedAsync(termination, () => writer.WriteLineAsync("f".AsMemory(), token));
            AssertTerminated(termination, () => writer.Flush());
            await AssertTerminatedAsync(termination, () => writer.FlushAsync());
            Assert.Null(writer.RowsCopied);
            // Unwinding a using block must not flush dead buffered text and mask the original error.
            writer.Dispose(); await writer.DisposeAsync(); await writer.CancelAsync(token); writer.Dispose();
            Assert.Equal(termination == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, false), token);
        }, commandTimeout: termination == "timeout" ? 1 : 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextImportUsingDoesNotReplaceTheOriginalCancellation(bool asynchronous)
    {
        await Run(async (connection, token) =>
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                if (asynchronous)
                {
                    await using var writer = await connection.BeginTextImportAsync("COPY items FROM STDIN", token);
                    writer.Write("pending"); connection.Close(); writer.Write('x');
                }
                else
                {
                    using var writer = await connection.BeginTextImportAsync("COPY items FROM STDIN", token);
                    writer.Write("pending"); connection.Close(); writer.Write('x');
                }
            });
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, false), token);
        });
    }

    [Fact]
    public async Task AcknowledgedTextExportRemainsAtEofAfterItsConnectionCloses()
    {
        await Run(async (connection, token) =>
        {
            using var reader = await connection.BeginTextExportAsync("COPY items TO STDOUT", token);
            Assert.Equal("ab", await reader.ReadToEndAsync()); connection.Close();
            Assert.Equal(-1, reader.Peek()); Assert.Equal(-1, reader.Read()); Assert.Equal("", await reader.ReadToEndAsync());
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token);
            await stream.WriteAsync(Join(Response(false, false), Frame('d', Encoding.UTF8.GetBytes("ab")), Frame('c'), Tail(1)), token);
        });
    }

    static async Task Terminate(KingbaseConnection connection, CancellationTokenSource lifetime, string termination, CancellationToken token)
    {
        if (termination == "close") connection.Close();
        else if (termination == "cancel") lifetime.Cancel();
        else
        {
            var broken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.StateChange += (_, args) => { if (args.CurrentState == ConnectionState.Broken) broken.TrySetResult(); };
            if (connection.State != ConnectionState.Broken) await broken.Task.WaitAsync(token);
        }
    }

    static void AssertTerminated(string termination, Action action)
    {
        if (termination == "timeout") Assert.Throws<TimeoutException>(action);
        else Assert.ThrowsAny<OperationCanceledException>(action);
    }

    static async Task AssertTerminatedAsync(string termination, Func<Task> action)
    {
        if (termination == "timeout") await Assert.ThrowsAsync<TimeoutException>(action);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
    }
}
