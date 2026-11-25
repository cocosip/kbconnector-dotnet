using System.Data;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.PublicPreparedTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class DataAdapterBatchExecutionTests
{
    [Fact]
    public Task PublicBatchProbeRetainsSuccessfulAndUnsentRowsAcrossGroups() => KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(adapterBatch: true);

    [Theory]
    [InlineData("timeout")]
    [InlineData("disconnect")]
    [InlineData("close")]
    public async Task InterruptedBatchPreservesFirstAcknowledgmentAndDistinguishesUnknownFromUnsent(string interruption)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString); var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Inserted(), guard.Token);
            await ReadExtended(stream, guard.Token); reached.SetResult();
            if (interruption != "disconnect") await Retired(stream, guard.Token);
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var table = Rows(); using var command = Insert(connection); command.CommandTimeout = interruption == "timeout" ? 1 : 0;
        using var adapter = new KingbaseDataAdapter { InsertCommand = command, UpdateBatchSize = 0 }; var affected = -1;
        adapter.RowUpdated += (_, args) => affected = args.RecordsAffected;
        var operation = Task.Run(() => adapter.Update(table)); await reached.Task.WaitAsync(guard.Token); if (interruption == "close") connection.Close();
        if (interruption == "timeout") await Assert.ThrowsAsync<TimeoutException>(async () => await operation.WaitAsync(guard.Token));
        else if (interruption == "close") await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation.WaitAsync(guard.Token));
        else await Assert.ThrowsAnyAsync<IOException>(async () => await operation.WaitAsync(guard.Token));
        Assert.Equal(1, affected); Assert.Equal(DataRowState.Unchanged, table.Rows[0].RowState); Assert.False(table.Rows[0].HasErrors);
        Assert.Equal(DataRowState.Added, table.Rows[1].RowState); Assert.Contains("unknown", table.Rows[1].RowError);
        Assert.Equal(DataRowState.Added, table.Rows[2].RowState); Assert.Contains("not executed", table.Rows[2].RowError);
        Assert.Equal(interruption == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostAcknowledgmentValidationFailurePreservesEveryConfirmedWrite(bool finalAcknowledgment)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Inserted(), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 8080, -1), Row(["{1}"]), Frame('C', Encoding.UTF8.GetBytes("INSERT 0 1\0")), Ready()), guard.Token);
            var recovery = await ReadExtended(stream, guard.Token); Assert.Contains("SELECT 42", Encoding.UTF8.GetString(recovery[0])); await stream.WriteAsync(Value("42"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var table = Rows(); if (finalAcknowledgment) table.Rows.RemoveAt(2); using var command = Insert(connection); using var adapter = new KingbaseDataAdapter { InsertCommand = command, UpdateBatchSize = 0 };
        adapter.RowUpdating += (_, args) => { if ((int)args.Row["id"] == 2) args.Command!.CommandText += " RETURNING unsupported_array"; };
        var affected = -1; adapter.RowUpdated += (_, args) => affected = args.RecordsAffected;
        Assert.Throws<NotSupportedException>(() => adapter.Update(table)); Assert.Equal(2, affected); Assert.Equal(DataRowState.Unchanged, table.Rows[0].RowState);
        Assert.Equal(DataRowState.Unchanged, table.Rows[1].RowState); Assert.False(table.Rows[1].HasErrors);
        if (!finalAcknowledgment) { Assert.Equal(DataRowState.Added, table.Rows[2].RowState); Assert.Contains("not executed", table.Rows[2].RowError); }
        using var query = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await query.ExecuteScalarAsync(guard.Token)); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("continue")]
    [InlineData("replace")]
    [InlineData("clear")]
    [InlineData("policy")]
    public async Task PostAcknowledgedHandlersCanSuppressOrReplaceTheReportedDiagnostic(string action)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Inserted(), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 8080, -1), Row(["{1}"]), Frame('C', Encoding.UTF8.GetBytes("INSERT 0 1\0")), Ready()), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var table = Rows(); table.Rows.RemoveAt(2); using var command = Insert(connection); using var adapter = new KingbaseDataAdapter { InsertCommand = command, UpdateBatchSize = 0, ContinueUpdateOnError = action == "policy" };
        var replacement = new InvalidOperationException("replacement diagnostic"); var seen = 0;
        adapter.RowUpdated += (_, args) =>
        {
            seen++; Assert.Equal(UpdateStatus.ErrorsOccurred, args.Status); Assert.IsType<NotSupportedException>(args.Errors);
            if (action == "continue") args.Status = UpdateStatus.Continue;
            if (action == "replace") args.Errors = replacement;
            if (action == "clear") args.Errors = null;
        };
        if (action == "replace") Assert.Same(replacement, Assert.Throws<InvalidOperationException>(() => adapter.Update(table))); else Assert.Equal(2, adapter.Update(table));
        Assert.Equal(1, seen); Assert.All(table.Rows.Cast<DataRow>(), row => { Assert.Equal(DataRowState.Unchanged, row.RowState); Assert.False(row.HasErrors); });
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ExpiredSharedDeadlineRejectsDispatchWithoutWaitingForATimer()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var batch = new KingbaseBatch(connection) { Timeout = 0 }; batch.BatchCommands.Add(new("INSERT INTO public.items VALUES(1)")); batch.SetSharedDeadline(System.Diagnostics.Stopwatch.StartNew(), 0);
        Assert.Throws<TimeoutException>(() => batch.ExecuteNonQuery()); Assert.Equal(-1, batch.BatchCommands[0].RecordsAffected); Assert.Equal(ConnectionState.Open, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    static byte[] Inserted() => Join(Frame('1'), Frame('2'), Frame('n'), Frame('C', Encoding.UTF8.GetBytes("INSERT 0 1\0")), Ready());
    static async Task Retired(Stream stream, CancellationToken token)
    { try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); } catch (IOException) { } }
    static DataTable Rows() { var table = new DataTable(); table.Columns.Add("id", typeof(int)); table.Rows.Add(1); table.Rows.Add(2); table.Rows.Add(3); return table; }
    static KingbaseCommand Insert(KingbaseConnection connection)
    {
        var command = new KingbaseCommand("INSERT INTO public.items VALUES(@id)", connection) { UpdatedRowSource = UpdateRowSource.None };
        command.Parameters.Add(new KingbaseParameter { ParameterName = "id", DbType = DbType.Int32, SourceColumn = "id" }); return command;
    }
}
