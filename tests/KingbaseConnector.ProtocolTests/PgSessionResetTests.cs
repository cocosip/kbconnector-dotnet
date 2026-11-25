using System.Net.Sockets;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgSessionResetTests
{
    const string Product = "KingbaseES V009R001C001B0001";
    const string StateSql = "SELECT pg_catalog.current_database(),SESSION_USER,CURRENT_USER,pg_catalog.current_setting('database_mode')," +
        "pg_catalog.current_setting('client_encoding'),pg_catalog.current_setting('standard_conforming_strings')," +
        "pg_catalog.current_setting('application_name'),pg_catalog.current_setting('search_path'),pg_catalog.current_setting('TimeZone')," +
        "pg_catalog.current_setting('default_transaction_isolation'),pg_catalog.current_setting('default_transaction_read_only')," +
        "pg_catalog.current_setting('default_transaction_deferrable'),pg_catalog.current_setting('DateStyle'),pg_catalog.current_setting('IntervalStyle')," +
        "pg_catalog.current_setting('transaction_isolation'),pg_catalog.current_setting('transaction_read_only'),pg_catalog.current_setting('sql_mode',true)";
    const string RestoreSql = "SELECT pg_catalog.set_config('client_encoding',$1,false),pg_catalog.set_config('standard_conforming_strings',$2,false)," +
        "pg_catalog.set_config('application_name',$3,false),pg_catalog.set_config('search_path',$4,false),pg_catalog.set_config('TimeZone',$5,false)," +
        "pg_catalog.set_config('default_transaction_isolation',$6,false),pg_catalog.set_config('default_transaction_read_only',$7,false)," +
        "pg_catalog.set_config('default_transaction_deferrable',$8,false),pg_catalog.set_config('DateStyle',$9,false),pg_catalog.set_config('IntervalStyle',$10,false)";
    internal static string[] Baseline() => ["db", "user", "user", "pg", "UTF8", "on", "app';--\\中文", "\"Odd Schema\", public", "Asia/Shanghai",
        "read committed", "off", "off", "ISO, MDY", "postgres", "read committed", "off", ""];
    internal static byte[] State(string[]? values = null, char status = 'I') => Join(Description(17), Row(values ?? Baseline()), Complete(), Frame('Z', [(byte)status]));
    internal static byte[] Control(string tag, char status = 'I') => Join(Frame('C', Encoding.ASCII.GetBytes(tag + "\0")), Frame('Z', [(byte)status]));
    static byte[] Error(char status = 'I') => Join(Frame('E', Encoding.ASCII.GetBytes("SERROR\0C42501\0Mprivate-reset-error\0\0")), Frame('Z', [(byte)status]));
    static byte[] FreshCatalog(uint start = 12000) => Catalog(new[] { ("bool", (short)1), ("int2", (short)2), ("int4", (short)4),
        ("int8", (short)8), ("text", (short)-1), ("bytea", (short)-1) }.Select((t, i) => new PgTypeDescription(start + (uint)i, 90, t.Item1, t.Item2, 'b', 'U', 0, 0, 0, -1)));
    internal static KingbaseConnectionStringBuilder PoolSettings(TcpListener listener)
    {
        var settings = Settings(listener); settings.Pooling = true; settings.ApplicationName = Baseline()[6];
        settings.SearchPath = Baseline()[7]; settings.Timezone = Baseline()[8];
        return settings;
    }
    internal static async Task Capture(Stream stream, CancellationToken token, byte[]? response = null)
    {
        Assert.Equal(StateSql, await ReadQueryAsync(stream, token));
        await stream.WriteAsync(response ?? State(), token);
    }
    internal static async Task Restore(Stream stream, uint textOid, CancellationToken token, byte[]? response = null)
    {
        await ReadRestore(stream, textOid, token);
        await stream.WriteAsync(response ?? Join(Frame('1'), Frame('2'), Description(10), Row(Baseline()[4..14]), Complete(), Ready()), token);
    }
    static async Task ReadRestore(Stream stream, uint textOid, CancellationToken token, string[]? baseline = null)
    {
        var frames = await ReadExtended(stream, token);
        Assert.Equal(Frame('P', Join([0], Encoding.UTF8.GetBytes(RestoreSql + "\0"), [0, 10], Join(Enumerable.Repeat(Int32(unchecked((int)textOid)), 10).ToArray()))), frames[0]);
        var values = (baseline ?? Baseline())[4..14];
        Assert.Equal(Frame('B', Join([0, 0, 0, 0, 0, 10], Join(values.Select(v => Join(Int32(Encoding.UTF8.GetByteCount(v)), Encoding.UTF8.GetBytes(v))).ToArray()), [0, 0])), frames[1]);
    }
    internal static async Task RefreshCatalog(Stream stream, CancellationToken token, uint start = 12000)
    {
        Assert.Contains("FROM sys_catalog.sys_type", await ReadQueryAsync(stream, token));
        await stream.WriteAsync(FreshCatalog(start), token);
    }
    internal static async Task Identity(Stream stream, CancellationToken token, string product = Product)
    {
        Assert.Equal("SELECT pg_catalog.version();", await ReadQueryAsync(stream, token));
        await stream.WriteAsync(Scalar(product), token);
    }

    [Theory]
    [InlineData(false, 'I')]
    [InlineData(true, 'T')]
    [InlineData(false, 'E')]
    public async Task ResetConfirmsRollbackAndRestoresBoundBaselineForTwoSimulatedBorrowers(bool sync, char dirtyStatus)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var creation = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Capture(stream, guard.Token);
            for (var borrower = 0; borrower < 2; borrower++)
            {
                Assert.Equal("SELECT dirty", await ReadQueryAsync(stream, guard.Token));
                await stream.WriteAsync(Join(Status("application_name", "borrower"), Status("standard_conforming_strings", "off"),
                    Description(1), Row(["dirty"]), Complete(), Frame('Z', [(byte)dirtyStatus])), guard.Token);
                if (dirtyStatus != 'I')
                {
                    Assert.Equal("ROLLBACK", await ReadQueryAsync(stream, guard.Token));
                    await stream.WriteAsync(Control("ROLLBACK"), guard.Token);
                }
                Assert.Equal("DISCARD ALL", await ReadQueryAsync(stream, guard.Token));
                await stream.WriteAsync(Join(Status("application_name", ""), Status("standard_conforming_strings", "on"), Control("DISCARD ALL")), guard.Token);
                await RefreshCatalog(stream, guard.Token, (uint)(12000 + borrower * 100));
                await Restore(stream, (uint)(12004 + borrower * 100), guard.Token,
                    Join(Status("application_name", Baseline()[6]), Frame('1'), Frame('2'), Description(10), Row(Baseline()[4..14]), Complete(), Ready()));
                await Capture(stream, guard.Token); await Identity(stream, guard.Token);
                Assert.Equal("SELECT next-borrower", await ReadQueryAsync(stream, guard.Token));
                await stream.WriteAsync(Scalar("clean"), guard.Token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var session = sync ? await Task.Run(() => PgPhysicalSession.Open(PoolSettings(listener).ConnectionString, prepareForPooling: true), guard.Token)
            : await PgPhysicalSession.OpenAsync(PoolSettings(listener).ConnectionString, creation.Token, prepareForPooling: true);
        creation.Cancel(); // Neither cleanup nor later use may inherit an old application token.
        for (var borrower = 0; borrower < 2; borrower++)
        {
            await session.Queries.ExecuteAsync("SELECT dirty", guard.Token);
            var oldCatalog = session.Catalog;
            if (sync) session.Reset(); else await session.ResetAsync();
            Assert.True(session.IsUsable); Assert.Equal(PgTransactionStatus.Idle, session.Queries.TransactionStatus);
            Assert.NotSame(oldCatalog, session.Catalog);
            Assert.Equal((uint)(12004 + borrower * 100), session.Catalog.ByName["pg_catalog.text"].Oid);
            Assert.Equal("on", session.Queries.Parameters["standard_conforming_strings"]);
            Assert.Equal(Baseline()[6], session.Queries.Parameters["application_name"]);
            Assert.Equal("clean", (await session.Queries.ExecuteAsync("SELECT next-borrower", guard.Token))[0].Rows[0][0]);
        }
        session.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(3, "oracle")]
    [InlineData(4, "LATIN1")]
    [InlineData(5, "unknown")]
    [InlineData(10, "true")]
    [InlineData(14, "serializable")]
    [InlineData(15, "on")]
    [InlineData(6, "nul\0value")]
    public async Task InvalidInitialBaselineRetiresBeforePublication(int index, string invalid)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            var values = Baseline(); values[index] = invalid; await Capture(stream, guard.Token, State(values));
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var error = await Assert.ThrowsAnyAsync<IOException>(() => PgPhysicalSession.OpenAsync(PoolSettings(listener).ConnectionString, guard.Token, prepareForPooling: true));
        Assert.DoesNotContain("nul", error.ToString()); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("discard")]
    [InlineData("catalog")]
    [InlineData("restore")]
    [InlineData("state")]
    [InlineData("identity")]
    public async Task SynchronizedResetFailureStillRetiresWithoutRetry(string phase)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Capture(stream, guard.Token);
            Assert.Equal("BEGIN", await ReadQueryAsync(stream, guard.Token)); await stream.WriteAsync(Control("BEGIN", 'T'), guard.Token);
            var stages = new[] { "rollback", "discard", "catalog", "restore", "state", "identity" };
            foreach (var stage in stages)
            {
                if (stage == "restore") await Restore(stream, 12004, guard.Token, stage == phase ? Error() : null);
                else
                {
                    var sql = await ReadQueryAsync(stream, guard.Token);
                    if (stage == "rollback") Assert.Equal("ROLLBACK", sql);
                    if (stage == "discard") Assert.Equal("DISCARD ALL", sql);
                    if (stage == "catalog") Assert.Contains("FROM sys_catalog.sys_type", sql);
                    if (stage == "state") Assert.Equal(StateSql, sql);
                    if (stage == "identity") Assert.Equal("SELECT pg_catalog.version();", sql);
                    await stream.WriteAsync(stage == phase ? Error() : stage switch
                    {
                        "rollback" => Control("ROLLBACK"), "discard" => Control("DISCARD ALL"), "catalog" => FreshCatalog(),
                        "state" => State(), _ => Scalar(Product)
                    }, guard.Token);
                }
                if (stage == phase) break;
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var session = await PgPhysicalSession.OpenAsync(PoolSettings(listener).ConnectionString, guard.Token, prepareForPooling: true);
        await session.Queries.ExecuteAsync("BEGIN", guard.Token);
        var error = await Assert.ThrowsAnyAsync<IOException>(() => session.ResetAsync());
        Assert.DoesNotContain("private", error.ToString()); Assert.False(session.IsUsable);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ResetAsync());
        await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }

    static async Task ReadUntilPhase(NetworkStream stream, string phase, CancellationToken token)
    {
        foreach (var stage in new[] { "rollback", "discard", "catalog", "restore", "state", "identity" })
        {
            if (stage == "restore") await ReadRestore(stream, 12004, token);
            else
            {
                var sql = await ReadQueryAsync(stream, token);
                if (stage == "rollback") Assert.Equal("ROLLBACK", sql);
                if (stage == "discard") Assert.Equal("DISCARD ALL", sql);
                if (stage == "catalog") Assert.Contains("FROM sys_catalog.sys_type", sql);
                if (stage == "state") Assert.Equal(StateSql, sql);
                if (stage == "identity") Assert.Equal("SELECT pg_catalog.version();", sql);
            }
            if (stage == phase) return;
            await stream.WriteAsync(stage switch
            {
                "rollback" => Control("ROLLBACK"), "discard" => Control("DISCARD ALL"), "catalog" => FreshCatalog(),
                "restore" => Join(Frame('1'), Frame('2'), Description(10), Row(Baseline()[4..14]), Complete(), Ready()),
                "state" => State(), _ => Scalar(Product)
            }, token);
        }
        throw new InvalidOperationException("Unknown reset test phase.");
    }

    public static IEnumerable<object[]> InterruptedPhases() =>
        from phase in new[] { "rollback", "discard", "catalog", "restore", "state", "identity" }
        from eof in new[] { false, true } select new object[] { phase, eof };

    [Theory]
    [MemberData(nameof(InterruptedPhases))]
    public async Task IndependentCleanupDeadlineAndEofRetireAtEveryPhase(string phase, bool eof)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = PoolSettings(listener); settings.Timeout = 0; settings.CommandTimeout = 0; settings.CleanupTimeout = 1;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Capture(stream, guard.Token);
            Assert.Equal("BEGIN", await ReadQueryAsync(stream, guard.Token)); await stream.WriteAsync(Control("BEGIN", 'T'), guard.Token);
            await ReadUntilPhase(stream, phase, guard.Token);
            if (!eof) Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, guard.Token, prepareForPooling: true);
        await session.Queries.ExecuteAsync("BEGIN", guard.Token);
        if (eof) await Assert.ThrowsAnyAsync<IOException>(() => session.ResetAsync());
        else await Assert.ThrowsAsync<TimeoutException>(() => session.ResetAsync());
        Assert.False(session.IsUsable); await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }

    [Fact]
    public async Task CleanupHasOneDeadlineAcrossEarlierPhases()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = PoolSettings(listener); settings.CleanupTimeout = 1;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Capture(stream, guard.Token);
            Assert.Equal("DISCARD ALL", await ReadQueryAsync(stream, guard.Token));
            await Task.Delay(700, guard.Token); await stream.WriteAsync(Control("DISCARD ALL"), guard.Token);
            Assert.Contains("FROM sys_catalog.sys_type", await ReadQueryAsync(stream, guard.Token));
            var closed = stream.ReadAsync(new byte[1], guard.Token).AsTask();
            if (await Task.WhenAny(closed, Task.Delay(500, guard.Token)) != closed)
                await stream.WriteAsync(Scalar("invalid-catalog"), guard.Token);
            Assert.Equal(0, await closed);
        }, guard.Token);
        using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, guard.Token, prepareForPooling: true);
        await Assert.ThrowsAsync<TimeoutException>(() => session.ResetAsync());
        Assert.False(session.IsUsable); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BaselineCaptureSharesTheOpenDeadlineAndCallerCancellation(bool cancel)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            Assert.Equal(StateSql, await ReadQueryAsync(stream, guard.Token)); arrived.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var settings = PoolSettings(listener); settings.Timeout = cancel ? 0 : 1;
        var opening = PgPhysicalSession.OpenAsync(settings.ConnectionString, caller.Token, prepareForPooling: true);
        await arrived.Task.WaitAsync(guard.Token);
        if (cancel)
        {
            caller.Cancel();
            Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening)).CancellationToken);
        }
        else await Assert.ThrowsAsync<TimeoutException>(() => opening);
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task DuplicateResetRejectsLocallyAndDisposalAbortsTheCurrentReset()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Capture(stream, guard.Token);
            Assert.Equal("DISCARD ALL", await ReadQueryAsync(stream, guard.Token)); arrived.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var session = await PgPhysicalSession.OpenAsync(PoolSettings(listener).ConnectionString, guard.Token, prepareForPooling: true);
        var resetting = session.ResetAsync(); await arrived.Task.WaitAsync(guard.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ResetAsync()); Assert.True(session.IsUsable);
        session.Dispose(); await Assert.ThrowsAnyAsync<Exception>(() => resetting.WaitAsync(guard.Token));
        Assert.False(session.IsUsable); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task UnpreparedResetAndInvalidPoolPreparationRejectWithoutTouchingTheWire()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Settings(listener);
        await Assert.ThrowsAsync<ArgumentException>(() => PgPhysicalSession.OpenAsync(settings.ConnectionString, guard.Token, prepareForPooling: true));
        Assert.False(listener.Pending());
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            Assert.Equal("SELECT untouched", await ReadQueryAsync(stream, guard.Token));
            await stream.WriteAsync(Scalar("usable"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, guard.Token);
        Assert.Throws<InvalidOperationException>(() => session.Reset());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ResetAsync());
        Assert.Equal("usable", (await session.Queries.ExecuteAsync("SELECT untouched", guard.Token))[0].Rows[0][0]);
        session.Dispose(); await peer.WaitAsync(guard.Token);
    }

    public static IEnumerable<object[]> MalformedReplies()
    {
        yield return ["rollback", Control("COMMIT")];
        yield return ["rollback", Control("ROLLBACK", 'T')];
        yield return ["discard", Control("RESET")];
        yield return ["discard", Control("DISCARD ALL", 'T')];
        yield return ["discard", Join(Description(1), Row(["private-cell"]), Control("DISCARD ALL"))];
        yield return ["restore", Join(Frame('1'), Frame('2'), Description(10), Row(Baseline()[4..14]), Complete(), Frame('Z', [(byte)'T']))];
        var wrong = Baseline()[4..14]; wrong[2] = "private-ignored-setting";
        yield return ["restore", Join(Frame('1'), Frame('2'), Description(10), Row(wrong), Complete(), Ready())];
        yield return ["state", Join(Description(16), Complete(), Ready())];
        yield return ["state", Join(Description(17), Row(Baseline()), Row(Baseline()), Complete(), Ready())];
        yield return ["state", State(status: 'T')];
        yield return ["state", Join(Status("unknown_session_state", "private-value"), State())];
        yield return ["state", Join(Status("server_version", "different-kernel"), State())];
        yield return ["identity", Scalar("KingbaseES V009R001C001B0002")];
        for (var index = 0; index < 16; index++)
        {
            var changed = Baseline(); changed[index] = "private-dirty-state";
            yield return ["state", State(changed)];
        }
    }

    [Theory]
    [MemberData(nameof(MalformedReplies))]
    public async Task MalformedOrUnrestoredStateRetiresWithoutPublishingNewCatalog(string phase, byte[] response)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Capture(stream, guard.Token);
            Assert.Equal("BEGIN", await ReadQueryAsync(stream, guard.Token)); await stream.WriteAsync(Control("BEGIN", 'T'), guard.Token);
            await ReadUntilPhase(stream, phase, guard.Token); await stream.WriteAsync(response, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var session = await PgPhysicalSession.OpenAsync(PoolSettings(listener).ConnectionString, guard.Token, prepareForPooling: true);
        var catalog = session.Catalog;
        await session.Queries.ExecuteAsync("BEGIN", guard.Token);
        var error = await Assert.ThrowsAnyAsync<IOException>(() => session.ResetAsync());
        Assert.DoesNotContain("private", error.ToString()); Assert.False(session.IsUsable); Assert.Same(catalog, session.Catalog);
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ReportedModeConflictRetiresBeforeAnyResetSql()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Capture(stream, guard.Token);
            Assert.Equal("SELECT drift", await ReadQueryAsync(stream, guard.Token));
            await stream.WriteAsync(Join(Status("database_mode", "oracle"), Scalar("drift")), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var session = await PgPhysicalSession.OpenAsync(PoolSettings(listener).ConnectionString, guard.Token, prepareForPooling: true);
        await session.Queries.ExecuteAsync("SELECT drift", guard.Token);
        await Assert.ThrowsAnyAsync<IOException>(() => session.ResetAsync());
        Assert.False(session.IsUsable); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MaxValue, true)]
    public async Task PreservesInitialTransactionDefaultsAndStopsTheCleanupTimerAfterSuccess(int cleanupTimeout, bool readOnly)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var baseline = Baseline();
        if (readOnly) { baseline[9] = baseline[14] = "serializable"; baseline[10] = baseline[11] = baseline[15] = "on"; }
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Capture(stream, guard.Token, State(baseline));
            Assert.Equal("DISCARD ALL", await ReadQueryAsync(stream, guard.Token)); await stream.WriteAsync(Control("DISCARD ALL"), guard.Token);
            await RefreshCatalog(stream, guard.Token); await ReadRestore(stream, 12004, guard.Token, baseline);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(10), Row(baseline[4..14]), Complete(), Ready()), guard.Token);
            await Capture(stream, guard.Token, State(baseline)); await Identity(stream, guard.Token);
            Assert.Equal("SELECT after-deadline", await ReadQueryAsync(stream, guard.Token)); await stream.WriteAsync(Scalar("usable"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var settings = PoolSettings(listener); settings.CleanupTimeout = cleanupTimeout;
        using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, guard.Token, prepareForPooling: true);
        await session.ResetAsync();
        if (cleanupTimeout == 1) await Task.Delay(1100, guard.Token);
        Assert.Equal("usable", (await session.Queries.ExecuteAsync("SELECT after-deadline", guard.Token))[0].Rows[0][0]);
        session.Dispose(); await peer.WaitAsync(guard.Token);
    }

    public static IEnumerable<object[]> InvalidBaselineShapes()
    {
        yield return [Error()];
        yield return [State(status: 'T')];
        yield return [Join(Description(16), Complete(), Ready())];
        yield return [Join(Description(17), Row(Baseline()), Row(Baseline()), Complete(), Ready())];
        yield return [Join(Description(15), Row(Baseline()[..15]), Complete(), Ready())];
        yield return [Join(Description(17), Row(Baseline()), Control("SHOW"))];
        yield return [Join(Description(17), Frame('D', Join([0, 17], Int32(-1), Join(Baseline()[1..].Select(value =>
            Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray()))), Complete(), Ready())];
    }

    [Theory]
    [MemberData(nameof(InvalidBaselineShapes))]
    public async Task BaselineErrorsAndMalformedShapesRetireInitialization(byte[] response)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Capture(stream, guard.Token, response);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var error = await Assert.ThrowsAnyAsync<IOException>(() => PgPhysicalSession.OpenAsync(PoolSettings(listener).ConnectionString, guard.Token, prepareForPooling: true));
        Assert.DoesNotContain("private", error.ToString()); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task WritableSearchPathCannotRedirectFinalIdentityProbeAndRecreateDirtyState()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var baseline = Baseline(); baseline[7] = "tenant,sys_catalog";
        var shadowedVersionExecuted = false;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await Capture(stream, guard.Token, State(baseline));
            Assert.Equal("DISCARD ALL", await ReadQueryAsync(stream, guard.Token)); await stream.WriteAsync(Control("DISCARD ALL"), guard.Token);
            await RefreshCatalog(stream, guard.Token); await ReadRestore(stream, 12004, guard.Token, baseline);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(10), Row(baseline[4..14]), Complete(), Ready()), guard.Token);
            await Capture(stream, guard.Token, State(baseline));
            var query = await ReadQueryAsync(stream, guard.Token);
            if (query == "SELECT version();") shadowedVersionExecuted = true; // Tenant function returns the real version but recreates dirty state.
            else Assert.Equal("SELECT pg_catalog.version();", query);
            await stream.WriteAsync(Scalar(Product), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var settings = PoolSettings(listener); settings.SearchPath = baseline[7];
        using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, guard.Token, prepareForPooling: true);
        await session.ResetAsync(); session.Dispose(); await peer.WaitAsync(guard.Token);
        Assert.False(shadowedVersionExecuted);
    }
}
