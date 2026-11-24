using System.Text;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public class PgIdentityProbeTests
{
    const string VersionText = "KingbaseES V009R001C001M001ExampleB0024PS001 on test-platform";

    [Theory]
    [InlineData("PG", KingbaseCompatibilityMode.PostgreSql)]
    [InlineData("oRaClE", KingbaseCompatibilityMode.Oracle)]
    [InlineData("mysql", KingbaseCompatibilityMode.MySql)]
    [InlineData("SQLSERVER", KingbaseCompatibilityMode.SqlServer)]
    public async Task AutoUsesStartupModeAndKeepsProductAndHandshakeVersionsDistinct(string reported, KingbaseCompatibilityMode expected)
    {
        var stream = new QueryStream(Scalar(VersionText));
        using var session = Session(stream, reported);
        var identity = await PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Auto);
        Assert.Equal("14.0-unrelated-kernel", identity.StartupVersion);
        Assert.Equal(VersionText, identity.Version.RawText);
        Assert.Equal("V009R001C001M001ExampleB0024PS001", identity.Version.Token);
        Assert.Equal(KingbaseCompatibilityMode.Auto, identity.ConfiguredMode);
        Assert.Equal(expected, identity.EffectiveMode);
        Assert.Equal(expected, identity.ReportedMode);
        Assert.Equal(PgModeSource.ParameterStatus, identity.ReportedModeSource);
        Assert.Equal(Query("SELECT version();"), Assert.Single(stream.Writes));
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task AutoQueriesMissingModeWithExactlyOneReadOnlyShow()
    {
        var stream = new QueryStream(Join(Scalar(VersionText), Scalar("oracle")));
        using var session = Session(stream);
        var identity = await PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Auto);
        Assert.Equal(KingbaseCompatibilityMode.Oracle, identity.EffectiveMode);
        Assert.Equal(KingbaseCompatibilityMode.Oracle, identity.ReportedMode);
        Assert.Equal(PgModeSource.Show, identity.ReportedModeSource);
        Assert.Equal(2, stream.Writes.Count);
        Assert.Equal(Query("SELECT version();"), stream.Writes[0]);
        Assert.Equal(Query("SHOW database_mode"), stream.Writes[1]);
    }

    [Fact]
    public async Task ParameterUpdatesDuringTheVersionQuerySupplyModeWithoutReplacingTheRawStartupVersion()
    {
        var stream = new QueryStream(Join(Description(), Row(VersionText), Status("database_mode", "sqlserver"),
            Status("server_version", "changed-later"), Complete(), Ready('I')));
        using var session = Session(stream);
        var identity = await PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Auto);
        Assert.Equal("14.0-unrelated-kernel", identity.StartupVersion);
        Assert.Equal(KingbaseCompatibilityMode.SqlServer, identity.EffectiveMode);
        Assert.Equal(PgModeSource.ParameterStatus, identity.ReportedModeSource);
        Assert.Single(stream.Writes);
    }

    [Theory]
    [InlineData("ORACLE", true)]
    [InlineData("pg", false)]
    [InlineData("unknown", false)]
    public async Task ParameterStatusArrivingDuringShowMustAgreeWithItsResult(string parameterMode, bool agrees)
    {
        var show = Join(Description(), Row("oracle"), Status("database_mode", parameterMode), Complete(), Ready('I'));
        var stream = new QueryStream(Join(Scalar(VersionText), show));
        using var session = Session(stream);
        if (agrees)
        {
            var identity = await PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Auto);
            Assert.Equal(KingbaseCompatibilityMode.Oracle, identity.EffectiveMode);
            Assert.Equal(PgModeSource.Show, identity.ReportedModeSource);
            Assert.Equal(0, stream.DisposeCount);
        }
        else
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Auto));
            Assert.Equal(1, stream.DisposeCount);
        }
        Assert.Equal(2, stream.Writes.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("oracle")]
    public async Task ExplicitModeSelectsRulesWithoutAnUnnecessaryModeQuery(string? reported)
    {
        var stream = new QueryStream(Scalar(VersionText));
        using var session = Session(stream, reported);
        var identity = await PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Oracle);
        Assert.Equal(KingbaseCompatibilityMode.Oracle, identity.ConfiguredMode);
        Assert.Equal(KingbaseCompatibilityMode.Oracle, identity.EffectiveMode);
        Assert.Equal(reported is null ? null : KingbaseCompatibilityMode.Oracle, identity.ReportedMode);
        Assert.Equal(reported is null ? null : PgModeSource.ParameterStatus, identity.ReportedModeSource);
        Assert.Single(stream.Writes);
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.Auto, "private-mode")]
    [InlineData(KingbaseCompatibilityMode.Oracle, "mysql")]
    [InlineData(KingbaseCompatibilityMode.Oracle, "private-mode")]
    [InlineData(KingbaseCompatibilityMode.Auto, "")]
    public async Task UnsupportedOrConflictingReportedModesRetireTheSession(KingbaseCompatibilityMode configured, string reported)
    {
        var stream = new QueryStream(Scalar(VersionText));
        using var session = Session(stream, reported);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => PgIdentityProbe.RunAsync(session, configured));
        Assert.DoesNotContain("private-", error.ToString());
        Assert.Equal(1, stream.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteAsync("next"));
    }

    [Theory]
    [InlineData("PostgreSQL 14.0")]
    [InlineData("private-version")]
    [InlineData("KingbaseES V008R006C009B0014 on test-platform")]
    public async Task ExplicitModeCannotBypassKingbaseV9Identity(string versionText)
    {
        var stream = new QueryStream(Scalar(versionText));
        using var session = Session(stream);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Oracle));
        Assert.DoesNotContain("private-", error.ToString());
        Assert.Single(stream.Writes);
        Assert.Equal(1, stream.DisposeCount);
    }

    [Theory]
    [InlineData("private-mode")]
    [InlineData(null)]
    public async Task MissingStartupModeMustBeEstablishedByAValidScalar(string? mode)
    {
        var stream = new QueryStream(Join(Scalar(VersionText), Scalar(mode)));
        using var session = Session(stream);
        if (mode is null)
            await Assert.ThrowsAsync<IOException>(() => PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Auto));
        else
            await Assert.ThrowsAsync<NotSupportedException>(() => PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Auto));
        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(2, stream.Writes.Count);
    }

    public static IEnumerable<object[]> InvalidVersionShapes()
    {
        yield return new object[] { Scalar(null) };
        yield return new object[] { Join(Description(), Complete(), Ready('I')) };
        yield return new object[] { Join(Complete(), Ready('I')) };
        yield return new object[] { Join(Frame('I'), Ready('I')) };
        yield return new object[] { Join(Description(), Row(VersionText), Row(VersionText), Complete(), Ready('I')) };
        yield return new object[] { Join(Description(), Row(VersionText), Complete(), Description(), Row(VersionText), Complete(), Ready('I')) };
        yield return new object[] { Join(Description(), Row(VersionText), Complete(), Ready('T')) };
        yield return new object[] { Join(Description(), Row(VersionText), Complete(), Ready('E')) };
        var column = Description().Skip(7).ToArray();
        var twoColumns = Frame('T', Join(new byte[] { 0, 2 }, column, column));
        var field = Row(VersionText).Skip(7).ToArray();
        var twoFields = Frame('D', Join(new byte[] { 0, 2 }, field, field));
        yield return new object[] { Join(twoColumns, twoFields, Complete(), Ready('I')) };
    }

    [Theory]
    [MemberData(nameof(InvalidVersionShapes))]
    public async Task VersionProbeRequiresExactlyOneNonNullScalarAndIdleState(byte[] input)
    {
        var stream = new QueryStream(input);
        using var session = Session(stream, "pg");
        await Assert.ThrowsAsync<IOException>(() => PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Auto));
        Assert.Single(stream.Writes);
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task ServerProbeErrorsAreNotRetriedOrReplacedWithHandshakeVersion()
    {
        var stream = new QueryStream(Join(Frame('E', Encoding.ASCII.GetBytes("SERROR\0C42883\0Mprivate-error\0\0")), Ready('I')));
        using var session = Session(stream, "pg");
        var error = await Assert.ThrowsAsync<PgQueryException>(() => PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Auto));
        Assert.Equal("42883", error.ServerError.SqlState);
        Assert.DoesNotContain("private-", error.ToString());
        Assert.Single(stream.Writes);
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task PreCancellationInvalidModeAndExistingTransactionDoNoProbeIo()
    {
        var stream = new QueryStream(Join(Complete(), Ready('T')));
        using var session = Session(stream, "pg");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Auto, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PgIdentityProbe.RunAsync(session, (KingbaseCompatibilityMode)99));
        Assert.Empty(stream.Writes);
        Assert.Equal(0, stream.DisposeCount);
        await session.ExecuteAsync("BEGIN");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Auto));
        Assert.Single(stream.Writes);
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task CancellationWhileWaitingForModeRetiresTheUninitializedSession()
    {
        var stream = new QueryStream(Scalar(VersionText)) { BlockAtEnd = true };
        using var session = Session(stream);
        using var cancel = new CancellationTokenSource();
        var probe = PgIdentityProbe.RunAsync(session, KingbaseCompatibilityMode.Auto, cancel.Token);
        await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        Assert.Equal(1, stream.DisposeCount);
    }

    static PgQuerySession Session(QueryStream stream, string? mode = null)
    {
        var parameters = new Dictionary<string, string> { ["client_encoding"] = "UTF8", ["server_version"] = "14.0-unrelated-kernel" };
        if (mode is not null) parameters["database_mode"] = mode;
        return new PgQuerySession(new PgWire(stream), new PgStartupResult(parameters, new PgBackendKey(1, 2)));
    }
    static byte[] Scalar(string? value) => Join(Description(), Row(value), Complete(), Ready('I'));
    static byte[] Description() => Frame('T', Join(new byte[] { 0, 1, (byte)'x', 0 }, new byte[6],
        new byte[] { 0, 0, 0, 25, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0, 0 }));
    static byte[] Row(string? value) => Frame('D', value is null ? Join(new byte[] { 0, 1 }, Int32(-1)) :
        Join(new byte[] { 0, 1 }, Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value)));
    static byte[] Complete() => Frame('C', Encoding.ASCII.GetBytes("SELECT 1\0"));
    static byte[] Status(string key, string value) => Frame('S', Encoding.UTF8.GetBytes(key + "\0" + value + "\0"));
    static byte[] Ready(char status) => Frame('Z', (byte)status);
    static byte[] Query(string sql) => Frame('Q', Encoding.UTF8.GetBytes(sql + "\0"));
    static byte[] Int32(int value) => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
    static byte[] Frame(char code, params byte[] payload) => Join(new[] { (byte)code }, Int32(payload.Length + 4), payload);
    static byte[] Join(params byte[][] pieces) => pieces.SelectMany(x => x).ToArray();
}
