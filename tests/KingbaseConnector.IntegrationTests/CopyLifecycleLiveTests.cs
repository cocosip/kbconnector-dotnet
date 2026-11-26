using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class CopyLifecycleLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, "close")]
    [InlineData(KingbaseCompatibilityMode.Oracle, "close")]
    [InlineData(KingbaseCompatibilityMode.MySql, "close")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "close")]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, "cancel")]
    [InlineData(KingbaseCompatibilityMode.Oracle, "cancel")]
    [InlineData(KingbaseCompatibilityMode.MySql, "cancel")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "cancel")]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, "timeout")]
    [InlineData(KingbaseCompatibilityMode.Oracle, "timeout")]
    [InlineData(KingbaseCompatibilityMode.MySql, "timeout")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "timeout")]
    public Task TextBuffersCannotOutliveTheirCopyOperation(KingbaseCompatibilityMode mode, string termination) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.CommandTimeout = termination == "timeout" ? 1 : 10;
        using (var connection = new KingbaseConnection(settings.ConnectionString))
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            await connection.OpenAsync(token);
            using var reader = await connection.BeginTextExportAsync("COPY (SELECT 'ab'::pg_catalog.text) TO STDOUT", lifetime.Token);
            Assert.Equal('a', reader.Read());
            await Terminate(connection, lifetime, termination, token);
            AssertTerminated(termination, () => reader.Read());
            AssertTerminated(termination, () => reader.Peek());
        }
        using (var connection = new KingbaseConnection(settings.ConnectionString))
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            await connection.OpenAsync(token);
            using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_lifetime (v pg_catalog.text)", connection)) await create.ExecuteNonQueryAsync(token);
            var writer = await connection.BeginTextImportAsync("COPY kb_copy_lifetime FROM STDIN", lifetime.Token);
            writer.Write("pending\n");
            await Terminate(connection, lifetime, termination, token);
            AssertTerminated(termination, () => writer.Write("discarded\n"));
            AssertTerminated(termination, () => writer.Flush());
            writer.Dispose(); await writer.DisposeAsync(); Assert.Null(writer.RowsCopied);
        }
    });

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
        Assert.Equal(termination == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
    }

    static void AssertTerminated(string termination, Action action)
    {
        if (termination == "timeout") Assert.Throws<TimeoutException>(action);
        else Assert.ThrowsAny<OperationCanceledException>(action);
    }
}
