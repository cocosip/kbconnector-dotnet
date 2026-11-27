using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.IntegrationTests;

// C15: the pool probes idle sessions with SQL keepalive, and a failed probe retires the session.
public sealed class KeepAliveLiveTests
{
    // The default pruning interval is longer than Keep Alive, which is what makes the heartbeat cadence observable.
    const string Idle = "Maximum Pool Size=1;Pooling=true";

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task KeepAliveProbesAnIdlePooledSession(KingbaseCompatibilityMode mode) =>
        Measure(mode, keepAlive: 1, maxIdleSeconds: 3);

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task WithoutKeepAliveAnIdlePooledSessionIsNotProbed(KingbaseCompatibilityMode mode) =>
        Measure(mode, keepAlive: 0, minIdleSeconds: 3.5);

    static Task Measure(KingbaseCompatibilityMode mode, int keepAlive, double maxIdleSeconds = 0, double minIdleSeconds = 0) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            var pooled = Pooled(settings, keepAlive, Idle);
            await using var connection = new KingbaseConnection(pooled);
            await connection.OpenAsync(token);
            var pid = Convert.ToInt64(await Scalar(connection, "SELECT pg_catalog.pg_backend_pid()", token));
            await connection.CloseAsync();
            await Task.Delay(TimeSpan.FromSeconds(6), token);
            var idleSeconds = await IdleSeconds(settings, pid, token);
            if (maxIdleSeconds > 0) Assert.True(idleSeconds < maxIdleSeconds, $"Last query was {idleSeconds:F1}s ago; keepalive did not run.");
            if (minIdleSeconds > 0) Assert.True(idleSeconds > minIdleSeconds, $"Last query was {idleSeconds:F1}s ago; a disabled keepalive still queried.");
        });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ADeadPooledSessionIsRetiredAndReplaced(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            var pooled = Pooled(settings, 1, Idle);
            long pid;
            await using (var first = new KingbaseConnection(pooled))
            {
                await first.OpenAsync(token);
                pid = Convert.ToInt64(await Scalar(first, "SELECT pg_catalog.pg_backend_pid()", token));
            }
            await Terminate(settings, pid, token);
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            await using var replacement = new KingbaseConnection(pooled);
            await replacement.OpenAsync(token);
            var next = Convert.ToInt64(await Scalar(replacement, "SELECT pg_catalog.pg_backend_pid()", token));
            Assert.NotEqual(pid, next);
            Assert.Equal(1, await Scalar(replacement, "SELECT 1", token));
        });

    // The observer connection never pools, so it cannot disturb the pool under test.
    static async Task<double> IdleSeconds(KingbaseConnectionStringBuilder settings, long pid, CancellationToken token)
    {
        await using var observer = new KingbaseConnection(Pooled(settings, 0, "Pooling=false"));
        await observer.OpenAsync(token);
        // The supplied server exposes the activity view under a different name per compatibility mode.
        foreach (var view in new[] { "pg_catalog.pg_stat_activity", "pg_catalog.sys_stat_activity", "sys_stat_activity" })
        {
            await using var command = new KingbaseCommand(
                "SELECT pg_catalog.date_part('epoch', pg_catalog.now() - query_start)::float8 FROM " + view + " WHERE pid = :pid", observer);
            command.Parameters.Add(new KingbaseParameter("pid", (int)pid) { DataTypeName = "pg_catalog.int4" });
            try { return Convert.ToDouble(await command.ExecuteScalarAsync(token)); }
            catch (KingbaseException ex) when (ex.SqlState == "42P01") { }
        }
        throw new InvalidOperationException("No activity view is available on this server.");
    }

    static async Task Terminate(KingbaseConnectionStringBuilder settings, long pid, CancellationToken token)
    {
        await using var observer = new KingbaseConnection(Pooled(settings, 0, "Pooling=false"));
        await observer.OpenAsync(token);
        await using var command = new KingbaseCommand("SELECT pg_catalog.pg_terminate_backend(:pid)", observer);
        command.Parameters.Add(new KingbaseParameter("pid", (int)pid) { DataTypeName = "pg_catalog.int4" });
        Assert.Equal(true, await command.ExecuteScalarAsync(token));
    }

    static string Pooled(KingbaseConnectionStringBuilder settings, int keepAlive, string options)
    {
        var builder = new KingbaseConnectionStringBuilder(settings.ConnectionString) { KeepAlive = keepAlive };
        foreach (var option in options.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = option.Split('=', 2);
            builder[pair[0]] = pair[1];
        }
        return builder.ConnectionString;
    }

    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    {
        await using var command = new KingbaseCommand(sql, connection);
        return await command.ExecuteScalarAsync(token);
    }
}
