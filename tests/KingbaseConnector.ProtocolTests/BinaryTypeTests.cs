using System.Data;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

// E72: sys.binary/sys.varbinary native contract. The types carry raw bytes but take literal text,
// so reads decode "0x"+hex, binary COPY carries the raw fields, and scalar writes are refused.
public sealed class BinaryTypeTests
{
    [Theory]
    [InlineData(KingbaseCompatibilityMode.MySql, "binaryin", "binaryout", 'S')]
    [InlineData(KingbaseCompatibilityMode.MySql, "varbinaryin", "varbinaryout", 'S')]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "sqlserver_binaryin", "sqlserver_binaryout", 'U')]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "sqlserver_varbinaryin", "sqlserver_varbinaryout", 'U')]
    public async Task NativeBinaryTypesResolveAsByteArrays(KingbaseCompatibilityMode mode, string input, string output, char category)
    {
        var name = input.Contains("varbinary", StringComparison.Ordinal) ? "varbinary" : "binary";
        var catalog = await Load(mode, name, input, output, category);
        var type = catalog.ByName["sys." + name];
        Assert.Equal(name, type.CodecName);
        Assert.Equal(-1, type.Size);
        Assert.Equal(typeof(byte[]), new PgTextCodecs(catalog).GetFieldType(type.Oid));
        Assert.True(new PgTextCodecs(catalog).NeedsMaterializedBytes(type.Oid));
    }

    [Theory]
    [InlineData("0x6162", new byte[] { 0x61, 0x62 })]
    [InlineData("0x", new byte[] { })]
    [InlineData("0x00FF", new byte[] { 0x00, 0xFF })]
    [InlineData("0X6162", new byte[] { 0x61, 0x62 })]
    public async Task TextResultsAreZeroXPrefixedHex(string text, byte[] expected)
    {
        var catalog = await Load(KingbaseCompatibilityMode.SqlServer, "varbinary", "sqlserver_varbinaryin", "sqlserver_varbinaryout", 'U');
        Assert.Equal(expected, new PgTextCodecs(catalog).Decode(catalog.ByName["sys.varbinary"].Oid, text));
    }

    [Theory]
    [InlineData("6162")]
    [InlineData("0x6")]
    [InlineData("0xzz")]
    [InlineData("0x616")]
    [InlineData("\\x6162")]
    [InlineData("")]
    public async Task MalformedBinaryTextIsRejected(string text)
    {
        var catalog = await Load(KingbaseCompatibilityMode.SqlServer, "varbinary", "sqlserver_varbinaryin", "sqlserver_varbinaryout", 'U');
        Assert.Throws<FormatException>(() => new PgTextCodecs(catalog).Decode(catalog.ByName["sys.varbinary"].Oid, text));
    }

    [Fact]
    public async Task NullResultIsDbNull()
    {
        var catalog = await Load(KingbaseCompatibilityMode.MySql, "varbinary", "varbinaryin", "varbinaryout", 'S');
        Assert.Same(DBNull.Value, new PgTextCodecs(catalog).Decode(catalog.ByName["sys.varbinary"].Oid, null));
    }

    [Fact]
    public async Task TextParameterEncodingIsRefusedForBinaryTypes()
    {
        var catalog = await Load(KingbaseCompatibilityMode.SqlServer, "varbinary", "sqlserver_varbinaryin", "sqlserver_varbinaryout", 'U');
        Assert.Throws<NotSupportedException>(() =>
            new PgTextCodecs(catalog).Encode(catalog.ByName["sys.varbinary"].Oid, new byte[] { 1, 2 }));
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.MySql, KingbaseDbType.Binary)]
    [InlineData(KingbaseCompatibilityMode.MySql, KingbaseDbType.VarBinary)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, KingbaseDbType.Binary)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, KingbaseDbType.VarBinary)]
    public async Task BinarySelectorsResolveButParameterWritesAreRejected(KingbaseCompatibilityMode mode, KingbaseDbType selector)
    {
        var catalog = await LoadAll(mode);
        var values = new KingbaseParameterCollection { new KingbaseParameter("p", new byte[] { 1 }) { KingbaseDbType = selector } };
        // The server takes literal bytes for these types, and its Bind rejects the binary parameter format,
        // so a scalar write is refused instead of silently storing the ASCII of the value.
        var error = Assert.Throws<NotSupportedException>(() => PgParameterBindings.Create("SELECT :p", values, catalog, mode, true));
        Assert.Contains("text parameter format", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ByteArraysStillDefaultToBytea()
    {
        var catalog = await LoadAll(KingbaseCompatibilityMode.SqlServer);
        var values = new KingbaseParameterCollection { new KingbaseParameter("p", new byte[] { 1 }) };
        var binding = PgParameterBindings.Create("SELECT :p", values, catalog, KingbaseCompatibilityMode.SqlServer, true);
        Assert.Equal(catalog.ByName["pg_catalog.bytea"].Oid, binding.Parameters[0].TypeOid);
    }

    [Fact]
    public async Task SequentialFieldAccessMaterializesPrefixedBinaryValues()
    {
        using var listener = PublicExecutionTests.Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token, mode: "mysql",
                nativeCatalogResponse: Join(Description(16), Join(Row(NativeRow(20000, "binary", "binaryin", "binaryout", 'S'))), Complete(), Ready()));
            await PublicExecutionTests.ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 20000), Row(["0x6162"]),
                Frame('C', System.Text.Encoding.ASCII.GetBytes("SELECT 1\0")), Ready()), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var settings = PublicExecutionTests.Settings(listener);
        settings.CompatibilityMode = KingbaseCompatibilityMode.MySql;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT v", connection);
        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, guard.Token);
        Assert.True(await reader.ReadAsync(guard.Token));
        await using (var field = reader.GetStream(0))
        using (var memory = new MemoryStream())
        {
            await field.CopyToAsync(memory, guard.Token);
            Assert.Equal(new byte[] { 0x61, 0x62 }, memory.ToArray());
        }
        Assert.False(await reader.ReadAsync(guard.Token));
        connection.Close();
        await peer.WaitAsync(guard.Token);
    }
    [Theory]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public async Task BinaryCopyResolvesAndRoundTripsTheNativeTypes(KingbaseCompatibilityMode mode)
    {
        var catalog = await LoadAll(mode);
        var names = PgBinaryCopyCodec.Resolve(catalog, [KingbaseDbType.Binary, KingbaseDbType.VarBinary]);
        Assert.Equal(new[] { "binary", "varbinary" }, names);
        var payload = new byte[] { 0x00, 0x61, 0xFF };
        var encoded = PgBinaryCopyCodec.Encode("binary", payload, PgBinaryCopyCodec.MaxRowBytes);
        Assert.Equal(payload, encoded);
        Assert.NotSame(payload, encoded);
        Assert.Equal(payload, PgBinaryCopyCodec.Decode("varbinary", encoded!));
        Assert.Null(PgBinaryCopyCodec.Encode("binary", null, PgBinaryCopyCodec.MaxRowBytes));
        Assert.Throws<InvalidCastException>(() => PgBinaryCopyCodec.Encode("binary", "text", PgBinaryCopyCodec.MaxRowBytes));
    }



    static Task<PgTypeCatalog> Load(KingbaseCompatibilityMode mode, string name, string input, string output, char category) =>
        NativeScalarTests.Load(mode, nativeRows: [NativeRow(20000, name, input, output, category)]);

    internal static Task<PgTypeCatalog> LoadAll(KingbaseCompatibilityMode mode) => NativeScalarTests.Load(mode, nativeRows:
    [
        NativeRow(20000, "binary", mode == KingbaseCompatibilityMode.MySql ? "binaryin" : "sqlserver_binaryin",
            mode == KingbaseCompatibilityMode.MySql ? "binaryout" : "sqlserver_binaryout",
            mode == KingbaseCompatibilityMode.MySql ? 'S' : 'U'),
        NativeRow(20001, "varbinary", mode == KingbaseCompatibilityMode.MySql ? "varbinaryin" : "sqlserver_varbinaryin",
            mode == KingbaseCompatibilityMode.MySql ? "varbinaryout" : "sqlserver_varbinaryout",
            mode == KingbaseCompatibilityMode.MySql ? 'S' : 'U')
    ]);

    static string?[] NativeRow(int oid, string name, string input, string output, char category) =>
        [oid.ToString(System.Globalization.CultureInfo.InvariantCulture), "91", "sys", name, "-1", "b", category.ToString(), "t", "0", "0", "0", "-1",
            "sys." + input, "sys." + output, "t", "t"];
}
