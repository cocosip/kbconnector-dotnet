using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgParameterPlanTests
{
    static PgParameterPlan Plan(string sql, string?[]? names = null, bool? conforming = true,
        KingbaseCompatibilityMode mode = KingbaseCompatibilityMode.PostgreSql) =>
        PgParameterPlan.Create(sql, names ?? [], mode, conforming);

    [Fact]
    public void NamedParametersReuseSlotsInOccurrenceOrderWithoutMutatingCollection()
    {
        string?[] names = [":second", "@First", "third"];
        var plan = Plan("SELECT :first, @second, :FIRST, @third, :second", names);
        Assert.Equal("SELECT $1, $2, $1, $3, $2", plan.Sql);
        Assert.Equal(new[] { 1, 0, 2 }, plan.ParameterIndexes);
        names[0] = "changed";
        Assert.Equal(new[] { 1, 0, 2 }, plan.ParameterIndexes);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)plan.ParameterIndexes)[0] = 99);
    }

    [Theory]
    [InlineData("SELECT $2,$1,$2", "SELECT $2,$1,$2")]
    [InlineData("SELECT $1::int, $2; -- $3 :ignored", "SELECT $1::int, $2; -- $3 :ignored")]
    public void NativePositionsKeepOriginalOrder(string sql, string expected)
    {
        var plan = Plan(sql, [null, "optional"]);
        Assert.Equal(expected, plan.Sql);
        Assert.Equal(new[] { 0, 1 }, plan.ParameterIndexes);
    }

    public static TheoryData<string, string> Quoting => new()
    {
        { "SELECT ':hidden '' @hidden $2', @p", "SELECT ':hidden '' @hidden $2', $1" },
        { "SELECT \"@hidden\"\"$2\", :p", "SELECT \"@hidden\"\"$2\", $1" },
        { "SELECT $$:hidden '$2 @hidden;$$, :p", "SELECT $$:hidden '$2 @hidden;$$, $1" },
        { "SELECT $tag$:hidden $TAG$ @hidden;$tag$, :p", "SELECT $tag$:hidden $TAG$ @hidden;$tag$, $1" },
        { "SELECT $标签$:hidden; $1$标签$, :p", "SELECT $标签$:hidden; $1$标签$, $1" },
        { "SELECT E'\\\'@hidden; $2', :p", "SELECT E'\\\'@hidden; $2', $1" },
        { "SELECT e'\\\\', :p", "SELECT e'\\\\', $1" },
        { "SELECT E'first'\n'\\\'@hidden; $2', :p", "SELECT E'first'\n'\\\'@hidden; $2', $1" },
        { "SELECT E'first' -- gap\r\n'\\\'@hidden', :p", "SELECT E'first' -- gap\r\n'\\\'@hidden', $1" },
        { "SELECT E'first' /* gap\n */ '\\' || :p -- '\n", "SELECT E'first' /* gap\n */ '\\' || $1 -- '\n" },
        { "SELECT E'first'\n/* gap */ '\\' || :p -- '\n", "SELECT E'first'\n/* gap */ '\\' || $1 -- '\n" },
        { "SELECT 'first'\n'@hidden', :p", "SELECT 'first'\n'@hidden', $1" },
        { "SELECT U&'d\\0061t @hidden', :p", "SELECT U&'d\\0061t @hidden', $1" },
        { "SELECT U&\"d\\0061t @hidden\" UESCAPE '!', :p", "SELECT U&\"d\\0061t @hidden\" UESCAPE '!', $1" },
        { "SELECT B'101', X'ab', N'@hidden', :p", "SELECT B'101', X'ab', N'@hidden', $1" },
        { "SELECT /* @x /* :y $2 */ ; */ :p -- @z\n", "SELECT /* @x /* :y $2 */ ; */ $1 -- @z\n" },
        { "SELECT abc$1, abc$tag$foo, :p", "SELECT abc$1, abc$tag$foo, $1" },
        { "SELECT :p::int, x:=:p, a @> b, a <@ b, a @@ b, a @? b", "SELECT $1::int, x:=$1, a @> b, a <@ b, a @@ b, a @? b" },
        { "SELECT col=:p, :p+:p, @p", "SELECT col=$1, $1+$1, $1" },
        { "SELECT :p::/* @hidden */int, a<@/* :hidden */b, a@@/* $2 */b", "SELECT $1::/* @hidden */int, a<@/* :hidden */b, a@@/* $2 */b" },
        { "SELECT :p, a<@@b, a@@@b, a@@-- :hidden\n b", "SELECT $1, a<@@b, a@@@b, a@@-- :hidden\n b" },
        { "SELECT :p; /* trailing ; :x */ -- $2\r\n", "SELECT $1; /* trailing ; :x */ -- $2\r\n" }
    };

    [Theory]
    [MemberData(nameof(Quoting))]
    public void LexerPreservesQuotedBodiesCommentsIdentifiersAndOperators(string sql, string expected)
    {
        var plan = Plan(sql, ["p"]);
        Assert.Equal(expected, plan.Sql);
        Assert.Equal(new[] { 0 }, plan.ParameterIndexes);
    }

    [Fact]
    public void ConfirmedSessionSettingChangesBackslashLexingWithoutCachedPlanReuse()
    {
        var sql = "SELECT '\\' || :p -- '\n";
        Assert.Equal("SELECT '\\' || $1 -- '\n", Plan(sql, ["p"], true).Sql);
        Assert.Throws<ArgumentException>(() => Plan(sql, ["p"], false));
        Assert.Equal("SELECT '\\' || $1 -- '\n", Plan(sql, ["p"], true).Sql);
        var escaped = "SELECT '\\'@hidden', :p";
        Assert.Equal("SELECT '\\'@hidden', $1", Plan(escaped, ["p"], false).Sql);
        Assert.Throws<ArgumentException>(() => Plan(escaped, ["p"], true));
    }

    [Theory]
    [InlineData("SELECT $0", 1)]
    [InlineData("SELECT $01", 1)]
    [InlineData("SELECT $2", 2)]
    [InlineData("SELECT $1", 2)]
    [InlineData("SELECT $3", 2)]
    [InlineData("SELECT $9999999999999999999999", 1)]
    [InlineData("SELECT $1foo", 1)]
    [InlineData("SELECT $1$", 1)]
    [InlineData("SELECT 1", 1)]
    [InlineData("SELECT :missing", 0)]
    [InlineData("SELECT @p, $1", 1)]
    [InlineData("SELECT $1, :p", 1)]
    public void MissingExtraMalformedAndMixedBindingsRejectLocally(string sql, int count) =>
        Assert.Throws<ArgumentException>(() => Plan(sql, Enumerable.Repeat<string?>("p", count).ToArray()));

    [Theory]
    [InlineData("SELECT :p", "p", "P")]
    [InlineData("SELECT :p", "@p", ":P")]
    [InlineData("SELECT :p", "p", "unused")]
    [InlineData("SELECT :p", "p", "")]
    [InlineData("SELECT :p", "p", null)]
    [InlineData("SELECT :p", "p", "bad name")]
    public void AmbiguousInvalidAndUnusedNamedParametersReject(string sql, string? first, string? second) =>
        Assert.Throws<ArgumentException>(() => Plan(sql, [first, second]));

    [Theory]
    [InlineData("SELECT :p界")]
    [InlineData("SELECT :p$tail$body$tail$")]
    public void UnsupportedNamedTokenSuffixesCannotPartiallyBind(string sql) =>
        Assert.Throws<ArgumentException>(() => Plan(sql, ["p"]));

    [Theory]
    [InlineData("SELECT 'secret")]
    [InlineData("SELECT \"secret")]
    [InlineData("SELECT $$secret")]
    [InlineData("SELECT $tag$secret$TAG$")]
    [InlineData("SELECT /* secret /* nested */")]
    [InlineData("SELECT E'backslash\\")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT :p; SET standard_conforming_strings=off")]
    [InlineData("SELECT 1;;")]
    public void UnterminatedBodiesAndMultipleStatementsRejectWithSafeErrors(string sql)
    {
        var error = Assert.Throws<ArgumentException>(() => Plan(sql));
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.Auto)]
    [InlineData((KingbaseCompatibilityMode)99)]
    public void UnknownModesRequireAConfirmedLexicalContract(KingbaseCompatibilityMode mode) =>
        Assert.Throws<NotSupportedException>(() => Plan("SELECT :p", ["p"], mode: mode));

    [Fact]
    public void UnknownLexicalStateAndInvalidInputsReject()
    {
        Assert.Throws<InvalidOperationException>(() => Plan("SELECT :p", ["p"], null));
        Assert.Throws<ArgumentNullException>(() => PgParameterPlan.Create(null!, [], KingbaseCompatibilityMode.PostgreSql, true));
        Assert.Throws<ArgumentNullException>(() => PgParameterPlan.Create("", null!, KingbaseCompatibilityMode.PostgreSql, true));
        Assert.Throws<ArgumentException>(() => Plan("SELECT '\0'"));
        Assert.Throws<ArgumentException>(() => Plan("SELECT '" + new string((char)0xD800, 1) + "'"));
        Assert.Throws<ArgumentException>(() => Plan("SELECT :" + new string('a', 129), [new string('a', 129)]));
    }

    [Fact]
    public void EmptyAndCommentOnlySqlKeepNoParameterSlots()
    {
        foreach (var sql in new[] { "", " -- text", "/* nested /* ok */ */", "; -- empty" })
        {
            var plan = Plan(sql);
            Assert.Equal(sql, plan.Sql);
            Assert.Empty(plan.ParameterIndexes);
        }
    }

    [Fact]
    public async Task PlanReordersActualCatalogBindingsAndLocalFailureDoesNotWrite()
    {
        var definitions = new (string Name, short Size)[] { ("bool", 1), ("int2", 2), ("int4", 4), ("int8", 8), ("text", -1), ("bytea", -1) }
            .Select((type, i) => new PgTypeDescription((uint)(9000 + i), 90, type.Name, type.Size, 'b', 'U', 0, 0, 0, -1));
        var stream = new QueryStream(Join(Catalog(definitions), Frame('1'), Frame('2'), Frame('n'), Complete(),
            Status("standard_conforming_strings", "off"), Ready()));
        using var session = new PgQuerySession(new PgWire(stream), new PgStartupResult(new Dictionary<string, string>
            { ["client_encoding"] = "UTF8", ["standard_conforming_strings"] = "on" }, new PgBackendKey(1, 2)));
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        var catalog = await PgTypeCatalog.LoadAsync(session, new PgIdentityResult(version!, "14", KingbaseCompatibilityMode.PostgreSql,
            KingbaseCompatibilityMode.PostgreSql, null, null));
        string?[] names = ["b", "a"];
        PgQueryParameter[] values = [new(9004, "private-value"), new(9002, 42)];
        var plan = Plan("SELECT :a, :b, :a", names, session.Parameters["standard_conforming_strings"] == "on");
        await session.ExecuteExtendedAsync(plan.Sql, catalog, plan.ParameterIndexes.Select(i => values[i]).ToArray());
        Assert.Equal("SELECT $1, $2, $1", plan.Sql);
        var parse = Encoding.UTF8.GetString(stream.Writes[1]);
        Assert.Contains("SELECT $1, $2, $1", parse, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", parse, StringComparison.Ordinal);
        var bind = stream.Writes[2];
        Assert.Equal(new byte[] { 0, 0, 0, 2 }, bind[7..11]);
        Assert.Equal(Join(Int32(2), Encoding.ASCII.GetBytes("42"), Int32(13), Encoding.ASCII.GetBytes("private-value")), bind[11..^2]);
        var writes = stream.Writes.Count;
        Assert.Throws<ArgumentException>(() => Plan("SELECT '\\' || :a -- '\n", ["a"],
            session.Parameters["standard_conforming_strings"] == "on"));
        Assert.Equal(writes, stream.Writes.Count);
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public void ResourceBoundsApplyBeforeAndAfterRewriting()
    {
        const int max = 1024 * 1024;
        var boundary = "SELECT '" + new string('a', max - 10) + "'";
        Assert.Equal(max, Encoding.UTF8.GetByteCount(Plan(boundary).Sql) + 1);
        Assert.Throws<ArgumentException>(() => Plan(boundary + " "));
        Assert.Throws<ArgumentException>(() => Plan("SELECT '" + new string('界', max / 3) + "'"));
        Assert.Throws<ArgumentException>(() => Plan("SELECT 1", new string?[1025]));
        var names = Enumerable.Range(1, 1024).Select(i => (string?)("p" + i)).ToArray();
        var sql = "SELECT " + string.Join(",", names.Select(name => ":" + name));
        var plan = Plan(sql, names);
        Assert.EndsWith("$1024", plan.Sql);
        Assert.Equal(1024, plan.ParameterIndexes.Count);
        var compactNames = Enumerable.Range(0, 1024).Select(i => (string?)("p" + i)).ToArray();
        var prefix = "SELECT :p1023," + string.Join(",", Enumerable.Range(1, 1022).Select(i => ":p" + i)) + ",:p0,";
        var tail = string.Concat(Enumerable.Repeat(":p0,", (max - prefix.Length - 1) / 4));
        // Reusing the 1024th slot expands :p0 (3 bytes) to $1024 (5 bytes).
        Assert.EndsWith("$1024", Plan(prefix.TrimEnd(','), compactNames).Sql);
        var expanding = prefix + tail.TrimEnd(',');
        Assert.InRange(Encoding.UTF8.GetByteCount(expanding) + 1, 1, max);
        Assert.Throws<ArgumentException>(() => Plan(expanding, compactNames));
    }

    [Fact]
    public void ParameterSnapshotMustRespectBoundEvenWhenReportedCountDiffers()
    {
        var actual = Enumerable.Range(0, 1025).Select(i => (string?)("p" + i)).ToArray();
        var sql = "SELECT " + string.Join(",", actual.Select(name => ":" + name));
        Assert.Throws<ArgumentException>(() => PgParameterPlan.Create(sql, new MiscountedNames(actual),
            KingbaseCompatibilityMode.PostgreSql, true));
    }

    sealed class MiscountedNames(string?[] actual) : IReadOnlyList<string?>
    {
        public int Count => 1;
        public string? this[int index] => actual[index];
        public IEnumerator<string?> GetEnumerator() => ((IEnumerable<string?>)actual).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
