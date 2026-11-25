namespace KingbaseConnector.UnitTests;

public sealed class MySqlCalendarValueTests
{
    [Fact]
    public void DefaultValuesEqualityAndExactClrConversionsRetainTheirOwnSemantics()
    {
        Assert.True(default(KingbaseMySqlDate).IsZero); Assert.True(new KingbaseMySqlDate(2024, 0, 15).HasZeroComponents);
        Assert.Equal("0000-00-00 00:00:00", default(KingbaseMySqlDateTime).ToString());
        var clr = new DateTime(2024, 2, 29, 13, 14, 15, DateTimeKind.Unspecified).AddTicks(1234560);
        var value = KingbaseMySqlDateTime.FromDateTime(clr); Assert.Equal(clr, value.ToDateTime()); Assert.Equal(DateTimeKind.Unspecified, value.ToDateTime().Kind);
        Assert.Equal(value, KingbaseMySqlDateTime.Parse(value.ToString())); Assert.Equal(value.GetHashCode(), KingbaseMySqlDateTime.Parse(value.ToString()).GetHashCode());
        Assert.True(value == KingbaseMySqlDateTime.Parse("2024-02-29 13:14:15.123456")); Assert.True(value != default);
        var date = KingbaseMySqlDate.FromDateTime(clr.Date); Assert.Equal(clr.Date, date.ToDateTime()); Assert.True(date == new KingbaseMySqlDate(2024, 2, 29)); Assert.True(date != default);
        Assert.Equal(KingbaseDbType.MySqlDate, new KingbaseParameter("p", date).KingbaseDbType);
        Assert.Equal(KingbaseDbType.MySqlDateTime, new KingbaseParameter("p", value).KingbaseDbType);
        Assert.Throws<InvalidCastException>(() => KingbaseMySqlDateTime.FromDateTime(DateTime.SpecifyKind(clr, DateTimeKind.Utc)));
        Assert.Throws<InvalidCastException>(() => KingbaseMySqlDate.FromDateTime(clr));
        Assert.Throws<ArgumentException>(() => KingbaseMySqlDateTime.FromDateTime(clr.AddTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KingbaseMySqlDateTime(date, TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KingbaseMySqlDate(-1, 1, 1));
        Assert.Equal(new KingbaseMySqlDate(9999999, 12, 31), KingbaseMySqlDate.Parse("9999999-12-31"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-001-01-01")]
    [InlineData("02024-01-01")]
    [InlineData("2024-13-01")]
    [InlineData("2024-01-32")]
    [InlineData("2024-2-29")]
    [InlineData("２０２４-02-29")]
    [InlineData("2024-02-29 ")]
    public void MySqlDateRejectsMalformedGrammarWithoutNormalizingComponents(string text)
    {
        Assert.False(KingbaseMySqlDate.TryParse(text, out _)); Assert.Throws<FormatException>(() => KingbaseMySqlDate.Parse(text));
    }

    [Theory]
    [InlineData("0000-00-00 24:00:00")]
    [InlineData("2024-02-29T13:14:15")]
    [InlineData("2024-02-29 13:14:15.1234567")]
    [InlineData("2024-02-29 13:14:15+00")]
    [InlineData("2024-02-29 13:14:15 private")]
    public void MySqlCalendarClockRejectsExtraTicksZonesAndPayload(string text)
    {
        Assert.False(KingbaseMySqlDateTime.TryParse(text, out _)); var error = Assert.Throws<FormatException>(() => KingbaseMySqlDateTime.Parse(text));
        Assert.DoesNotContain("private", error.Message);
    }

    [Theory]
    [InlineData("KingbaseMySqlDate", "0000-00-00")]
    [InlineData("KingbaseMySqlDate", "2024-00-15")]
    [InlineData("KingbaseMySqlDate", "2024-02-31")]
    [InlineData("KingbaseMySqlDate", "10000-01-01")]
    [InlineData("KingbaseMySqlDateTime", "0000-00-00 00:00:00")]
    [InlineData("KingbaseMySqlDateTime", "2024-00-15 13:14:15.123456")]
    [InlineData("KingbaseMySqlDateTime", "10000-01-01 00:00:00")]
    public void ProviderValuesPreserveNonClrCalendarsWithoutInventingMinValue(string name, string text)
    {
        var type = typeof(KingbaseConnection).Assembly.GetType("KingbaseConnector." + name); Assert.NotNull(type);
        var value = type.GetMethod("Parse")!.Invoke(null, [text]); Assert.NotNull(value); Assert.Equal(text, value.ToString());
        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => type.GetMethod("ToDateTime")!.Invoke(value, null)); Assert.IsType<OverflowException>(error.InnerException);
    }
}
