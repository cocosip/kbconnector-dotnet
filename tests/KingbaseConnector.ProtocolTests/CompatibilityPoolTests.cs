using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.PgSessionResetTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class CompatibilityPoolTests
{
    [Theory]
    [InlineData("oracle", null, null, false)]
    [InlineData("sqlserver", null, "", true)]
    [InlineData("mysql", "", null, true)]
    [InlineData("oracle", "", "ONLY_FULL_GROUP_BY", true)]
    [InlineData("mysql", "ONLY_FULL_GROUP_BY", "", true)]
    [InlineData("mysql", "ONLY_FULL_GROUP_BY", "ONLY_FULL_GROUP_BY", false)]
    public async Task ModeSpecificSqlModeMustMatchTheCapturedBaseline(string mode, string? initial, string? restored, bool mismatch)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var baseline = Baseline(); baseline[3] = mode; baseline[16] = initial!;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream();
            await Initialize(stream, guard.Token, mode: mode); await Capture(stream, guard.Token, State(baseline));
            Assert.Equal("DISCARD ALL", await ReadQueryAsync(stream, guard.Token)); await stream.WriteAsync(Control("DISCARD ALL"), guard.Token);
            await RefreshCatalog(stream, guard.Token); await Restore(stream, 12004, guard.Token);
            var after = (string[])baseline.Clone(); after[16] = restored!;
            await Capture(stream, guard.Token, State(after));
            if (!mismatch) await Identity(stream, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var session = await PgPhysicalSession.OpenAsync(PoolSettings(listener).ConnectionString, guard.Token, prepareForPooling: true);
        if (mismatch) { await Assert.ThrowsAnyAsync<IOException>(() => session.ResetAsync()); Assert.False(session.IsUsable); }
        else { await session.ResetAsync(); Assert.True(session.IsUsable); }
        session.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("pg", "TimeZone", "UTC")]
    [InlineData("oracle", "TimeZone", "UTC")]
    [InlineData("mysql", "standard_conforming_strings", "off")]
    [InlineData("sqlserver", "unexpected_setting", "changed")]
    public async Task IdleReportedDriftRetiresBeforeGrantingTheNextLease(string mode, string key, string value)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var baseline = Baseline(); baseline[3] = mode;
        using var source = new KingbaseDataSourceBuilder(PoolExecutionTests.Configuration(listener).ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using (var client = await listener.AcceptTcpClientAsync(guard.Token))
            using (var stream = client.GetStream())
            {
                await Initialize(stream, guard.Token, mode: mode); await Capture(stream, guard.Token, State(baseline));
                await PoolExecutionTests.Reset(stream, guard.Token, baseline: baseline);
                Assert.Equal("SELECT 1", await ReadQueryAsync(stream, guard.Token));
                await stream.WriteAsync(Join(Status(key, value), Scalar("1")), guard.Token);
                var next = new byte[1];
                if (await stream.ReadAsync(next, guard.Token) != 0)
                {
                    Assert.Equal((byte)'P', next[0]); await ReadExtended(stream, guard.Token, parseCodeConsumed: true);
                    await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 12002, 4), Row(["7"]), Complete(), Ready()), guard.Token); return;
                }
            }
            using var replacement = await listener.AcceptTcpClientAsync(guard.Token); using var clean = replacement.GetStream();
            await Initialize(clean, guard.Token, mode: mode); await Capture(clean, guard.Token, State(baseline));
            await ReadExtended(clean, guard.Token); await clean.WriteAsync(Value("9"), guard.Token);
            await PoolExecutionTests.Reset(clean, guard.Token, baseline: baseline);
            Assert.Equal(0, await clean.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using (var first = await source.OpenConnectionAsync(guard.Token)) await first.CloseAsync();
        using (var second = await source.OpenConnectionAsync(guard.Token))
        {
            using var command = new KingbaseCommand("SELECT 7", second);
            Assert.Equal(9, await command.ExecuteScalarAsync(guard.Token)); await second.CloseAsync();
        }
        source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ExactAssetPoolProbe(KingbaseCompatibilityMode mode) => KingbaseConnector.Tests.PoolAssetProbe.RunAsync(mode);

    [Theory]
    [InlineData(KingbaseCompatibilityMode.Oracle, "oracle")]
    [InlineData(KingbaseCompatibilityMode.MySql, "mysql")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "sqlserver")]
    public async Task ModeAwareIdleValidationReusesOriginalPhysicalSession(KingbaseCompatibilityMode mode, string wireMode)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = PoolExecutionTests.Configuration(listener); settings.CompatibilityMode = mode; settings.Timeout = 1;
        var baseline = Baseline(); baseline[3] = wireMode;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream();
            await Initialize(stream, guard.Token, mode: wireMode); await Capture(stream, guard.Token, State(baseline));
            for (var borrower = 0; borrower < 2; borrower++)
            {
                if (borrower != 0) await PoolExecutionTests.Ping(stream, guard.Token);
                await PoolExecutionTests.Query(stream, "SELECT 7", borrower == 0 ? uint.MaxValue : 12002, guard.Token);
                await PoolExecutionTests.Reset(stream, guard.Token, baseline: baseline);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        for (var borrower = 0; borrower < 2; borrower++)
        {
            using var connection = await source.OpenConnectionAsync(guard.Token);
            using var command = new KingbaseCommand("SELECT 7", connection);
            Assert.Equal(7, await command.ExecuteScalarAsync(guard.Token));
            await connection.CloseAsync();
        }
        source.Dispose(); await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.Oracle, "oracle", 'I')]
    [InlineData(KingbaseCompatibilityMode.Oracle, "oracle", 'T')]
    [InlineData(KingbaseCompatibilityMode.Oracle, "oracle", 'E')]
    [InlineData(KingbaseCompatibilityMode.MySql, "mysql", 'I')]
    [InlineData(KingbaseCompatibilityMode.MySql, "mysql", 'T')]
    [InlineData(KingbaseCompatibilityMode.MySql, "mysql", 'E')]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "sqlserver", 'I')]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "sqlserver", 'T')]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "sqlserver", 'E')]
    public async Task ActualModeOwnsCaptureRollbackRestoreAndRefreshedCatalog(KingbaseCompatibilityMode mode, string wireMode, char status)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var baseline = Baseline(); baseline[3] = wireMode;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream();
            await Initialize(stream, guard.Token, mode: wireMode.ToUpperInvariant()); await Capture(stream, guard.Token, State(baseline));
            Assert.Equal("SELECT dirty", await ReadQueryAsync(stream, guard.Token));
            await stream.WriteAsync(Join(Description(1), Row(["dirty"]), Complete(), Frame('Z', [(byte)status])), guard.Token);
            if (status != 'I')
            { Assert.Equal("ROLLBACK", await ReadQueryAsync(stream, guard.Token)); await stream.WriteAsync(Control("ROLLBACK"), guard.Token); }
            Assert.Equal("DISCARD ALL", await ReadQueryAsync(stream, guard.Token)); await stream.WriteAsync(Control("DISCARD ALL"), guard.Token);
            await RefreshCatalog(stream, guard.Token); await Restore(stream, 12004, guard.Token);
            var restored = (string[])baseline.Clone(); restored[3] = wireMode.ToUpperInvariant();
            await Capture(stream, guard.Token, State(restored)); await Identity(stream, guard.Token);
            Assert.Equal("SELECT next", await ReadQueryAsync(stream, guard.Token)); await stream.WriteAsync(Scalar("clean"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var settings = PoolSettings(listener); settings.CompatibilityMode = mode;
        using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, guard.Token, prepareForPooling: true);
        Assert.Equal(mode, session.Identity.EffectiveMode);
        await session.Queries.ExecuteAsync("SELECT dirty", guard.Token); var previous = session.Catalog;
        await session.ResetAsync(); Assert.NotSame(previous, session.Catalog);
        Assert.Equal(PgTransactionStatus.Idle, session.Queries.TransactionStatus);
        Assert.Equal("clean", (await session.Queries.ExecuteAsync("SELECT next", guard.Token))[0].Rows[0][0]);
        session.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("oracle", false)]
    [InlineData("mysql", false)]
    [InlineData("sqlserver", false)]
    [InlineData("oracle", true)]
    [InlineData("mysql", true)]
    [InlineData("sqlserver", true)]
    public async Task InitialOrReportedModeMismatchRetiresWithoutResetDispatch(string wireMode, bool reportedDrift)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var baseline = Baseline(); baseline[3] = reportedDrift ? wireMode : "pg";
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream();
            await Initialize(stream, guard.Token, mode: wireMode); await Capture(stream, guard.Token, State(baseline));
            if (reportedDrift)
            {
                Assert.Equal("SELECT drift", await ReadQueryAsync(stream, guard.Token));
                await stream.WriteAsync(Join(Status("database_mode", "pg"), Scalar("drift")), guard.Token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        if (reportedDrift)
        {
            using var session = await PgPhysicalSession.OpenAsync(PoolSettings(listener).ConnectionString, guard.Token, prepareForPooling: true);
            await session.Queries.ExecuteAsync("SELECT drift", guard.Token);
            await Assert.ThrowsAnyAsync<IOException>(() => session.ResetAsync()); Assert.False(session.IsUsable);
        }
        else await Assert.ThrowsAnyAsync<IOException>(() => PgPhysicalSession.OpenAsync(PoolSettings(listener).ConnectionString, guard.Token, prepareForPooling: true));
        await peer.WaitAsync(guard.Token);
    }
}
