using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public sealed class OrdinaryLobTests
{
    [Fact]
    public async Task OracleLobSelectionIgnoresAnUnselectedSameNameSysDomain()
    {
        var rows = Rows(KingbaseCompatibilityMode.Oracle); var other = (string?[])rows.Single(row => row[3] == "blob").Clone();
        other[0] = "50000"; other[1] = "91"; other[2] = "sys"; rows.Add(other);
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.Oracle, nativeRows: rows);
        Assert.True(catalog.ByName.ContainsKey("pg_catalog.blob")); Assert.True(catalog.ByName.ContainsKey("sys.varcharbyte")); Assert.False(catalog.ByName.ContainsKey("sys.blob"));
    }

    [Fact]
    public async Task PartialBinaryReadsDecodeOncePerRowAndColumnWithoutSharingMutablePublicArrays()
    {
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql); var oid = catalog.ByName["pg_catalog.bytea"].Oid;
        var columns = new[] { new PgColumnDescription("a", 0, 0, oid, -1, -1), new PgColumnDescription("b", 0, 0, oid, -1, -1) };
        var escaped = string.Concat(Enumerable.Repeat("\\000", 200 * 1024));
        using var reader = new KingbaseDataReader([new PgTextResult(columns, [new string?[] { escaped, "B" }, new string?[] { "C", "D" }], "SELECT 2"), new PgTextResult(columns, [new string?[] { "E", "F" }], "SELECT 1")], catalog);
        Assert.True(reader.Read()); var destination = new byte[1024]; var small = new byte[1];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var offset = 0; offset < 200 * 1024; offset += destination.Length)
        { Assert.Equal(1024, reader.GetBytes(0, offset, destination, 0, destination.Length)); Assert.Equal(1, reader.GetBytes(1, 0, small, 0, 1)); }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 2 * 1024 * 1024);
        var exposed = reader.GetFieldValue<byte[]>(0); exposed[0] = 123; var provider = (byte[])reader.GetProviderSpecificValue(0); provider[0] = 124;
        Assert.Equal(1, reader.GetBytes(0, 0, small, 0, 1)); Assert.Equal(0, small[0]); using var stream = reader.GetStream(0); Assert.Equal(0, stream.ReadByte());
        Assert.True(reader.Read()); Assert.Equal(1, reader.GetBytes(0, 0, small, 0, 1)); Assert.Equal((byte)'C', small[0]);
        Assert.True(reader.NextResult()); Assert.True(reader.Read()); Assert.Equal(1, reader.GetBytes(0, 0, small, 0, 1)); Assert.Equal((byte)'E', small[0]);
        reader.Close(); Assert.Throws<InvalidOperationException>(() => reader.GetBytes(0, 0, small, 0, 1));
    }

    [Fact]
    public async Task SmallCharacterSlicesDoNotRescanTheEntireValueAndStillValidateEachNewRow()
    {
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql); var oid = catalog.ByName["pg_catalog.text"].Oid;
        var columns = new[] { new PgColumnDescription("text", 0, 0, oid, -1, -1) }; var text = new string('x', 512 * 1024);
        using var reader = new KingbaseDataReader([new PgTextResult(columns, [new string?[] { text }, new string?[] { "bad\0text" }], "SELECT 2"), new PgTextResult(columns, [new string?[] { "y" }], "SELECT 1")], catalog);
        Assert.True(reader.Read()); var buffer = new char[1]; reader.GetChars(0, 0, buffer, 0, 1); var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 256; i++) Assert.Same(text, reader.GetString(0)); var fullValidationTicks = watch.ElapsedTicks;
        watch.Restart(); long copied = 0; for (var i = 0; i < 65536; i++) copied += reader.GetChars(0, i, buffer, 0, 1);
        var sliceTicks = watch.ElapsedTicks; Assert.Equal(65536, copied); Assert.Equal('x', buffer[0]);
        Assert.True(sliceTicks <= fullValidationTicks * 16 + System.Diagnostics.Stopwatch.Frequency / 4, "Small slices repeatedly validated the full text value.");
        Assert.True(reader.Read()); Assert.Throws<FormatException>(() => reader.GetChars(0, 0, buffer, 0, 1));
        Assert.True(reader.NextResult()); Assert.True(reader.Read()); Assert.Equal(1, reader.GetChars(0, 0, buffer, 0, 1)); Assert.Equal('y', buffer[0]);
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    public async Task TrustedLobDomainsBindActualOidsAndReadBytesOrUnicodeWithoutLocators(KingbaseCompatibilityMode mode)
    {
        var catalog = await NativeScalarTests.Load(mode, nativeRows: Rows(mode)); var codecs = new PgTextCodecs(catalog); var schema = Schema(mode);
        foreach (var name in new[] { "blob", "clob", "nclob" })
        {
            var type = catalog.ByName[schema + "." + name]; var binary = name == "blob";
            var selector = Enum.Parse<KingbaseDbType>(name == "nclob" ? "NClob" : binary ? "Blob" : "Clob");
            object value = binary ? new byte[] { 0, 1, 255, 92 } : "LOB_文字😀";
            var parameter = new KingbaseParameter("v", value) { KingbaseDbType = selector, DbType = binary ? DbType.Binary : DbType.String };
            Assert.Equal(binary ? DbType.Binary : DbType.String, parameter.DbType);
            var bindings = Bind(catalog, mode, parameter); Assert.Equal(type.Oid, bindings.Parameters[0].TypeOid);
            if (value is byte[] bytes) { Assert.NotSame(bytes, bindings.Parameters[0].Value); Assert.Equal(bytes, bindings.Parameters[0].Value); }
            var encoded = codecs.Encode(type.Oid, bindings.Parameters[0].Value); Assert.Equal(binary ? "\\x0001ff5c" : value, encoded);
            using var reader = new KingbaseDataReader([new PgTextResult([new PgColumnDescription("v", 0, 0, type.Oid, -1, -1)], [new string?[] { binary ? "\\000\\001\\377\\\\" : (string)value }], "SELECT 1")], catalog);
            Assert.True(reader.Read()); Assert.Equal(binary ? typeof(byte[]) : typeof(string), reader.GetFieldType(0));
            if (binary)
            {
                Assert.Equal(4, reader.GetBytes(0, 0, null, 0, 0)); var partial = new byte[2]; Assert.Equal(2, reader.GetBytes(0, 1, partial, 0, 2)); Assert.Equal(new byte[] { 1, 255 }, partial);
                using var stream = reader.GetStream(0); using var copy = new MemoryStream(); await stream.CopyToAsync(copy); Assert.Equal((byte[])value, copy.ToArray());
                Assert.Throws<ArgumentException>(() => Bind(catalog, mode, new KingbaseParameter("v", value) { KingbaseDbType = selector, Size = 3 }));
            }
            else
            {
                Assert.Equal(((string)value).Length, reader.GetChars(0, 0, null, 0, 0)); var partial = new char[2]; Assert.Equal(2, reader.GetChars(0, 4, partial, 0, 2)); Assert.Equal("文字", new string(partial));
                using var text = reader.GetTextReader(0); Assert.Equal(value, await text.ReadToEndAsync());
            }
            Assert.Equal(type.Oid, Bind(catalog, mode, new KingbaseParameter("v", DBNull.Value) { KingbaseDbType = selector }).Parameters[0].TypeOid);
            Assert.Null(codecs.Encode(type.Oid, DBNull.Value));
            Assert.Equal(type.Oid, Bind(catalog, mode, new KingbaseParameter("v", value) { DataTypeName = schema + "." + name }).Parameters[0].TypeOid);
        }
    }

    [Theory]
    [InlineData(4, "4")]
    [InlineData(5, "b")]
    [InlineData(6, "N")]
    [InlineData(8, "10004")]
    [InlineData(12, "sys.untrusted_in")]
    [InlineData(13, "pg_catalog.byteaout")]
    [InlineData(14, "f")]
    [InlineData(15, "f")]
    public async Task UnprovenDomainIdentityCannotPublishAnOrdinaryLobCodec(int field, string value)
    {
        var rows = Rows(KingbaseCompatibilityMode.MySql); var blob = rows.Single(row => row[3] == "blob"); blob[field] = value;
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.MySql, nativeRows: rows); Assert.False(catalog.ByName.ContainsKey("sys.blob"));
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public async Task UnavailableModesRejectLobSelectorsRatherThanFallingBackToByteaOrOid(KingbaseCompatibilityMode mode)
    {
        var catalog = await NativeScalarTests.Load(mode);
        foreach (var name in new[] { "Blob", "Clob", "NClob" })
            Assert.Throws<NotSupportedException>(() => Bind(catalog, mode, new KingbaseParameter("v", DBNull.Value) { KingbaseDbType = Enum.Parse<KingbaseDbType>(name) }));
    }

    internal static List<string?[]> Rows(KingbaseCompatibilityMode mode)
    {
        var rows = NativeScalarTests.NativeRows(mode); var schema = Schema(mode);
        foreach (var name in new[] { "blob", "clob", "nclob" })
        {
            var binary = name == "blob";
            rows.Add([(40000 + rows.Count).ToString(System.Globalization.CultureInfo.InvariantCulture), schema == "sys" ? "91" : "90", schema, name, "-1", "d", binary ? "U" : "S", "t", binary ? "10005" : "10004", "0", "0", "-1",
                binary ? "pg_catalog.byteain" : "pg_catalog.textin", binary ? "pg_catalog.blobout" : "pg_catalog.textout", "t", "t"]);
        }
        return rows;
    }
    static string Schema(KingbaseCompatibilityMode mode) => mode == KingbaseCompatibilityMode.Oracle ? "pg_catalog" : "sys";
    static PgParameterBindings Bind(PgTypeCatalog catalog, KingbaseCompatibilityMode mode, KingbaseParameter parameter)
    { var parameters = new KingbaseParameterCollection(); parameters.Add(parameter); return PgParameterBindings.Create("SELECT :v", parameters, catalog, mode, true); }
}
