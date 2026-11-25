using System.Globalization;

namespace KingbaseConnector.UnitTests;

public sealed class TimeTzTests
{
    [Theory]
    [InlineData("00:00:00+00", 0L, 0)]
    [InlineData("00:00:00.000001-00:00:01", 10L, -1)]
    [InlineData("12:34:56.123456+05:30", 452961234560L, 19800)]
    [InlineData("23:59:59.999999-15:59:59", 863999999990L, -57599)]
    [InlineData("24:00:00+15:59:59", 864000000000L, 57599)]
    public void ExactWallClockAndSignedSecondOffsetArePreserved(string text, long ticks, int offsetSeconds)
    {
        var value = KingbaseTimeTz.Parse(text);
        Assert.Equal(TimeSpan.FromTicks(ticks), value.Time); Assert.Equal(TimeSpan.FromSeconds(offsetSeconds), value.Offset);
        Assert.Equal(text, value.ToString()); Assert.Equal(new KingbaseTimeTz(value.Time, value.Offset), value);
        Assert.True(KingbaseTimeTz.TryParse(text, out var parsed)); Assert.Equal(value, parsed);
        Assert.Equal(value.GetHashCode(), parsed.GetHashCode()); Assert.True(value == parsed); Assert.False(value != parsed);
        Assert.Equal(value, (object)parsed);
    }

    [Theory]
    [InlineData("12:34:56.100000+00:00:00", "12:34:56.1+00")]
    [InlineData("24:00:00.000000-00:00", "24:00:00+00")]
    [InlineData("12:34:56+05:00:00", "12:34:56+05")]
    [InlineData("12:34:56-00:30:00", "12:34:56-00:30")]
    public void CanonicalFormattingIsInvariantAndTrimsOnlyZeros(string input, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(expected, KingbaseTimeTz.Parse(input).ToString());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void DefaultAndEqualityPreserveRepresentationRatherThanNormalizingToUtc()
    {
        Assert.Equal("00:00:00+00", default(KingbaseTimeTz).ToString());
        Assert.NotEqual(KingbaseTimeTz.Parse("12:00:00+01"), KingbaseTimeTz.Parse("11:00:00+00"));
        Assert.NotEqual(KingbaseTimeTz.Parse("24:00:00+00"), default(KingbaseTimeTz));
        Assert.False(default(KingbaseTimeTz).Equals(null)); Assert.False(default(KingbaseTimeTz).Equals(TimeSpan.Zero));
    }

    [Theory]
    [InlineData(-1L, 0L, true)]
    [InlineData(864000000010L, 0L, true)]
    [InlineData(long.MinValue, 0L, true)]
    [InlineData(long.MaxValue, 0L, true)]
    [InlineData(0L, -576000000000L, true)]
    [InlineData(0L, 576000000000L, true)]
    [InlineData(0L, long.MinValue, true)]
    [InlineData(0L, long.MaxValue, true)]
    [InlineData(1L, 0L, false)]
    [InlineData(0L, 10L, false)]
    public void ConstructorRejectsRangeAndPrecisionWithoutRounding(long time, long offset, bool range)
    {
        Action create = () => new KingbaseTimeTz(TimeSpan.FromTicks(time), TimeSpan.FromTicks(offset));
        if (range) Assert.Throws<ArgumentOutOfRangeException>(create);
        else Assert.Throws<ArgumentException>(create);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12:00:00")]
    [InlineData("1:00:00+00")]
    [InlineData("12:0:00+00")]
    [InlineData("12:00:0+00")]
    [InlineData("12:00:00.+00")]
    [InlineData("12:00:00.0000000+00")]
    [InlineData("12:00:00,1+00")]
    [InlineData("24:00:00.000001+00")]
    [InlineData("24:00:01+00")]
    [InlineData("25:00:00+00")]
    [InlineData("23:60:00+00")]
    [InlineData("23:59:60+00")]
    [InlineData("-01:00:00+00")]
    [InlineData("12:00:00+16")]
    [InlineData("12:00:00-16")]
    [InlineData("12:00:00+01:60")]
    [InlineData("12:00:00-00:00:60")]
    [InlineData("12:00:00+0000")]
    [InlineData("12:00:00+1")]
    [InlineData("12:00:00+01:2")]
    [InlineData("12:00:00+00:00:")]
    [InlineData("12:00:00+00:00:00.1")]
    [InlineData("12:00:00Z")]
    [InlineData("12:00:00 UTC")]
    [InlineData("12:00:00-05 EST")]
    [InlineData("１２:00:00+00")]
    [InlineData("12:00:00+０0")]
    [InlineData(" 12:00:00+00")]
    [InlineData("12:00:00+00 ")]
    [InlineData("12:00:00+00\0")]
    [InlineData("private-time")]
    public void MalformedTextFailsWithoutPayloadOrInnerException(string? text)
    {
        Assert.False(KingbaseTimeTz.TryParse(text, out var value)); Assert.Equal(default, value);
        if (text is null) Assert.Throws<ArgumentNullException>(() => KingbaseTimeTz.Parse(null!));
        else
        {
            var error = Assert.Throws<FormatException>(() => KingbaseTimeTz.Parse(text));
            Assert.Null(error.InnerException);
            if (text.Length != 0) Assert.DoesNotContain(text, error.Message);
        }
    }
}
