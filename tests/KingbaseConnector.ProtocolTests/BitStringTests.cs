using System.Collections;
using System.Data;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class BitStringTests
{
    [Theory]
    [InlineData("bit")]
    [InlineData("varbit")]
    public async Task BitStringsKeepEveryBitLengthAndFreshMutableResults(string name)
    {
        var catalog = await Load(); var codecs = new PgTextCodecs(catalog); var oid = catalog.ByName["pg_catalog." + name].Oid;
        Assert.Equal(typeof(BitArray), codecs.GetFieldType(oid)); Assert.Equal(typeof(BitArray), codecs.GetProviderSpecificFieldType(oid));
        foreach (var text in new[] { "", "0", "1", "000001010", "10000000100000001" })
        {
            Assert.Equal(text, codecs.Encode(oid, Bits(text)));
            var decoded = Assert.IsType<BitArray>(codecs.Decode(oid, text)); Assert.Equal(text, Text(decoded));
            if (decoded.Length > 0) decoded[0] = !decoded[0];
            Assert.Equal(text, Text(Assert.IsType<BitArray>(codecs.DecodeProviderSpecific(oid, text))));
        }
        Assert.Same(DBNull.Value, codecs.Decode(oid, null)); Assert.Null(codecs.Encode(oid, DBNull.Value));
        Assert.Throws<InvalidCastException>(() => codecs.Encode(oid, true));
        Assert.Throws<InvalidCastException>(() => codecs.Encode(oid, "01"));
        Assert.Throws<InvalidCastException>(() => codecs.Encode(oid, new byte[] { 1 }));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("+01")]
    [InlineData("-01")]
    [InlineData(" 01")]
    [InlineData("01 ")]
    [InlineData("０１")]
    [InlineData("B'01'")]
    public async Task NonBitSyntaxRejectsWithoutLeakingTheValue(string text)
    {
        var catalog = await Load(); var codecs = new PgTextCodecs(catalog);
        foreach (var name in new[] { "bit", "varbit" })
        { var error = Assert.Throws<FormatException>(() => codecs.Decode(catalog.ByName["pg_catalog." + name].Oid, text)); Assert.DoesNotContain(text, error.Message); }
    }

    [Fact]
    public async Task BitArrayBoundsAreCheckedBeforeTextAllocation()
    {
        var catalog = await Load(); var codecs = new PgTextCodecs(catalog); var oid = catalog.ByName["pg_catalog.varbit"].Oid;
        Assert.Equal(1024 * 1024, Assert.IsType<BitArray>(codecs.Decode(oid, new string('0', 1024 * 1024))).Length);
        Assert.Equal(1024 * 1024, codecs.Encode(oid, new BitArray(1024 * 1024))!.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => codecs.Decode(oid, new string('0', 1024 * 1024 + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => codecs.Encode(oid, new BitArray(1024 * 1024 + 1)));
    }

    [Fact]
    public async Task BindingInfersVarbitAndSnapshotsMutableBits()
    {
        var catalog = await Load(); var input = Bits("00101");
        var binding = Bind(catalog, new KingbaseParameter("p", input)).Parameters[0];
        Assert.Equal(catalog.ByName["pg_catalog.varbit"].Oid, binding.TypeOid); input.SetAll(true);
        Assert.Equal("00101", Text(Assert.IsType<BitArray>(binding.Value)));
        Assert.Equal("Varbit", new KingbaseParameter("p", input).KingbaseDbType.ToString());
        foreach (var name in new[] { "Bit", "Varbit" })
        {
            Assert.True(Enum.TryParse<KingbaseDbType>(name, out var provider)); Assert.True((int)provider > (int)KingbaseDbType.SqlServerBit);
            var parameter = new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = provider, DbType = DbType.Object };
            Assert.Equal(catalog.ByName["pg_catalog." + name.ToLowerInvariant()].Oid, Bind(catalog, parameter).Parameters[0].TypeOid);
        }
    }

    [Fact]
    public async Task StandardSchemaUsesBitArrayAndOneBestMatchWithoutClaimingBoolean()
    {
        var catalog = await Load(); Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        using var table = KingbaseSchema.Create("DataTypes", version!, catalog);
        var rows = table.Rows.Cast<DataRow>().Where(row => (string)row["DataType"] == typeof(BitArray).FullName).ToArray();
        Assert.Equal(2, rows.Length); Assert.Single(rows, row => (bool)row["IsBestMatch"]);
        Assert.True((bool)rows.Single(row => (string)row["TypeName"] == "varbit")["IsBestMatch"]);
        Assert.Same(DBNull.Value, rows.Single(row => (string)row["TypeName"] == "bit")["IsFixedLength"]);
        Assert.All(rows, row => Assert.True((bool)row["IsLong"]));
    }

    [Theory]
    [InlineData(DbType.Boolean)]
    [InlineData(DbType.Binary)]
    [InlineData(DbType.String)]
    [InlineData(DbType.Int32)]
    public async Task BitStringsDoNotAcquireBooleanNumericOrStringSelectors(DbType constraint)
    {
        var catalog = await Load();
        Assert.Throws<ArgumentException>(() => Bind(catalog, new KingbaseParameter("p", DBNull.Value) { DataTypeName = "pg_catalog.bit", DbType = constraint }));
        Assert.Throws<NotSupportedException>(() => Bind(catalog, new KingbaseParameter("p", Bits("01")) { Size = 2 }));
        Assert.Throws<NotSupportedException>(() => Bind(catalog, new KingbaseParameter("p", Bits("01")) { Precision = 2 }));
    }

    [Theory]
    [InlineData("bit", "4", "V", "b")]
    [InlineData("varbit", "-1", "S", "b")]
    [InlineData("bit", "-1", "V", "d")]
    public async Task UnprovenOptionalLayoutsRemainUnavailable(string name, string size, string category, string kind)
    {
        var catalog = await Load(name, short.Parse(size), category[0], kind[0]);
        Assert.False(catalog.ByName.ContainsKey("pg_catalog." + name)); Assert.True(catalog.ByName.ContainsKey("pg_catalog.text"));
    }

    [Theory]
    [InlineData("bit", 5)]
    [InlineData("varbit", 17)]
    [InlineData("bit", -1)]
    [InlineData("varbit", 0)]
    public async Task ReaderReportsDeclaredBitCountWithoutDependingOnRowContent(string name, int modifier)
    {
        var catalog = await Load(); var oid = catalog.ByName["pg_catalog." + name].Oid;
        using var reader = new KingbaseDataReader(new[] { new PgTextResult(new[] { new PgColumnDescription("v", 0, 0, oid, -1, modifier) }, new[] { new string?[] { "00101" }, new string?[] { null } }, "SELECT 2") }, catalog);
        Assert.Equal(modifier > 0 ? modifier : (int?)null, reader.GetColumnSchema()[0].ColumnSize);
        Assert.True(reader.Read()); Assert.Equal("00101", Text(reader.GetFieldValue<BitArray>(0)));
        reader.GetFieldValue<BitArray>(0).SetAll(true); Assert.Equal("00101", Text(reader.GetFieldValue<BitArray>(0)));
        Assert.Equal(typeof(BitArray), reader.GetSchemaTable()!.Rows[0]["DataType"]);
        Assert.Throws<InvalidCastException>(() => reader.GetBoolean(0));
        Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.Equal(typeof(BitArray), reader.GetFieldType(0));
    }

    [Fact]
    public async Task UnlimitedAdapterBatchRejectsOversizedBitsBeforeQueueingAndContinuesValidRows()
    {
        using var listener = PublicExecutionTests.Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(PublicExecutionTests.Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token, catalogTypes: Types());
            foreach (var text in new[] { "00101", "101" })
            {
                var frames = await PublicExecutionTests.ReadExtended(stream, guard.Token);
                var suffix = Join(Int32(text.Length), System.Text.Encoding.ASCII.GetBytes(text), new byte[2]);
                Assert.True(frames[1].AsSpan(frames[1].Length - suffix.Length).SequenceEqual(suffix));
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('n'), Frame('C', System.Text.Encoding.ASCII.GetBytes("INSERT 0 1\0")), Ready()), guard.Token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("INSERT INTO public.items VALUES(:bits)", connection) { UpdatedRowSource = UpdateRowSource.None };
        command.Parameters.Add(new KingbaseParameter { ParameterName = "bits", DataTypeName = "pg_catalog.varbit", SourceColumn = "bits" });
        using var adapter = new KingbaseDataAdapter { InsertCommand = command, UpdateBatchSize = 0, ContinueUpdateOnError = true };
        using var table = new DataTable(); table.Columns.Add("bits", typeof(BitArray)); var oversized = new BitArray(1024 * 1024 + 1);
        table.Rows.Add(Bits("00101")); table.Rows.Add(oversized); table.Rows.Add(oversized); table.Rows.Add(Bits("101"));
        Assert.Equal(2, adapter.Update(table)); Assert.Equal(DataRowState.Unchanged, table.Rows[0].RowState); Assert.Equal(DataRowState.Unchanged, table.Rows[3].RowState);
        foreach (var index in new[] { 1, 2 }) { Assert.True(table.Rows[index].HasErrors); Assert.Equal(DataRowState.Added, table.Rows[index].RowState); }
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    static PgParameterBindings Bind(PgTypeCatalog catalog, KingbaseParameter parameter) =>
        PgParameterBindings.Create("SELECT :p", new KingbaseParameterCollection { parameter }, catalog, KingbaseCompatibilityMode.PostgreSql, true);
    internal static BitArray Bits(string text) => new(text.Select(c => c == '1').ToArray());
    internal static string Text(BitArray bits) => new(bits.Cast<bool>().Select(value => value ? '1' : '0').ToArray());
    static async Task<PgTypeCatalog> Load(string? altered = null, short size = -1, char category = 'V', char kind = 'b')
    {
        var rows = Types().Select(type => type.Name == altered ? new PgTypeDescription(type.Oid, 90, type.Name, size, kind, category, 0, 0, 0, -1) : type);
        using var session = new PgQuerySession(new PgWire(new QueryStream(Catalog(rows))), new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        return await PgTypeCatalog.LoadAsync(session, new PgIdentityResult(version!, "14", KingbaseCompatibilityMode.PostgreSql, KingbaseCompatibilityMode.PostgreSql, KingbaseCompatibilityMode.PostgreSql, PgModeSource.ParameterStatus));
    }
    static IEnumerable<PgTypeDescription> Types()
    {
        var types = new[] { ("bool", (short)1, 'B'), ("int2", (short)2, 'N'), ("int4", (short)4, 'N'), ("int8", (short)8, 'N'),
            ("text", (short)-1, 'S'), ("bytea", (short)-1, 'U'), ("bit", (short)-1, 'V'), ("varbit", (short)-1, 'V') };
        return types.Select((type, index) => new PgTypeDescription((uint)(7000 + index), 90, type.Item1, type.Item2, 'b', type.Item3, 0, 0, 0, -1));
    }
}
