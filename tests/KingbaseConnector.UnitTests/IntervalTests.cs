using System.Globalization;

namespace KingbaseConnector.UnitTests;

public sealed class IntervalTests
{
    [Theory]
    [InlineData(0, 0, 0L, "PT0S")]
    [InlineData(14, 3, 14706123456L, "P14M3DT4H5M6.123456S")]
    [InlineData(-14, -3, -14706123456L, "P-14M-3DT-4H-5M-6.123456S")]
    [InlineData(14, -3, -1L, "P14M-3DT-0.000001S")]
    [InlineData(0, 0, 86400000000L, "PT24H")]
    [InlineData(0, 1, 0L, "P1D")]
    [InlineData(int.MaxValue, int.MaxValue, long.MaxValue, "P2147483647M2147483647DT2562047788H54.775807S")]
    [InlineData(int.MinValue, int.MinValue, long.MinValue, "P-2147483648M-2147483648DT-2562047788H-54.775808S")]
    public void ExactComponentsAndCanonicalTextNeverNormalizeCalendarDays(int months, int days, long microseconds, string text)
    {
        var value = new KingbaseInterval(months, days, microseconds);
        Assert.Equal(months, value.Months); Assert.Equal(days, value.Days); Assert.Equal(microseconds, value.Microseconds);
        Assert.Equal(text, value.ToString()); Assert.Equal(value, KingbaseInterval.Parse(text));
        Assert.True(KingbaseInterval.TryParse(text, out var parsed)); Assert.True(value == parsed); Assert.False(value != parsed);
        Assert.Equal(value, (object)parsed); Assert.Equal(value.GetHashCode(), parsed.GetHashCode()); Assert.False(value.Equals("interval"));
    }

    [Theory]
    [InlineData("P1Y2M3DT4H5M6.123456S", 14, 3, 14706123456L)]
    [InlineData("P-1Y-2M-3DT-4H-5M-6.000001S", -14, -3, -14706000001L)]
    [InlineData("P1Y-2M-3DT4H-5M0.000001S", 10, -3, 14100000001L)]
    [InlineData("P178956970Y7M", int.MaxValue, 0, 0L)]
    [InlineData("P-178956970Y-8M", int.MinValue, 0, 0L)]
    [InlineData("PT-0.000001S", 0, 0, -1L)]
    [InlineData("PT60M", 0, 0, 3600000000L)]
    [InlineData("P+1DT+1.2S", 0, 1, 1200000L)]
    public void IsoUsesExactSignedFieldsWithIndependentLiteralExpectations(string text, int months, int days, long microseconds) =>
        Assert.Equal(new KingbaseInterval(months, days, microseconds), KingbaseInterval.Parse(text));

    [Fact]
    public void EqualityIsStructuralRatherThanAssumingEveryDayIsTwentyFourClockHours()
    {
        Assert.Equal(new KingbaseInterval(0, 0, 0), default);
        Assert.NotEqual(new KingbaseInterval(0, 1, 0), new KingbaseInterval(0, 0, 86400000000));
        Assert.NotEqual(new KingbaseInterval(1, 0, 0), new KingbaseInterval(0, 30, 0));
        Assert.NotEqual(new KingbaseInterval(0, 1, -86400000000), default);
    }

    [Theory]
    [InlineData(0L)] [InlineData(10L)] [InlineData(-10L)]
    [InlineData(864000000010L)] [InlineData(-864000000010L)]
    [InlineData(9223372036854775800L)] [InlineData(-9223372036854775800L)]
    public void TimeSpanInputPreservesExactMicrosecondsAndDoesNotCreateCalendarDays(long ticks)
    {
        var value = KingbaseInterval.FromTimeSpan(TimeSpan.FromTicks(ticks));
        Assert.Equal(new KingbaseInterval(0, 0, ticks / 10), value); Assert.Equal(ticks, value.ToTimeSpan().Ticks);
    }

    [Fact]
    public void TimeSpanConversionChecksFinalExactValueIncludingDayAndClockCancellation()
    {
        Assert.Equal(TimeSpan.FromTicks(864000000010), new KingbaseInterval(0, 1, 1).ToTimeSpan());
        Assert.Equal(TimeSpan.FromTicks(10), new KingbaseInterval(0, 10675200, -922337279999999999).ToTimeSpan());
        Assert.Equal(TimeSpan.FromTicks(-10), new KingbaseInterval(0, -10675200, 922337279999999999).ToTimeSpan());
        Assert.Throws<OverflowException>(() => new KingbaseInterval(0, 0, long.MaxValue).ToTimeSpan());
        Assert.Throws<OverflowException>(() => new KingbaseInterval(0, int.MinValue, long.MaxValue).ToTimeSpan());
        Assert.Throws<InvalidCastException>(() => new KingbaseInterval(1, 0, 0).ToTimeSpan());
        foreach (var ticks in new[] { 1L, -1L, long.MaxValue, long.MinValue })
            Assert.Throws<ArgumentException>(() => KingbaseInterval.FromTimeSpan(TimeSpan.FromTicks(ticks)));
    }

    [Theory]
    [InlineData("")] [InlineData("P")] [InlineData("PT")] [InlineData("P1DT")]
    [InlineData(" P1D")] [InlineData("P1D ")] [InlineData("P1d")] [InlineData("P1W")]
    [InlineData("P1M2Y")] [InlineData("P1D2D")] [InlineData("PT1M1H")] [InlineData("PT1HT1S")]
    [InlineData("P1.5M")] [InlineData("P1.5D")] [InlineData("PT1.5H")] [InlineData("PT1.5M")]
    [InlineData("PT.1S")] [InlineData("PT1.S")] [InlineData("PT1.0000001S")] [InlineData("PT1e2S")]
    [InlineData("PT١S")] [InlineData("PT+S")] [InlineData("PT--1S")] [InlineData("PT1\0S")]
    [InlineData("P2147483648M")] [InlineData("P-2147483649M")] [InlineData("P2147483648D")]
    [InlineData("P-2147483649D")] [InlineData("PT9223372036854.775808S")]
    [InlineData("PT-9223372036854.775809S")] [InlineData("infinity")] [InlineData("-infinity")]
    public void InvalidAndOutOfRangeIsoTextRejectsWithoutEchoOrInnerException(string text)
    {
        var error = Assert.Throws<FormatException>(() => KingbaseInterval.Parse(text));
        Assert.Null(error.InnerException); Assert.Equal("Invalid or out-of-range PG INTERVAL text.", error.Message);
        Assert.False(KingbaseInterval.TryParse(text, out var value)); Assert.Equal(default, value);
    }

    [Fact]
    public void ParsingIsBoundedCultureInvariantAndStableAcrossRandomStorageValues()
    {
        Assert.Throws<ArgumentNullException>(() => KingbaseInterval.Parse(null!)); Assert.False(KingbaseInterval.TryParse(null, out _));
        Assert.False(KingbaseInterval.TryParse("P" + new string('1', 10000) + "M", out _));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("PT-0.123456S", new KingbaseInterval(0, 0, -123456).ToString());
            Assert.False(KingbaseInterval.TryParse("PT0,1S", out _));
            var random = new Random(1389); var bytes = new byte[16];
            for (var index = 0; index < 500; index++)
            {
                random.NextBytes(bytes);
                var value = new KingbaseInterval(BitConverter.ToInt32(bytes, 0), BitConverter.ToInt32(bytes, 4), BitConverter.ToInt64(bytes, 8));
                Assert.Equal(value, KingbaseInterval.Parse(value.ToString()));
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
