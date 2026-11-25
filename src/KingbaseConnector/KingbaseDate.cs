using System.Globalization;

namespace KingbaseConnector;

/// <summary>A proleptic Gregorian calendar date, including BC years and explicit infinities.</summary>
public readonly struct KingbaseDate : IEquatable<KingbaseDate>
{
    readonly int _yearMinusOne;
    readonly byte _monthMinusOne;
    readonly byte _dayMinusOne;
    readonly sbyte _infinity;

    /// <summary>Creates a calendar date. Negative years denote BC; there is no year zero.</summary>
    /// <remarks>Absolute years are bounded to seven digits. The server enforces its own date range.</remarks>
    public KingbaseDate(int year, int month, int day)
    {
        if (year == 0 || year is < -9999999 or > 9999999) throw new ArgumentOutOfRangeException(nameof(year));
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        var astronomicalYear = year < 0 ? year + 1 : year;
        var leap = astronomicalYear % 4 == 0 && (astronomicalYear % 100 != 0 || astronomicalYear % 400 == 0);
        var days = month == 2 ? leap ? 29 : 28 : month is 4 or 6 or 9 or 11 ? 30 : 31;
        if (day < 1 || day > days) throw new ArgumentOutOfRangeException(nameof(day));
        _yearMinusOne = year - 1;
        _monthMinusOne = (byte)(month - 1);
        _dayMinusOne = (byte)(day - 1);
        _infinity = 0;
    }

    KingbaseDate(sbyte infinity) { _infinity = infinity; _yearMinusOne = 0; _monthMinusOne = 0; _dayMinusOne = 0; }
    public bool IsFinite => _infinity == 0;
    public bool IsInfinity => !IsFinite;
    public static KingbaseDate PositiveInfinity => new(1);
    public static KingbaseDate NegativeInfinity => new(-1);
    public int Year => IsFinite ? _yearMinusOne + 1 : throw Nonfinite();
    public int Month => IsFinite ? _monthMinusOne + 1 : throw Nonfinite();
    public int Day => IsFinite ? _dayMinusOne + 1 : throw Nonfinite();

    /// <summary>Parses ISO Y-M-D (four to seven year digits), optional BC, or lowercase infinity tokens.</summary>
    public static KingbaseDate Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text == "infinity") return PositiveInfinity;
        if (text == "-infinity") return NegativeInfinity;
        if (text.Length is < 10 or > 16) throw InvalidText();
        var bc = text.EndsWith(" BC", StringComparison.Ordinal);
        var end = text.Length - (bc ? 3 : 0);
        var digits = end - 6;
        if (digits is < 4 or > 7 || (digits > 4 && text[0] == '0') || text[digits] != '-' || text[digits + 3] != '-')
            throw InvalidText();
        var year = Read(0, digits);
        try { return new KingbaseDate(bc ? -year : year, Read(digits + 1, 2), Read(digits + 4, 2)); }
        catch (ArgumentOutOfRangeException) { throw InvalidText(); }

        int Read(int start, int count)
        {
            var value = 0;
            for (var i = start; i < start + count; i++)
            {
                if (text[i] is < '0' or > '9') throw InvalidText();
                value = value * 10 + text[i] - '0';
            }
            return value;
        }
    }

    public static bool TryParse(string? text, out KingbaseDate value)
    {
        value = default;
        if (text is null) return false;
        try { value = Parse(text); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>Creates an exact date; rejects time components and any timezone Kind.</summary>
    public static KingbaseDate FromDateTime(DateTime value)
    {
        if (value.Kind != DateTimeKind.Unspecified || value.TimeOfDay != TimeSpan.Zero)
            throw new InvalidCastException("DATE input requires an Unspecified midnight DateTime.");
        return new(value.Year, value.Month, value.Day);
    }

    /// <summary>Returns Unspecified midnight; BC, out-of-CLR-range dates and infinities throw.</summary>
    public DateTime ToDateTime()
    {
        if (!IsFinite || Year is < 1 or > 9999) throw new OverflowException("The date cannot be represented by DateTime.");
        return new DateTime(Year, Month, Day, 0, 0, 0, DateTimeKind.Unspecified);
    }

#if NET6_0_OR_GREATER
    public static KingbaseDate FromDateOnly(DateOnly value) => new(value.Year, value.Month, value.Day);
    public DateOnly ToDateOnly() => DateOnly.FromDateTime(ToDateTime());
#endif

    public override string ToString() => !IsFinite ? _infinity > 0 ? "infinity" : "-infinity" :
        Math.Abs(Year).ToString("D4", CultureInfo.InvariantCulture) + "-" + Month.ToString("D2", CultureInfo.InvariantCulture) +
        "-" + Day.ToString("D2", CultureInfo.InvariantCulture) + (Year < 0 ? " BC" : "");
    public bool Equals(KingbaseDate other) => _infinity == other._infinity && _yearMinusOne == other._yearMinusOne &&
        _monthMinusOne == other._monthMinusOne && _dayMinusOne == other._dayMinusOne;
    public override bool Equals(object? obj) => obj is KingbaseDate other && Equals(other);
    public override int GetHashCode() => unchecked((((_yearMinusOne * 397) ^ _monthMinusOne) * 397 ^ _dayMinusOne) * 397 ^ _infinity);
    public static bool operator ==(KingbaseDate left, KingbaseDate right) => left.Equals(right);
    public static bool operator !=(KingbaseDate left, KingbaseDate right) => !left.Equals(right);
    static InvalidOperationException Nonfinite() => new("An infinite date has no calendar components.");
    static FormatException InvalidText() => new("Invalid ISO calendar date text.");
}
