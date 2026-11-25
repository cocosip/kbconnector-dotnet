using System.Globalization;

namespace KingbaseConnector.UnitTests;

public sealed class TimestampTests
{
    [Theory]
    [InlineData("0001-01-01 00:00:00", 1, 1, 1, 0L)]
    [InlineData("2000-02-29 12:34:56.123456", 2000, 2, 29, 452961234560L)]
    [InlineData("9999-12-31 23:59:59.999999", 9999, 12, 31, 863999999990L)]
    [InlineData("10000-01-01 00:00:00.000001", 10000, 1, 1, 10L)]
    [InlineData("294276-12-31 23:59:59.999999", 294276, 12, 31, 863999999990L)]
    [InlineData("0001-02-29 12:34:56.123456 BC", -1, 2, 29, 452961234560L)]
    [InlineData("4714-11-24 00:00:00 BC", -4714, 11, 24, 0L)]
    public void CalendarAndClockStayExactIncludingBcAndLargeYears(string text, int year, int month, int day, long ticks)
    {
        var value = KingbaseTimestamp.Parse(text);
        Assert.True(value.IsFinite); Assert.False(value.IsInfinity);
        Assert.Equal(new KingbaseDate(year, month, day), value.Date); Assert.Equal(TimeSpan.FromTicks(ticks), value.Time);
        Assert.Equal(text, value.ToString()); Assert.True(KingbaseTimestamp.TryParse(text, out var parsed));
        Assert.Equal(value, parsed); Assert.Equal(value.GetHashCode(), parsed.GetHashCode()); Assert.True(value == parsed); Assert.False(value != parsed);
        Assert.Equal(value, (object)parsed);
        if (year is >= 1 and <= 9999)
        {
            Assert.Equal(new DateTime(year, month, day).AddTicks(ticks), value.ToDateTime());
            Assert.Equal(DateTimeKind.Unspecified, value.ToDateTime().Kind);
            Assert.Equal(value, KingbaseTimestamp.FromDateTime(value.ToDateTime()));
        }
        else Assert.Throws<OverflowException>(() => value.ToDateTime());
    }

    [Theory]
    [InlineData("2024-01-01 00:00:00+00", "2024-01-01 00:00:00+00")]
    [InlineData("2024-01-01 00:00:00+00:00:01", "2023-12-31 23:59:59+00")]
    [InlineData("2023-12-31 23:59:59-00:00:01", "2024-01-01 00:00:00+00")]
    [InlineData("2024-03-01 00:00:00+01", "2024-02-29 23:00:00+00")]
    [InlineData("1900-03-01 00:00:00+01", "1900-02-28 23:00:00+00")]
    [InlineData("2000-02-29 23:59:59.999999-00:00:01", "2000-03-01 00:00:00.999999+00")]
    [InlineData("2024-05-01 00:00:00+01", "2024-04-30 23:00:00+00")]
    [InlineData("2024-12-31 12:34:56.123456+15:59:59", "2024-12-30 20:34:57.123456+00")]
    [InlineData("2024-12-31 12:34:56.123456-15:59:59", "2025-01-01 04:34:55.123456+00")]
    [InlineData("0001-01-01 00:00:00+00:00:01", "0001-12-31 23:59:59+00 BC")]
    [InlineData("0001-12-31 23:59:59-00:00:01 BC", "0001-01-01 00:00:00+00")]
    [InlineData("0001-03-01 00:00:00+01 BC", "0001-02-29 23:00:00+00 BC")]
    [InlineData("0101-03-01 00:00:00+01 BC", "0101-02-28 23:00:00+00 BC")]
    [InlineData("0401-03-01 00:00:00+01 BC", "0401-02-29 23:00:00+00 BC")]
    [InlineData("9999-12-31 23:59:59-01", "10000-01-01 00:59:59+00")]
    [InlineData("10000-01-01 00:00:00+01", "9999-12-31 23:00:00+00")]
    [InlineData("2024-03-10 01:59:59.999999-05", "2024-03-10 06:59:59.999999+00")]
    [InlineData("2024-03-10 03:00:00-04", "2024-03-10 07:00:00+00")]
    [InlineData("2024-11-03 01:30:00-04", "2024-11-03 05:30:00+00")]
    [InlineData("2024-11-03 01:30:00-05", "2024-11-03 06:30:00+00")]
    public void ExplicitOffsetsNormalizeAcrossCivilDaysErasAndDstSeasons(string input, string expected)
    {
        var value = KingbaseTimestampTz.Parse(input);
        Assert.Equal(expected, value.ToString()); Assert.Equal(value, KingbaseTimestampTz.Parse(expected));
        Assert.True(KingbaseTimestampTz.TryParse(input, out var parsed)); Assert.Equal(value, parsed);
        Assert.Equal(value.GetHashCode(), parsed.GetHashCode()); Assert.True(value == parsed); Assert.False(value != parsed);
        Assert.Equal(value, (object)parsed);
        if (value.UtcValue.Date.Year is >= 1 and <= 9999)
        {
            Assert.Equal(DateTimeKind.Utc, value.ToDateTime().Kind); Assert.Equal(TimeSpan.Zero, value.ToDateTimeOffset().Offset);
            Assert.Equal(value, KingbaseTimestampTz.FromDateTimeOffset(value.ToDateTimeOffset()));
        }
        else Assert.Throws<OverflowException>(() => value.ToDateTime());
    }

    [Theory]
    [InlineData("infinity")]
    [InlineData("-infinity")]
    public void InfinitiesStayExplicitWithoutClrSentinelsOrComponents(string text)
    {
        var calendar = KingbaseTimestamp.Parse(text); var instant = KingbaseTimestampTz.Parse(text);
        Assert.True(calendar.IsInfinity); Assert.True(instant.IsInfinity);
        Assert.Equal(text, calendar.ToString()); Assert.Equal(text, instant.ToString());
        Assert.Throws<InvalidOperationException>(() => calendar.Date); Assert.Throws<InvalidOperationException>(() => calendar.Time);
        Assert.Throws<OverflowException>(() => calendar.ToDateTime()); Assert.Throws<OverflowException>(() => instant.ToDateTimeOffset());
        Assert.Equal(text == "infinity" ? KingbaseTimestampTz.PositiveInfinity : KingbaseTimestampTz.NegativeInfinity, instant);
        Assert.NotEqual(calendar, default); Assert.NotEqual(instant, default);
    }

    [Fact]
    public void ClrKindOffsetPrecisionAndDefaultsFollowExplicitContracts()
    {
        Assert.Equal("0001-01-01 00:00:00", default(KingbaseTimestamp).ToString());
        Assert.Equal("0001-01-01 00:00:00+00", default(KingbaseTimestampTz).ToString());
        foreach (var kind in new[] { DateTimeKind.Local, DateTimeKind.Utc })
            Assert.Throws<InvalidCastException>(() => KingbaseTimestamp.FromDateTime(DateTime.SpecifyKind(DateTime.MinValue, kind)));
        foreach (var kind in new[] { DateTimeKind.Local, DateTimeKind.Unspecified })
            Assert.Throws<InvalidCastException>(() => KingbaseTimestampTz.FromDateTime(DateTime.SpecifyKind(DateTime.MinValue, kind)));
        Assert.Throws<ArgumentException>(() => KingbaseTimestamp.FromDateTime(DateTime.MinValue.AddTicks(1)));
        Assert.Throws<ArgumentException>(() => KingbaseTimestampTz.FromDateTimeOffset(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KingbaseTimestamp(default, TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KingbaseTimestamp(default, TimeSpan.FromTicks(-10)));
        Assert.Throws<ArgumentException>(() => new KingbaseTimestamp(KingbaseDate.PositiveInfinity, TimeSpan.Zero));
        var offset = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(14));
        Assert.Equal("2023-12-31 10:00:00+00", KingbaseTimestampTz.FromDateTimeOffset(offset).ToString());
        Assert.Equal(new DateTime(2023, 12, 31, 10, 0, 0, DateTimeKind.Utc), KingbaseTimestampTz.FromDateTimeOffset(offset).ToDateTime());
        Assert.False(default(KingbaseTimestamp).Equals(null)); Assert.False(default(KingbaseTimestampTz).Equals(DateTime.MinValue));
    }

    [Fact]
    public void CanonicalTextDoesNotFollowMachineCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal("2000-02-29 12:34:56.12", KingbaseTimestamp.Parse("2000-02-29 12:34:56.120000").ToString());
            Assert.Equal("2000-02-29 12:34:56.12+00", KingbaseTimestampTz.Parse("2000-02-29 12:34:56.120000+00").ToString());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("private-timestamp")]
    [InlineData("2024-02-30 00:00:00")]
    [InlineData("0000-01-01 00:00:00")]
    [InlineData("2024-01-01T00:00:00")]
    [InlineData("01-02-2024 00:00:00")]
    [InlineData("2024-01-01 24:00:00")]
    [InlineData("2024-01-01 23:59:60")]
    [InlineData("2024-01-01 12:34:56.0000001")]
    [InlineData("2024-01-01 12:34:56 ")]
    [InlineData("2024-01-01 12:34:56\0")]
    [InlineData("2024-01-01 12:34:56Z")]
    [InlineData("2024-01-01 12:34:56 UTC")]
    [InlineData("2024-01-01 12:34:56 AD")]
    [InlineData("2024-01-01 12:34:56 bc")]
    [InlineData("Infinity")]
    [InlineData("infinity+00")]
    public void MalformedCalendarAndInstantSyntaxRejectWithoutPayload(string? text)
    {
        Assert.False(KingbaseTimestamp.TryParse(text, out var timestamp)); Assert.Equal(default, timestamp);
        Assert.False(KingbaseTimestampTz.TryParse(text, out var instant)); Assert.Equal(default, instant);
        if (text is null)
        {
            Assert.Throws<ArgumentNullException>(() => KingbaseTimestamp.Parse(null!));
            Assert.Throws<ArgumentNullException>(() => KingbaseTimestampTz.Parse(null!));
        }
        else foreach (var parse in new Action[] { () => KingbaseTimestamp.Parse(text), () => KingbaseTimestampTz.Parse(text) })
        {
            var error = Assert.Throws<FormatException>(parse); Assert.Null(error.InnerException);
            if (text.Length != 0) Assert.DoesNotContain(text, error.Message);
        }
    }

    [Theory]
    [InlineData("2024-01-01 00:00:00")]
    [InlineData("2024-01-01 00:00:00+16")]
    [InlineData("2024-01-01 00:00:00-00:60")]
    [InlineData("2024-01-01 00:00:00+00:00:60")]
    [InlineData("2024-01-01 00:00:00+0000")]
    [InlineData("2024-01-01 00:00:00+00 BC ignored")]
    [InlineData("9999999-12-31 23:59:59-01")]
    [InlineData("9999999-01-01 00:00:00+01 BC")]
    public void InvalidOffsetOrUtcNormalizationOutsideClientCalendarIsNotGuessed(string text)
    {
        Assert.False(KingbaseTimestampTz.TryParse(text, out var value)); Assert.Equal(default, value);
        var error = Assert.Throws<FormatException>(() => KingbaseTimestampTz.Parse(text)); Assert.Null(error.InnerException);
    }

    [Fact]
    public void NumericOffsetNormalizationAgreesWithBclAcrossCalendarDates()
    {
        var random = new Random(3401);
        for (var i = 0; i < 200; i++)
        {
            var date = new DateTime(random.Next(2, 9999), random.Next(1, 13), 1).AddDays(random.Next(0, 27));
            var local = date.AddTicks(random.NextInt64(0, 86400000000L) * 10);
            var offset = TimeSpan.FromMinutes(random.Next(-839, 840));
            var expected = new DateTimeOffset(local, offset).UtcDateTime;
            var text = KingbaseTimestamp.FromDateTime(local).ToString() + new KingbaseTimeTz(TimeSpan.Zero, offset).ToString().Substring(8);
            Assert.Equal(expected, KingbaseTimestampTz.Parse(text).ToDateTime());
        }
    }
}
