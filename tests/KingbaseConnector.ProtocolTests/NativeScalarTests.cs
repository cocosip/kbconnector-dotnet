using System.Data;
using System.Globalization;
using System.Numerics;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class NativeScalarTests
{
    [Theory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ExactAssetNativeScalarProbe(KingbaseCompatibilityMode mode) => KingbaseConnector.Tests.NativeScalarAssetProbe.RunAsync(mode);

    [Theory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public async Task PgTinyintIsEligibleOnlyForTheProvenOracleMode(KingbaseCompatibilityMode mode)
    {
        var catalog = await Load(mode);
        Assert.Equal(mode == KingbaseCompatibilityMode.Oracle, catalog.ByName.ContainsKey("pg_catalog.tinyint"));
        var parameter = new KingbaseParameter("p", (sbyte)-128) { KingbaseDbType = KingbaseDbType.SignedTinyint };
        var binding = Bind(catalog, mode, parameter).Parameters[0];
        var expected = mode == KingbaseCompatibilityMode.Oracle ? "pg_catalog.tinyint" : mode == KingbaseCompatibilityMode.MySql ? "sys.tinyint" : "pg_catalog.int2";
        Assert.Equal(catalog.ByName[expected].Oid, binding.TypeOid);
        Assert.Equal("-128", new PgTextCodecs(catalog).Encode(binding.TypeOid, binding.Value));
    }

    [Theory]
    [InlineData("int16", "4", "N", "b")]
    [InlineData("int16", "16", "S", "b")]
    [InlineData("int16", "16", "N", "d")]
    [InlineData("tinyint", "2", "N", "b")]
    [InlineData("tinyint", "1", "S", "b")]
    [InlineData("tinyint", "1", "N", "d")]
    public async Task UnprovenOptionalPgNumericLayoutsAreOmitted(string name, string size, string category, string kind)
    {
        var rows = BaseRows(); var row = rows.Single(item => item[3] == name); row[4] = size; row[5] = kind; row[6] = category;
        var catalog = await Load(KingbaseCompatibilityMode.Oracle, baseRows: rows);
        Assert.False(catalog.ByName.ContainsKey("pg_catalog." + name));
        if (name == "tinyint") Assert.Equal(catalog.ByName["pg_catalog.int2"].Oid, Bind(catalog, KingbaseCompatibilityMode.Oracle,
            new KingbaseParameter("p", (sbyte)1) { KingbaseDbType = KingbaseDbType.SignedTinyint }).Parameters[0].TypeOid);
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, KingbaseDbType.UnsignedTinyint, DbType.SByte)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, KingbaseDbType.SignedTinyint, DbType.Byte)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, KingbaseDbType.SignedTinyint, DbType.Byte)]
    [InlineData(KingbaseCompatibilityMode.MySql, KingbaseDbType.UnsignedTinyint, DbType.SByte)]
    public async Task ConflictingLogicalTinySelectorsRejectTypedNullBeforeFallback(KingbaseCompatibilityMode mode, KingbaseDbType provider, DbType dbType)
    {
        var catalog = await Load(mode, nativeRows: []);
        Assert.Throws<ArgumentException>(() => Bind(catalog, mode, new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = provider, DbType = dbType }));
    }

    [Theory]
    [InlineData("42501")]
    [InlineData("42P01")]
    [InlineData("42703")]
    [InlineData("42883")]
    [InlineData("0A000")]
    public async Task SynchronizedOptionalDiscoveryRejectionKeepsTheProvenCatalog(string state)
    {
        var catalog = await Load(KingbaseCompatibilityMode.MySql, nativeResponse: Error(state));
        Assert.True(catalog.ByName.ContainsKey("pg_catalog.int4")); Assert.False(catalog.ByName.ContainsKey("sys.uint4"));
        Assert.Throws<NotSupportedException>(() => Bind(catalog, KingbaseCompatibilityMode.MySql,
            new KingbaseParameter("p", 1U) { DataTypeName = "sys.uint4" }));
    }

    [Theory]
    [InlineData("tinyint")]
    [InlineData("int16")]
    public async Task OmittedPgTypesStillReserveTheirOidAcrossNativeDiscovery(string name)
    {
        var pgRows = BaseRows(); var omitted = pgRows.Single(row => row[3] == name);
        if (name == "int16") omitted[4] = "4";
        var nativeRows = NativeRows(KingbaseCompatibilityMode.MySql);
        nativeRows.Single(row => row[3] == "uint4")[0] = omitted[0];
        var (session, _, identity) = Create(KingbaseCompatibilityMode.MySql, baseRows: pgRows, nativeResponse: Response(nativeRows, 16));
        using (session) { await Assert.ThrowsAnyAsync<IOException>(() => PgTypeCatalog.LoadAsync(session, identity)); Assert.True(session.IsDisposed); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("byte")]
    [InlineData("uint")]
    public async Task ConflictingExplicitUnsignedSelectorsRejectEveryValue(string kind)
    {
        var catalog = await Load(KingbaseCompatibilityMode.MySql);
        object value = kind == "null" ? DBNull.Value : kind == "byte" ? (object)(byte)1 : 1U;
        Assert.Throws<ArgumentException>(() => Bind(catalog, KingbaseCompatibilityMode.MySql,
            new KingbaseParameter("p", value) { KingbaseDbType = KingbaseDbType.UnsignedInteger, DbType = DbType.Byte }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogicalByteStillUsesTheNativeUint4PhysicalFallback(bool typedNull)
    {
        var catalog = await Load(KingbaseCompatibilityMode.MySql);
        foreach (var explicitProvider in new[] { false, true })
        {
            var parameter = new KingbaseParameter("p", typedNull ? DBNull.Value : (object)(byte)255) { DbType = DbType.Byte };
            if (explicitProvider) parameter.KingbaseDbType = KingbaseDbType.UnsignedTinyint;
            var binding = Bind(catalog, KingbaseCompatibilityMode.MySql, parameter).Parameters[0];
            Assert.Equal(catalog.ByName["sys.uint4"].Oid, binding.TypeOid);
            Assert.Equal(typedNull ? null : "255", new PgTextCodecs(catalog).Encode(binding.TypeOid, binding.Value));
        }
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public async Task OptionalCatalogPermissionDenialPreservesPublicScalarExecution(KingbaseCompatibilityMode mode)
    {
        using var listener = PublicExecutionTests.Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token, mode: WireMode(mode), nativeCatalogResponse: Error("42501"));
            await PublicExecutionTests.ReadExtended(stream, guard.Token); await stream.WriteAsync(PublicExecutionTests.Value("42"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var settings = PublicExecutionTests.Settings(listener); settings.CompatibilityMode = mode;
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT :p", connection);
        command.Parameters.Add(new KingbaseParameter("p", "private-value") { DataTypeName = "sys.varcharbyte" });
        await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteScalarAsync(guard.Token));
        command.Parameters.Clear(); command.CommandText = "SELECT 42"; Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(0, "10000")]
    [InlineData(1, "90")]
    [InlineData(2, "private_schema")]
    [InlineData(3, "private_type")]
    [InlineData(4, "0")]
    [InlineData(5, "bb")]
    [InlineData(7, "false")]
    [InlineData(8, "-1")]
    [InlineData(9, "x")]
    [InlineData(10, "-1")]
    [InlineData(11, "2147483648")]
    [InlineData(12, null)]
    [InlineData(14, "unknown")]
    [InlineData(15, "yes")]
    public async Task MalformedNativeCatalogRetiresWithoutExposingValues(int column, string? invalid)
    {
        var rows = NativeRows(KingbaseCompatibilityMode.MySql); rows[0][column] = invalid;
        var (session, stream, identity) = Create(KingbaseCompatibilityMode.MySql, nativeResponse: Response(rows, 16));
        using (session)
        {
            var error = await Assert.ThrowsAnyAsync<IOException>(() => PgTypeCatalog.LoadAsync(session, identity));
            Assert.True(session.IsDisposed); Assert.DoesNotContain("private", error.ToString()); Assert.Equal(2, stream.Writes.Count);
        }
    }

    [Theory]
    [InlineData(4, "8")]
    [InlineData(5, "d")]
    [InlineData(6, "N")]
    [InlineData(8, "42")]
    [InlineData(9, "42")]
    [InlineData(12, "private_schema.varcharbytein")]
    [InlineData(13, "sys.unproven_out")]
    [InlineData(14, "false")]
    [InlineData(15, "false")]
    public async Task UntrustedOrUnprovenNativeAliasRemainsUnavailable(int column, string value)
    {
        var rows = NativeRows(KingbaseCompatibilityMode.MySql); rows[0][column] = value;
        var catalog = await Load(KingbaseCompatibilityMode.MySql, nativeRows: rows);
        Assert.False(catalog.ByName.ContainsKey("sys.varcharbyte")); Assert.True(catalog.ByName.ContainsKey("sys.uint8"));
    }

    [Fact]
    public async Task MySqlBitNeverAcquiresSqlServerBooleanSemantics()
    {
        var catalog = await Load(KingbaseCompatibilityMode.MySql);
        Assert.False(catalog.ByName.ContainsKey("sys.bit"));
        Assert.Throws<NotSupportedException>(() => Bind(catalog, KingbaseCompatibilityMode.MySql,
            new KingbaseParameter("p", true) { KingbaseDbType = KingbaseDbType.SqlServerBit }));
    }

    [Fact]
    public async Task WideAndUnsignedScalarsPreserveEveryBitAndRejectInvalidText()
    {
        var catalog = await Load(KingbaseCompatibilityMode.MySql); var codecs = new PgTextCodecs(catalog);
        foreach (var (name, value) in new (string, object)[] { ("sys.uint4", uint.MaxValue), ("sys.uint8", ulong.MaxValue),
            ("sys.tinyint", sbyte.MinValue), ("pg_catalog.int16", -(BigInteger.One << 127)), ("pg_catalog.int16", (BigInteger.One << 127) - 1) })
        {
            var oid = catalog.ByName[name].Oid; var text = codecs.Encode(oid, value);
            Assert.Equal(value.GetType(), codecs.GetFieldType(oid)); Assert.Equal(value, codecs.Decode(oid, text));
            Assert.Equal(value, codecs.DecodeProviderSpecific(oid, text)); Assert.Same(DBNull.Value, codecs.Decode(oid, null));
        }
        var wide = catalog.ByName["pg_catalog.int16"].Oid;
        Assert.Throws<OverflowException>(() => codecs.Encode(wide, BigInteger.One << 127));
        Assert.Throws<OverflowException>(() => codecs.Decode(wide, "170141183460469231731687303715884105728"));
        Assert.Throws<OverflowException>(() => codecs.Decode(wide, new string('0', 41)));
        Assert.Throws<FormatException>(() => codecs.Decode(wide, "1e2"));
        Assert.Throws<InvalidCastException>(() => codecs.Encode(wide, long.MaxValue));
        Assert.Throws<OverflowException>(() => codecs.Decode(catalog.ByName["sys.uint8"].Oid, "18446744073709551616"));
        Assert.Throws<FormatException>(() => codecs.Decode(catalog.ByName["sys.uint4"].Oid, "-1"));
        Assert.Throws<OverflowException>(() => codecs.Decode(catalog.ByName["sys.tinyint"].Oid, "128"));
    }

    [Fact]
    public async Task NativeBitAndStringCodecsKeepTheirSeparateRepresentations()
    {
        var catalog = await Load(KingbaseCompatibilityMode.SqlServer); var codecs = new PgTextCodecs(catalog);
        var bit = catalog.ByName["sys.bit"].Oid; Assert.Equal("1", codecs.Encode(bit, true)); Assert.Equal(false, codecs.Decode(bit, "0"));
        Assert.Throws<FormatException>(() => codecs.Decode(bit, "t"));
        var text = "汉😀 ' \\  ";
        foreach (var name in new[] { "varcharbyte", "bpcharbyte", "nchar", "nvarchar" })
        { var type = catalog.ByName["sys." + name]; Assert.Equal("sys", type.Namespace); Assert.Equal(text, codecs.Decode(type.Oid, codecs.Encode(type.Oid, text))); }
        var narrow = catalog.ByName["sys.tinyint"].Oid;
        Assert.Equal(byte.MaxValue, codecs.Decode(narrow, "255")); Assert.Throws<OverflowException>(() => codecs.Decode(narrow, "256"));
    }

    [Fact]
    public async Task UnsignedTypedFallbackGettersCheckTheFullRange()
    {
        var catalog = await Load(KingbaseCompatibilityMode.PostgreSql);
        foreach (var (name, text) in new[] { ("int8", "4294967295"), ("int16", "18446744073709551615") })
        {
            var type = catalog.ByName["pg_catalog." + name];
            using var reader = new KingbaseDataReader(new[] { new PgTextResult(new[] { new PgColumnDescription("v", 0, 0, type.Oid, type.Size, -1) }, new[] { new string?[] { text } }, "SELECT 1") }, catalog);
            Assert.True(reader.Read());
            if (name == "int8") Assert.Equal(uint.MaxValue, reader.GetFieldValue<uint>(0)); else Assert.Equal(ulong.MaxValue, reader.GetFieldValue<ulong>(0));
        }
        foreach (var (name, text) in new[] { ("int8", "-1"), ("int8", "4294967296"), ("int16", "-1"), ("int16", "18446744073709551616") })
        {
            var type = catalog.ByName["pg_catalog." + name];
            using var reader = new KingbaseDataReader(new[] { new PgTextResult(new[] { new PgColumnDescription("v", 0, 0, type.Oid, type.Size, -1) }, new[] { new string?[] { text } }, "SELECT 1") }, catalog);
            Assert.True(reader.Read());
            if (name == "int8") Assert.Throws<OverflowException>(() => reader.GetFieldValue<uint>(0)); else Assert.Throws<OverflowException>(() => reader.GetFieldValue<ulong>(0));
        }
    }

    [Theory]
    [InlineData("UPDATE 1", 'I')]
    [InlineData("SELECT 6", 'T')]
    [InlineData("SELECT 6", 'E')]
    public async Task UnexpectedNativeCompletionOrTransactionStateRetires(string tag, char status)
    {
        var rows = NativeRows(KingbaseCompatibilityMode.MySql);
        var (session, _, identity) = Create(KingbaseCompatibilityMode.MySql, nativeResponse: Response(rows, 16, tag, status));
        using (session) { await Assert.ThrowsAnyAsync<IOException>(() => PgTypeCatalog.LoadAsync(session, identity)); Assert.True(session.IsDisposed); }
    }

    [Theory]
    [InlineData("42501", 'T', "ERROR")]
    [InlineData("42501", 'E', "ERROR")]
    [InlineData("42501", 'I', "FATAL")]
    [InlineData("42601", 'I', "ERROR")]
    [InlineData("XX000", 'I', "ERROR")]
    public async Task NativeRejectionWithoutAReusableIdleTailDoesNotDegradeSilently(string state, char status, string severity)
    {
        var response = Join(Frame('E', Encoding.ASCII.GetBytes("S" + severity + "\0C" + state + "\0Mprivate-catalog-error\0\0")), Frame('Z', [(byte)status]));
        var (session, _, identity) = Create(KingbaseCompatibilityMode.MySql, nativeResponse: response);
        using (session) { await Assert.ThrowsAnyAsync<IOException>(() => PgTypeCatalog.LoadAsync(session, identity)); Assert.True(session.IsDisposed); }
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("success")]
    public async Task ModeDriftDuringOptionalDiscoveryAlwaysRetires(string path)
    {
        var response = Join(Status("database_mode", "oracle"), path == "permission" ? Error("42501") : Response(NativeRows(KingbaseCompatibilityMode.MySql), 16));
        var (session, _, identity) = Create(KingbaseCompatibilityMode.MySql, nativeResponse: response);
        using (session) { await Assert.ThrowsAsync<NotSupportedException>(() => PgTypeCatalog.LoadAsync(session, identity)); Assert.True(session.IsDisposed); }
    }

    [Fact]
    public async Task NativeIdentityDuplicatesAndNamespaceDisagreementNeverPublish()
    {
        foreach (var mutation in new[] { "oid", "name", "namespace" })
        {
            var rows = NativeRows(KingbaseCompatibilityMode.MySql);
            if (mutation == "namespace") rows[1][1] = "92";
            else { var duplicate = (string?[])rows[0].Clone(); duplicate[0] = mutation == "oid" ? rows[0][0] : "30000"; rows.Add(duplicate); }
            await Assert.ThrowsAnyAsync<IOException>(() => Load(KingbaseCompatibilityMode.MySql, nativeRows: rows));
        }
    }

    [Fact]
    public async Task CancellationDuringNativeDiscoveryRetiresTheSession()
    {
        var stream = new QueryStream(Response(BaseRows(), 12)) { BlockAtEnd = true };
        using var session = new PgQuerySession(new PgWire(stream), new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8", ["database_mode"] = "mysql" }, new PgBackendKey(1, 2)));
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        var identity = new PgIdentityResult(version!, "14", KingbaseCompatibilityMode.MySql, KingbaseCompatibilityMode.MySql, KingbaseCompatibilityMode.MySql, PgModeSource.ParameterStatus);
        using var canceled = new CancellationTokenSource();
        var load = PgTypeCatalog.LoadAsync(session, identity, canceled.Token); await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2)); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load); Assert.True(session.IsDisposed); Assert.Equal(2, stream.Writes.Count);
    }

    static PgParameterBindings Bind(PgTypeCatalog catalog, KingbaseCompatibilityMode mode, KingbaseParameter parameter)
    { var values = new KingbaseParameterCollection { parameter }; return PgParameterBindings.Create("SELECT :p", values, catalog, mode, true); }
    static async Task<PgTypeCatalog> Load(KingbaseCompatibilityMode mode, List<string?[]>? nativeRows = null, List<string?[]>? baseRows = null, byte[]? nativeResponse = null)
    {
        var (session, _, identity) = Create(mode, baseRows, nativeResponse ?? Response(nativeRows ?? NativeRows(mode), 16));
        using (session) return await PgTypeCatalog.LoadAsync(session, identity);
    }
    static (PgQuerySession Session, QueryStream Stream, PgIdentityResult Identity) Create(KingbaseCompatibilityMode mode, List<string?[]>? baseRows = null, byte[]? nativeResponse = null)
    {
        var stream = new QueryStream(Join(Response(baseRows ?? BaseRows(), 12), mode == KingbaseCompatibilityMode.PostgreSql ? [] : nativeResponse ?? Response(NativeRows(mode), 16)));
        var session = new PgQuerySession(new PgWire(stream), new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8", ["database_mode"] = WireMode(mode) }, new PgBackendKey(1, 2)));
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        return (session, stream, new PgIdentityResult(version!, "14", mode, mode, mode, PgModeSource.ParameterStatus));
    }
    static List<string?[]> BaseRows() => new[] { ("bool", "1", "B"), ("int2", "2", "N"), ("int4", "4", "N"), ("int8", "8", "N"),
        ("text", "-1", "S"), ("bytea", "-1", "U"), ("int16", "16", "N"), ("tinyint", "1", "N") }
        .Select((type, i) => Fields(10000 + i, "90", "pg_catalog", type.Item1, type.Item2, type.Item3)).ToList();
    static List<string?[]> NativeRows(KingbaseCompatibilityMode mode)
    {
        var rows = new List<string?[]>();
        Add("varcharbyte", "-1", "S", "sys.varcharbytein", "sys.varcharbyteout"); Add("bpcharbyte", "-1", "S", "sys.bpcharbytein", "sys.bpcharbyteout");
        if (mode == KingbaseCompatibilityMode.MySql)
        {
            Add("tinyint", "1", "N", "pg_catalog.tinyintin", "pg_catalog.tinyintout");
            Add("uint4", "4", "N", "pg_catalog.uint4in", "pg_catalog.uint4out"); Add("uint8", "8", "N", "pg_catalog.uint8in", "pg_catalog.uint8out");
            Add("bit", "-1", "V", "sys.mysql_bit_in", "sys.mysql_bit_out");
        }
        if (mode == KingbaseCompatibilityMode.SqlServer)
        {
            Add("tinyint", "1", "N", "sys.sqlserver_tinyint_in", "sys.sqlserver_tinyint_out"); Add("bit", "1", "B", "sys.sqlserver_bit_in", "sys.sqlserver_bit_out");
            Add("nchar", "-1", "S", "sys.nchar_in", "sys.nchar_out"); Add("nvarchar", "-1", "S", "sys.nvarchar_in", "sys.nvarchar_out");
        }
        return rows;
        void Add(string name, string size, string category, string input, string output) => rows.Add(Fields(20000 + rows.Count, "91", "sys", name, size, category).Concat(new string?[] { input, output, "t", "t" }).ToArray());
    }
    static string?[] Fields(int oid, string ns, string schema, string name, string size, string category) =>
        [oid.ToString(CultureInfo.InvariantCulture), ns, schema, name, size, "b", category, "t", "0", "0", "0", "-1"];
    static byte[] Response(List<string?[]> rows, int width, string? tag = null, char status = 'I') => Join(Description(width), Join(rows.Select(Row).ToArray()),
        Frame('C', Encoding.ASCII.GetBytes((tag ?? "SELECT " + rows.Count.ToString(CultureInfo.InvariantCulture)) + "\0")), Frame('Z', [(byte)status]));
    static byte[] Error(string state) => Join(Frame('E', Encoding.ASCII.GetBytes("SERROR\0C" + state + "\0Mprivate-catalog-error\0\0")), Ready());
    static string WireMode(KingbaseCompatibilityMode mode) => mode switch
    { KingbaseCompatibilityMode.Oracle => "oracle", KingbaseCompatibilityMode.MySql => "mysql", KingbaseCompatibilityMode.SqlServer => "sqlserver", _ => "pg" };
}
