using System.Globalization;

namespace KingbaseConnector;

/// <summary>Lossless MySQL calendar components, including zero, partial and non-CLR dates.</summary>
public readonly struct KingbaseMySqlDate : IEquatable<KingbaseMySqlDate>
{
    public KingbaseMySqlDate(int year, int month, int day)
    {
        if (year is < 0 or > 9999999) throw new ArgumentOutOfRangeException(nameof(year));
        if (month is < 0 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        if (day is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(day));
        Year = year; Month = month; Day = day;
    }
    public int Year { get; }
    public int Month { get; }
    public int Day { get; }
    public bool IsZero => Year == 0 && Month == 0 && Day == 0;
    public bool HasZeroComponents => Year == 0 || Month == 0 || Day == 0;

    public static KingbaseMySqlDate Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var digits = text.Length - 6;
        if (digits is < 4 or > 7 || text[digits] != '-' || text[digits + 3] != '-' || (digits > 4 && text[0] == '0')) throw InvalidText();
        var year = Read(0, digits); var month = Read(digits + 1, 2); var day = Read(digits + 4, 2);
        if (month > 12 || day > 31) throw InvalidText();
        return new(year, month, day);

        int Read(int start, int count)
        {
            var value = 0;
            for (var i = start; i < start + count; i++)
            { if (text[i] is < '0' or > '9') throw InvalidText(); value = value * 10 + text[i] - '0'; }
            return value;
        }
    }
    public static bool TryParse(string? text, out KingbaseMySqlDate value)
    {
        value = default;
        if (text is null) return false;
        try { value = Parse(text); return true; } catch (FormatException) { return false; }
    }
    public static KingbaseMySqlDate FromDateTime(DateTime value)
    {
        var date = KingbaseDate.FromDateTime(value); return new(date.Year, date.Month, date.Day);
    }
    /// <summary>Returns Unspecified midnight; zero, invalid and out-of-CLR dates throw.</summary>
    public DateTime ToDateTime()
    {
        try { return new DateTime(Year, Month, Day, 0, 0, 0, DateTimeKind.Unspecified); }
        catch (ArgumentOutOfRangeException) { throw new OverflowException("The MySQL calendar cannot be represented by DateTime."); }
    }
    public override string ToString() => Year.ToString("D4", CultureInfo.InvariantCulture) + "-" + Month.ToString("D2", CultureInfo.InvariantCulture) + "-" + Day.ToString("D2", CultureInfo.InvariantCulture);
    public bool Equals(KingbaseMySqlDate other) => Year == other.Year && Month == other.Month && Day == other.Day;
    public override bool Equals(object? obj) => obj is KingbaseMySqlDate other && Equals(other);
    public override int GetHashCode() => unchecked((Year * 397 ^ Month) * 397 ^ Day);
    public static bool operator ==(KingbaseMySqlDate left, KingbaseMySqlDate right) => left.Equals(right);
    public static bool operator !=(KingbaseMySqlDate left, KingbaseMySqlDate right) => !left.Equals(right);
    static FormatException InvalidText() => new("Invalid MySQL calendar date text.");
}
