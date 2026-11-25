using System.Globalization;

namespace KingbaseConnector.UnitTests;

public sealed class DateTests
{
    [Theory]
    [InlineData("0001-01-01", 1, 1, 1)]
    [InlineData("2000-02-29", 2000, 2, 29)]
    [InlineData("1900-03-01", 1900, 3, 1)]
    [InlineData("9999-12-31", 9999, 12, 31)]
    [InlineData("10000-01-01", 10000, 1, 1)]
    [InlineData("5874897-12-31", 5874897, 12, 31)]
    [InlineData("0001-02-29 BC", -1, 2, 29)]
    [InlineData("0101-02-28 BC", -101, 2, 28)]
    [InlineData("0401-02-29 BC", -401, 2, 29)]
    [InlineData("4714-11-24 BC", -4714, 11, 24)]
    public void CalendarRoundTripsIncludeHistoricalEraAndGregorianLeapYears(string text, int year, int month, int day)
    {
        var value = KingbaseDate.Parse(text);
        Assert.True(value.IsFinite); Assert.False(value.IsInfinity);
        Assert.Equal(year, value.Year); Assert.Equal(month, value.Month); Assert.Equal(day, value.Day);
        Assert.Equal(text, value.ToString()); Assert.Equal(new KingbaseDate(year, month, day), value);
        Assert.True(KingbaseDate.TryParse(text, out var parsed)); Assert.Equal(value, parsed);
        Assert.Equal(value.GetHashCode(), parsed.GetHashCode());
        Assert.Equal(value, (object)parsed);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(int.MinValue, 1, 1)]
    [InlineData(int.MaxValue, 1, 1)]
    [InlineData(2000, 0, 1)]
    [InlineData(2000, 13, 1)]
    [InlineData(2000, 1, 0)]
    [InlineData(2000, 4, 31)]
    [InlineData(1900, 2, 29)]
    [InlineData(2100, 2, 29)]
    [InlineData(-101, 2, 29)]
    [InlineData(-4, 2, 29)]
    public void RejectsInvalidCalendarComponents(int year, int month, int day) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new KingbaseDate(year, month, day));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000-01-01")]
    [InlineData("0000-01-01 BC")]
    [InlineData("1900-02-29")]
    [InlineData("0004-02-29 BC")]
    [InlineData("2024-04-31")]
    [InlineData("2024-13-01")]
    [InlineData("2024-00-01")]
    [InlineData("2024-01-00")]
    [InlineData("2024-01-32")]
    [InlineData("2024-1-01")]
    [InlineData("2024/01/01")]
    [InlineData("01-02-2024")]
    [InlineData(" 2024-01-01")]
    [InlineData("2024-01-01 ")]
    [InlineData("2024-01-01 AD")]
    [InlineData("2024-01-01 bc")]
    [InlineData("02024-01-01")]
    [InlineData("-0001-01-01")]
    [InlineData("+2024-01-01")]
    [InlineData("２０２４-01-01")]
    [InlineData("2024-01-01\0")]
    [InlineData("2024-01-01 00:00:00")]
    [InlineData("Infinity")]
    [InlineData("+infinity")]
    public void MalformedTextNeverLeaksItsPayload(string? text)
    {
        Assert.False(KingbaseDate.TryParse(text, out var value)); Assert.Equal(default, value);
        if (text is null) Assert.Throws<ArgumentNullException>(() => KingbaseDate.Parse(null!));
        else
        {
            var error = Assert.Throws<FormatException>(() => KingbaseDate.Parse(text));
            Assert.Null(error.InnerException);
            if (text.Length > 0) Assert.DoesNotContain(text, error.Message);
        }
    }

    [Theory]
    [InlineData("infinity")]
    [InlineData("-infinity")]
    [InlineData("0001-01-01 BC")]
    [InlineData("10000-01-01")]
    public void NonClrDatesStayLosslessAndNeverMapToClrSentinels(string text)
    {
        var value = KingbaseDate.Parse(text);
        Assert.Equal(text, value.ToString());
        Assert.Throws<OverflowException>(() => value.ToDateTime());
        Assert.Throws<OverflowException>(() => value.ToDateOnly());
        if (value.IsInfinity) Assert.Throws<InvalidOperationException>(() => value.Year);
    }

    [Fact]
    public void DefaultEqualityCultureAndClrConversionsUseExactCalendarDays()
    {
        Assert.Equal("0001-01-01", default(KingbaseDate).ToString());
        Assert.Equal(KingbaseDate.Parse("infinity"), KingbaseDate.PositiveInfinity);
        Assert.Equal(KingbaseDate.Parse("-infinity"), KingbaseDate.NegativeInfinity);
        Assert.True(KingbaseDate.PositiveInfinity != KingbaseDate.NegativeInfinity);
        Assert.False(default(KingbaseDate).Equals(null));
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            foreach (var date in new[] { DateTime.MinValue, new DateTime(1582, 10, 10), new DateTime(2000, 2, 29), DateTime.MaxValue.Date })
            {
                var value = KingbaseDate.FromDateTime(date);
                Assert.Equal(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), value.ToString());
                Assert.Equal(date, value.ToDateTime()); Assert.Equal(DateTimeKind.Unspecified, value.ToDateTime().Kind);
                Assert.Equal(DateOnly.FromDateTime(date), value.ToDateOnly());
                Assert.Equal(value, KingbaseDate.FromDateOnly(value.ToDateOnly()));
            }
        }
        finally { CultureInfo.CurrentCulture = old; }
        Assert.Throws<InvalidCastException>(() => KingbaseDate.FromDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)));
        Assert.Throws<InvalidCastException>(() => KingbaseDate.FromDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Local)));
        Assert.Throws<InvalidCastException>(() => KingbaseDate.FromDateTime(new DateTime(2000, 1, 1).AddTicks(1)));
        Assert.False(KingbaseDate.TryParse(new string('9', 1024 * 1024), out _));
    }
}
