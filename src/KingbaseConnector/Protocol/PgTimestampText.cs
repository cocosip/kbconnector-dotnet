namespace KingbaseConnector.Protocol;

internal static class PgTimestampText
{
    internal static bool TryRead(string text, bool zoned, out KingbaseDate date, out TimeSpan time, out TimeSpan offset)
    {
        date = default; time = default; offset = default;
        if (text.Length < 19 || text.Length > (zoned ? 41 : 32)) return false;
        var bc = text.EndsWith(" BC", StringComparison.Ordinal);
        var core = bc ? text.Substring(0, text.Length - 3) : text;
        var separator = core.IndexOf(' ');
        if (separator is < 10 or > 13 || !KingbaseDate.TryParse(core.Substring(0, separator) + (bc ? " BC" : ""), out date) || !date.IsFinite) return false;
        var clock = core.Substring(separator + 1);
        if (zoned)
        {
            if (!KingbaseTimeTz.TryParse(clock, out var value)) return false;
            time = value.Time; offset = value.Offset;
        }
        else if (!PgTimeText.TryRead(clock, out time, out var end) || end != clock.Length) return false;
        return time.Ticks < TimeSpan.TicksPerDay;
    }

    internal static string Format(KingbaseTimestamp value, bool utc)
    {
        if (!value.IsFinite) return value.InfinityText;
        var bc = value.Date.Year < 0;
        var date = value.Date.ToString();
        return (bc ? date.Substring(0, date.Length - 3) : date) + " " + PgTimeText.Format(value.Time) +
            (utc ? "+00" : "") + (bc ? " BC" : "");
    }

    internal static KingbaseTimestamp ToUtc(KingbaseDate date, TimeSpan time, TimeSpan offset)
    {
        var ticks = time.Ticks - offset.Ticks;
        if (ticks < 0) { date = ShiftDay(date, -1); ticks += TimeSpan.TicksPerDay; }
        else if (ticks >= TimeSpan.TicksPerDay) { date = ShiftDay(date, 1); ticks -= TimeSpan.TicksPerDay; }
        return new(date, TimeSpan.FromTicks(ticks));
    }

    static KingbaseDate ShiftDay(KingbaseDate date, int delta)
    {
        var year = date.Year; var month = date.Month; var day = date.Day + delta;
        if (day == 0)
        {
            if (--month == 0) { month = 12; year = year == 1 ? -1 : year - 1; }
            day = DaysInMonth(year, month);
        }
        else if (day > DaysInMonth(year, month))
        {
            day = 1;
            if (++month == 13) { month = 1; year = year == -1 ? 1 : year + 1; }
        }
        try { return new(year, month, day); }
        catch (ArgumentOutOfRangeException) { throw new OverflowException("UTC timestamp exceeds the client calendar bound."); }
    }
    static int DaysInMonth(int historicalYear, int month)
    {
        var year = historicalYear < 0 ? historicalYear + 1 : historicalYear;
        var leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
        return month == 2 ? leap ? 29 : 28 : month is 4 or 6 or 9 or 11 ? 30 : 31;
    }
    internal static FormatException InvalidText() => new("Invalid ISO timestamp text or UTC calendar range.");
}
