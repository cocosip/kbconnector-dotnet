using System.Globalization;
using System.Text;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgTypeCatalogTests
{
    const string Sql = "SELECT t.oid, t.typnamespace, n.nspname, t.typname, t.typlen, t.typtype, " +
        "t.typcategory, t.typisdefined, t.typbasetype, t.typelem, t.typarray, t.typtypmod " +
        "FROM sys_catalog.sys_type AS t JOIN sys_catalog.sys_namespace AS n ON n.oid = t.typnamespace " +
        "WHERE n.nspname = 'pg_catalog' AND t.typisdefined AND t.typname IN " +
        "('bool','int2','int4','int8','oid','float4','float8','numeric','text','varchar','bpchar'," +
        "'bytea','uuid','date','time','timetz','timestamp','timestamptz','interval','json','jsonb','xml')";

    static readonly string[] Required = ["bool", "int2", "int4", "int8", "text", "bytea"];

    [Fact]
    public async Task LoadsActualUnsignedOidsAndMetadataWithExactQueryAndImmutableMaps()
    {
        var numeric = Fields("numeric", "4294967295");
        numeric[4] = "-1"; numeric[5] = "d"; numeric[6] = "?";
        numeric[8] = "42"; numeric[9] = "404"; numeric[10] = "4000000000"; numeric[11] = "-2147483648";
        var stream = new QueryStream(Response(Rows().Append(numeric)));
        using var session = Session(stream);
        var catalog = await PgTypeCatalog.LoadAsync(session, Identity());
        Assert.Single(stream.Writes);
        Assert.Equal(Query(Sql), stream.Writes[0]);
        Assert.Equal(7, catalog.ByOid.Count);
        var type = catalog.ByOid[uint.MaxValue];
        Assert.Same(type, catalog.ByName["pg_catalog.numeric"]);
        Assert.Equal((uint)987, type.NamespaceOid);
        Assert.Equal("pg_catalog", type.Namespace);
        Assert.Equal("numeric", type.Name);
        Assert.Equal((short)-1, type.Size);
        Assert.Equal('d', type.Kind);
        Assert.Equal('?', type.Category);
        Assert.Equal((uint)42, type.BaseTypeOid);
        Assert.Equal((uint)404, type.ElementTypeOid);
        Assert.Equal((uint)4000000000, type.ArrayTypeOid);
        Assert.Equal(int.MinValue, type.TypeModifier);
        Assert.False(catalog.ByOid.ContainsKey(23));
        Assert.False(catalog.ByName.ContainsKey("pg_catalog.INT4"));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<uint, PgTypeDescription>)catalog.ByOid).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, PgTypeDescription>)catalog.ByName).Clear());
        Assert.Equal(0, stream.DisposeCount);
    }

    [Theory]
    [InlineData("t")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public async Task SupportsExplicitTrueTextSpellingsWithoutNumericCoercion(string defined)
    {
        var rows = Rows().ToArray();
        foreach (var row in rows) row[7] = defined;
        var stream = new QueryStream(Response(rows));
        using var session = Session(stream);
        Assert.Equal(Required.Length, (await PgTypeCatalog.LoadAsync(session, Identity())).ByOid.Count);
    }

    public static IEnumerable<object?[]> InvalidFields()
    {
        foreach (var column in Enumerable.Range(0, 12)) yield return [column, null];
        foreach (var value in new[] { "0", "-1", "4294967296", "+42", " 42", "42 ", "4.2", "١٢" })
            yield return [0, value];
        yield return [1, "0"];
        yield return [1, "988"];
        yield return [2, "public"];
        yield return [2, "PG_CATALOG"];
        yield return [3, "user_secret_type"];
        foreach (var value in new[] { "0", "-3", "32768", "-32769", "1.0", " 4", "+4" }) yield return [4, value];
        foreach (var column in new[] { 5, 6 })
            foreach (var value in new[] { "", "ab", "\n", " ", "中" }) yield return [column, value];
        foreach (var value in new[] { "f", "false", "1", "yes", "" }) yield return [7, value];
        foreach (var column in new[] { 8, 9, 10 })
            foreach (var value in new[] { "-1", "4294967296", "+1", "1 " }) yield return [column, value];
        foreach (var value in new[] { "2147483648", "-2147483649", "+1", " 1", "1.0" }) yield return [11, value];
    }

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public async Task RejectsInvalidCatalogFieldsWithoutLeakingOrReusingSession(int index, string? value)
    {
        var rows = Rows().ToArray();
        rows[^1][index] = value;
        var stream = new QueryStream(Response(rows));
        using var session = Session(stream);
        var error = await Assert.ThrowsAsync<IOException>(() => PgTypeCatalog.LoadAsync(session, Identity()));
        Assert.DoesNotContain("user_secret_type", error.ToString());
        Assert.Single(stream.Writes);
        Assert.Equal(1, stream.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteAsync("SELECT 1"));
    }

    public static IEnumerable<object[]> InvalidSets()
    {
        yield return [Array.Empty<string?[]>()];
        foreach (var missing in Required) yield return [Rows().Where(x => x[3] != missing).ToArray()];
        yield return [Rows().Append(Fields("int4", "6000")).ToArray()];
        yield return [Rows().Append(Fields("numeric", "5000")).ToArray()];
        var domain = Rows().ToArray(); domain[2][5] = "d";
        yield return [domain];
        var derived = Rows().ToArray(); derived[2][8] = "99";
        yield return [derived];
    }

    [Theory]
    [MemberData(nameof(InvalidSets))]
    public async Task RequiresUnambiguousRequiredDefinedBaseTypes(string?[][] rows)
    {
        var stream = new QueryStream(Response(rows));
        using var session = Session(stream);
        await Assert.ThrowsAsync<IOException>(() => PgTypeCatalog.LoadAsync(session, Identity()));
        Assert.Equal(1, stream.DisposeCount);
    }

    [Theory]
    [InlineData('T')]
    [InlineData('E')]
    public async Task RejectsNonIdleResults(char status)
    {
        var stream = new QueryStream(Response(Rows(), status));
        using var session = Session(stream);
        await Assert.ThrowsAsync<IOException>(() => PgTypeCatalog.LoadAsync(session, Identity()));
        Assert.Equal(1, stream.DisposeCount);
    }

    [Theory]
    [InlineData("oracle")]
    [InlineData("unknown")]
    public async Task RejectsChangedCompatibilityModeDuringCatalogLoad(string reported)
    {
        var stream = new QueryStream(Join(Status("database_mode", reported), Response(Rows())));
        using var session = Session(stream);
        await Assert.ThrowsAsync<NotSupportedException>(() => PgTypeCatalog.LoadAsync(session, Identity()));
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task IndependentSessionCatalogsDoNotShareOidMaps()
    {
        using var first = Session(new QueryStream(Response(Rows())));
        using var second = Session(new QueryStream(Response(Rows(8000))));
        var one = await PgTypeCatalog.LoadAsync(first, Identity());
        var two = await PgTypeCatalog.LoadAsync(second, Identity());
        Assert.Equal((uint)5002, one.ByName["pg_catalog.int4"].Oid);
        Assert.Equal((uint)8002, two.ByName["pg_catalog.int4"].Oid);
    }

    [Fact]
    public async Task RejectsExtraResultsAndIncorrectDescriptionCount()
    {
        foreach (var bytes in new[] { Join(Response(Rows())[..^6], Response(Rows())),
            Join(Description(11), Complete(), Ready('I')) })
        {
            var stream = new QueryStream(bytes);
            using var session = Session(stream);
            await Assert.ThrowsAsync<IOException>(() => PgTypeCatalog.LoadAsync(session, Identity()));
            Assert.Equal(1, stream.DisposeCount);
        }
    }

    [Fact]
    public async Task ServerErrorsAndTruncationRetireWithoutFallbackOrReplay()
    {
        foreach (var input in new[] { Join(Frame('E', Encoding.ASCII.GetBytes("SERROR\0C42501\0Mprivate-error\0\0")), Ready('I')),
            Response(Rows())[..^1] })
        {
            var stream = new QueryStream(input);
            using var session = Session(stream);
            var error = await Assert.ThrowsAnyAsync<IOException>(() => PgTypeCatalog.LoadAsync(session, Identity()));
            Assert.DoesNotContain("private-error", error.ToString());
            Assert.Single(stream.Writes);
            Assert.Equal(1, stream.DisposeCount);
        }
    }

    [Fact]
    public async Task LocalValidationAndPreCancellationPreserveSession()
    {
        var stream = new QueryStream(Join(Complete(), Ready('T')));
        using var session = Session(stream);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PgTypeCatalog.LoadAsync(session, Identity(), cancelled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => PgTypeCatalog.LoadAsync(session, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => PgTypeCatalog.LoadAsync(null!, Identity()));
        Assert.Empty(stream.Writes);
        Assert.Equal(0, stream.DisposeCount);
        await session.ExecuteAsync("BEGIN");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PgTypeCatalog.LoadAsync(session, Identity()));
        Assert.Single(stream.Writes);
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task CancellationDuringCatalogLoadRetiresSession()
    {
        var stream = new QueryStream([]) { BlockAtEnd = true };
        using var session = Session(stream);
        using var cancelled = new CancellationTokenSource();
        var load = PgTypeCatalog.LoadAsync(session, Identity(), cancelled.Token);
        await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.Equal(1, stream.DisposeCount);
    }

    static PgQuerySession Session(QueryStream stream) => new(new PgWire(stream), new PgStartupResult(
        new Dictionary<string, string> { ["client_encoding"] = "UTF8", ["database_mode"] = "pg" }, new PgBackendKey(1, 2)));
    static PgIdentityResult Identity()
    {
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        return new PgIdentityResult(version!, "kernel-14", KingbaseCompatibilityMode.Auto,
            KingbaseCompatibilityMode.PostgreSql, KingbaseCompatibilityMode.PostgreSql, PgModeSource.ParameterStatus);
    }
    static IEnumerable<string?[]> Rows(int firstOid = 5000) => Required.Select((name, i) => Fields(name,
        (firstOid + i).ToString(CultureInfo.InvariantCulture)));
    static string?[] Fields(string name, string oid) => [oid, "987", "pg_catalog", name, "4", "b", "N", "t", "0", "0", "0", "-1"];
    static byte[] Response(IEnumerable<string?[]> rows, char status = 'I') => Join(Description(12),
        Join(rows.Select(Row).ToArray()), Complete(), Ready(status));
    static byte[] Description(int count) => Frame('T', Join(new[] { (byte)(count >> 8), (byte)count },
        Join(Enumerable.Range(0, count).Select(i => Join(Encoding.UTF8.GetBytes("c" + i + "\0"),
            new byte[6], new byte[] { 0, 0, 0, 25, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0, 0 })).ToArray())));
    static byte[] Row(string?[] fields) => Frame('D', Join(new[] { (byte)(fields.Length >> 8), (byte)fields.Length },
        Join(fields.Select(value => value is null ? Int32(-1) : Join(Int32(Encoding.UTF8.GetByteCount(value)),
            Encoding.UTF8.GetBytes(value))).ToArray())));
    static byte[] Complete() => Frame('C', Encoding.ASCII.GetBytes("SELECT 6\0"));
    static byte[] Status(string key, string value) => Frame('S', Encoding.UTF8.GetBytes(key + "\0" + value + "\0"));
    static byte[] Ready(char status) => Frame('Z', (byte)status);
    static byte[] Query(string sql) => Frame('Q', Encoding.UTF8.GetBytes(sql + "\0"));
    static byte[] Int32(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    static byte[] Frame(char code, params byte[] payload) => Join([(byte)code], Int32(payload.Length + 4), payload);
    static byte[] Join(params byte[][] pieces) => pieces.SelectMany(x => x).ToArray();
}
