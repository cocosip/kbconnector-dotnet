using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class NoticeLiveTests
{
    const string WarningSql = "DO $kb_notice$ BEGIN RAISE WARNING 'kb_notice_通知😀' USING ERRCODE='01000', DETAIL='kb_detail', HINT='kb_hint'; END; $kb_notice$";

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task PreparedNoticesExposeExpectedFieldsAndRejectCallbackReentry(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); var calls = 0;
        connection.Notice += (sender, args) =>
        {
            calls++; Assert.Same(connection, sender); var notice = args.Notice;
            Assert.Equal("01000", notice.SqlState); Assert.Equal("kb_notice_通知😀", notice.MessageText); Assert.Equal("kb_detail", notice.Detail); Assert.Equal("kb_hint", notice.Hint);
            Assert.Equal("WARNING", notice.InvariantSeverity ?? notice.Severity); Assert.DoesNotContain("kb_notice_", notice.ToString());
            using var nested = new KingbaseCommand("SELECT 99", connection); Assert.Throws<InvalidOperationException>(() => nested.ExecuteScalar()); Assert.Throws<InvalidOperationException>(() => connection.Wait(0));
        };
        using var command = new KingbaseCommand(WarningSql, connection); await command.PrepareAsync(token); Assert.Equal(0, calls);
        await command.ExecuteNonQueryAsync(token); await command.ExecuteNonQueryAsync(token); Assert.Equal(2, calls);
        using var next = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await next.ExecuteScalarAsync(token)); Assert.Equal(ConnectionState.Open, connection.State);
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task HandlerFailurePreservesTheSessionAndServerOrProtocolErrorsKeepPrecedence(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        var failure = new InvalidOperationException("notice callback"); var calls = 0; connection.Notice += (_, _) => { calls++; throw failure; };
        using var command = new KingbaseCommand(WarningSql, connection);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteNonQueryAsync(token))); Assert.Equal(1, calls);
        command.CommandText = "DO $kb_notice$ BEGIN RAISE NOTICE 'before_error'; RAISE EXCEPTION 'kb_expected' USING ERRCODE='22012'; END; $kb_notice$";
        if (mode == KingbaseCompatibilityMode.SqlServer)
        {
            // This profile sends Error/CommandComplete/Ready; strict error-tail synchronization remains open.
            await Assert.ThrowsAsync<IOException>(() => command.ExecuteNonQueryAsync(token)); Assert.Equal(ConnectionState.Broken, connection.State);
            connection.Close(); await connection.OpenAsync(token);
        }
        else { var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteNonQueryAsync(token)); Assert.Equal("22012", error.SqlState); }
        Assert.Equal(2, calls);
        command.CommandText = "SELECT 42"; Assert.Equal(42, await command.ExecuteScalarAsync(token)); Assert.Equal(ConnectionState.Open, connection.State);
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task PooledNoticesBelongToTheCurrentLogicalBorrower(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1; using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build(); var counts = new int[2]; int? pid = null;
        for (var lease = 0; lease < 2; lease++)
        {
            using var connection = await source.OpenConnectionAsync(token); var owner = lease;
            connection.Notice += (_, _) => counts[owner]++;
            using var command = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", connection); var current = (int)(await command.ExecuteScalarAsync(token))!;
            if (pid is not null) Assert.Equal(pid, current); else pid = current;
            command.CommandText = WarningSql; await command.ExecuteNonQueryAsync(token); Assert.Equal(1, counts[owner]); await connection.CloseAsync(); Assert.Equal(1, counts[owner]);
        }
        Assert.Equal(new[] { 1, 1 }, counts);
    });
}
