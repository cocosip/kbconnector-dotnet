using System.Numerics;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public sealed class BinaryCopyExtendedCodecTests
{
    public static IEnumerable<object[]> Literals()
    {
        yield return ["numeric", KingbaseNumeric.Parse("12345.6789"), "0003000100000004000109291A85"];
        yield return ["numeric", KingbaseNumeric.Parse("-0.00001"), "0001FFFE4000000503E8"];
        yield return ["numeric", KingbaseNumeric.Parse("10000.0000"), "00010001000000040001"];
        yield return ["numeric", KingbaseNumeric.Parse("0.000"), "0000000000000003"];
        yield return ["numeric", KingbaseNumeric.NaN, "00000000C0000000"];
        yield return ["numeric", KingbaseNumeric.PositiveInfinity, "00000000D0000000"];
        yield return ["numeric", KingbaseNumeric.NegativeInfinity, "00000000F0000000"];
        yield return ["date", new KingbaseDate(2000, 1, 1), "00000000"];
        yield return ["date", new KingbaseDate(1999, 12, 31), "FFFFFFFF"];
        yield return ["date", new KingbaseDate(1, 1, 1), "FFF4DBF9"];
        yield return ["date", new KingbaseDate(-1, 12, 31), "FFF4DBF8"];
        yield return ["date", KingbaseDate.PositiveInfinity, "7FFFFFFF"];
        yield return ["date", KingbaseDate.NegativeInfinity, "80000000"];
        yield return ["date", new KingbaseDate(-4714, 11, 24), "FFDA97A7"];
        yield return ["date", new KingbaseDate(5874897, 12, 31), "7FDA970C"];
        yield return ["time", TimeSpan.Zero, "0000000000000000"];
        yield return ["time", TimeSpan.FromDays(1), "000000141DD76000"];
        yield return ["timetz", new KingbaseTimeTz(TimeSpan.FromTicks(10), TimeSpan.FromSeconds(19801)), "0000000000000001FFFFB2A7"];
        yield return ["timestamp", new KingbaseTimestamp(new KingbaseDate(2000, 1, 1), TimeSpan.Zero), "0000000000000000"];
        yield return ["timestamp", new KingbaseTimestamp(new KingbaseDate(1999, 12, 31), TimeSpan.FromDays(1) - TimeSpan.FromTicks(10)), "FFFFFFFFFFFFFFFF"];
        yield return ["timestamp", KingbaseTimestamp.PositiveInfinity, "7FFFFFFFFFFFFFFF"];
        yield return ["timestamp", KingbaseTimestamp.NegativeInfinity, "8000000000000000"];
        yield return ["timestamp", KingbaseTimestamp.Parse("4714-11-24 00:00:00 BC"), "FD0F7CC1411FA000"];
        yield return ["timestamp", KingbaseTimestamp.Parse("294276-12-31 23:59:59.999999"), "7FFFFF5BB3B29FFF"];
        yield return ["timestamptz", new KingbaseTimestampTz(new KingbaseTimestamp(new KingbaseDate(2000, 1, 1), TimeSpan.Zero)), "0000000000000000"];
        yield return ["timestamptz", KingbaseTimestampTz.NegativeInfinity, "8000000000000000"];
        yield return ["interval", new KingbaseInterval(-2, 3, -1), "FFFFFFFFFFFFFFFF00000003FFFFFFFE"];
        yield return ["interval", new KingbaseInterval(int.MinValue, int.MaxValue, long.MinValue), "80000000000000007FFFFFFF80000000"];
    }

    [Theory]
    [MemberData(nameof(Literals))]
    public void ExactProviderValuesMatchIndependentNetworkLiterals(string name, object value, string hex)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Equal(bytes, PgBinaryCopyCodec.Encode(name, value, 1048576));
        var decoded = PgBinaryCopyCodec.Decode(name, bytes); Assert.Equal(value, decoded);
        if (value is KingbaseNumeric { IsFinite: true } numeric)
        { var exact = Assert.IsType<KingbaseNumeric>(decoded); Assert.Equal(numeric.Coefficient, exact.Coefficient); Assert.Equal(numeric.Scale, exact.Scale); }
        Assert.Throws<ArgumentException>(() => PgBinaryCopyCodec.Encode(name, value, bytes.Length - 1));
    }

    [Fact]
    public void ClrInputsRetainExactPrecisionScaleAndExplicitTimezone()
    {
        Assert.Equal(KingbaseNumeric.Parse("1.2300"), RoundTrip("numeric", 1.2300m));
        Assert.Equal(4, Assert.IsType<KingbaseNumeric>(RoundTrip("numeric", 1.2300m)).Scale);
        Assert.Equal(new KingbaseDate(2024, 2, 29), RoundTrip("date", new DateTime(2024, 2, 29)));
        Assert.Equal(new KingbaseDate(2024, 2, 29), RoundTrip("date", new DateOnly(2024, 2, 29)));
        Assert.Equal(TimeSpan.FromTicks(123450), RoundTrip("time", new TimeOnly(123450)));
        Assert.Equal(KingbaseTimestamp.Parse("2024-02-29 12:00:00.123456"), RoundTrip("timestamp", new DateTime(2024, 2, 29, 12, 0, 0).AddTicks(1234560)));
        Assert.Equal(KingbaseTimestampTz.Parse("1999-12-31 18:30:00+00"), RoundTrip("timestamptz", new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.FromMinutes(330))));
        Assert.Equal(KingbaseTimestampTz.Parse("2000-01-01 00:00:00+00"), RoundTrip("timestamptz", new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new KingbaseInterval(0, 0, -1), RoundTrip("interval", TimeSpan.FromTicks(-10)));
    }

    [Fact]
    public void UnsupportedKindsPrecisionAndScaleFailWithoutRounding()
    {
        foreach (var kind in new[] { DateTimeKind.Local, DateTimeKind.Utc })
        {
            Assert.Throws<InvalidCastException>(() => Encode("date", new DateTime(2024, 1, 1, 0, 0, 0, kind)));
            Assert.Throws<InvalidCastException>(() => Encode("timestamp", new DateTime(2024, 1, 1, 0, 0, 0, kind)));
        }
        Assert.Throws<InvalidCastException>(() => Encode("timestamptz", new DateTime(2024, 1, 1)));
        Assert.Throws<InvalidCastException>(() => Encode("date", new DateTime(2024, 1, 1, 1, 0, 0)));
        foreach (var name in new[] { "numeric", "date", "time", "timetz", "timestamp", "timestamptz", "interval" })
            Assert.Throws<InvalidCastException>(() => Encode(name, "1"));
        Assert.Throws<ArgumentException>(() => Encode("time", TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentException>(() => Encode("interval", TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentException>(() => Encode("timestamp", new DateTime(2024, 1, 1).AddTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode("time", TimeSpan.FromTicks(-10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode("time", TimeSpan.FromDays(1).Add(TimeSpan.FromTicks(10))));
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode("numeric", new KingbaseNumeric(1, 16384)));
    }

    [Theory]
    [InlineData("0001")]
    [InlineData("0001000000000000")]
    [InlineData("00000000000000000000")]
    [InlineData("0000000020000000")]
    [InlineData("0000000000004000")]
    [InlineData("00010000000000002710")]
    [InlineData("0001FFFF000000000001")]
    [InlineData("00010000C00000000001")]
    public void MalformedNumericNeverRoundsOrAcceptsInvalidHeaders(string hex) =>
        Assert.Throws<IOException>(() => PgBinaryCopyCodec.Decode("numeric", Convert.FromHexString(hex)));

    [Fact]
    public void NumericProviderBoundsAreCheckedBeforeLargeExponentArithmetic()
    {
        var maximum = new KingbaseNumeric(BigInteger.Pow(10, 16384) - 1, 16383);
        Assert.Equal(maximum, RoundTrip("numeric", maximum));
        Assert.Equal(new KingbaseNumeric(1, 16383), RoundTrip("numeric", new KingbaseNumeric(1, 16383)));
        Assert.Throws<OverflowException>(() => PgBinaryCopyCodec.Decode("numeric", Convert.FromHexString("00017FFF000000000001")));
        // Redundant zero groups are legal and must not inflate the bounded coefficient.
        Assert.Equal(KingbaseNumeric.Parse("1.00"), PgBinaryCopyCodec.Decode("numeric", Convert.FromHexString("0003000100000002000000010000")));
        Assert.Equal(new KingbaseNumeric(0, 16383), PgBinaryCopyCodec.Decode("numeric", Convert.FromHexString("00017FFF40003FFF0000")));
        var redundant = new byte[8 + 65535 * 2]; redundant[0] = 255; redundant[1] = 255; redundant[2] = 127; redundant[3] = 255;
        redundant[8 + 32767 * 2 + 1] = 1;
        Assert.Equal(new KingbaseNumeric(1, 0), PgBinaryCopyCodec.Decode("numeric", redundant));
    }

    [Fact]
    public void GregorianArithmeticMatchesTheIndependentClrCalendarAcrossItsRange()
    {
        var epoch = new DateTime(2000, 1, 1); var random = new Random(66);
        for (var i = 0; i < 256; i++)
        {
            var date = new DateTime(random.Next(1, 10000), random.Next(1, 13), 1).AddDays(random.Next(0, 27));
            var timestamp = date.AddTicks(random.NextInt64(0, TimeSpan.TicksPerDay / 10) * 10);
            var dateBytes = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(dateBytes, (int)(date - epoch).TotalDays);
            var timestampBytes = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(timestampBytes, (timestamp.Ticks - epoch.Ticks) / 10);
            Assert.Equal(dateBytes, Encode("date", date)); Assert.Equal(KingbaseDate.FromDateTime(date), PgBinaryCopyCodec.Decode("date", dateBytes));
            Assert.Equal(timestampBytes, Encode("timestamp", timestamp)); Assert.Equal(KingbaseTimestamp.FromDateTime(timestamp), PgBinaryCopyCodec.Decode("timestamp", timestampBytes));
        }
    }

    [Theory]
    [InlineData("date", "00")]
    [InlineData("date", "7FFFFFFE")]
    [InlineData("date", "80000001")]
    [InlineData("date", "FFDA97A6")]
    [InlineData("date", "7FDA970D")]
    [InlineData("timestamp", "00")]
    [InlineData("timestamp", "7FFFFFFFFFFFFFFE")]
    [InlineData("timestamp", "8000000000000001")]
    [InlineData("timestamp", "FD0F7CC1411F9FFF")]
    [InlineData("timestamp", "7FFFFF5BB3B2A000")]
    [InlineData("timestamptz", "7FFFFFFFFFFFFFFE")]
    [InlineData("time", "FFFFFFFFFFFFFFFF")]
    [InlineData("time", "000000141DD76001")]
    [InlineData("timetz", "00000000000000010000E100")]
    [InlineData("timetz", "0000000000000001FFFF1F00")]
    [InlineData("interval", "0000")]
    public void MalformedTemporalWidthsAndFiniteRangesAreRejected(string name, string hex) =>
        Assert.Throws<IOException>(() => PgBinaryCopyCodec.Decode(name, Convert.FromHexString(hex)));

    [Fact]
    public void GregorianEraLeapAndWireRangeBoundariesRoundTrip()
    {
        foreach (var date in new[] { new KingbaseDate(-1, 2, 29), new KingbaseDate(-401, 2, 29), new KingbaseDate(-101, 3, 1),
            new KingbaseDate(1900, 3, 1), new KingbaseDate(2000, 2, 29), new KingbaseDate(5874897, 12, 31), new KingbaseDate(-4714, 11, 24) })
            Assert.Equal(date, RoundTrip("date", date));
        foreach (var timestamp in new[] { KingbaseTimestamp.Parse("0001-01-01 00:00:00 BC"),
            KingbaseTimestamp.Parse("294276-12-31 23:59:59.999999"), KingbaseTimestamp.Parse("4714-11-24 00:00:00 BC") })
            Assert.Equal(timestamp, RoundTrip("timestamp", timestamp));
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode("date", new KingbaseDate(5874898, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode("date", new KingbaseDate(-4714, 11, 23)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode("timestamp", KingbaseTimestamp.Parse("294277-01-01 00:00:00")));
    }

    static byte[] Encode(string name, object value) => PgBinaryCopyCodec.Encode(name, value, 1048576)!;
    static object RoundTrip(string name, object value) => PgBinaryCopyCodec.Decode(name, Encode(name, value));
}
