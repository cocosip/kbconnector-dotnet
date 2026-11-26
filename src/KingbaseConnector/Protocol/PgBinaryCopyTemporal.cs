namespace KingbaseConnector.Protocol;

internal static class PgBinaryCopyTemporal
{
    const long MicrosecondsPerDay = 86400000000;
    const long MinDay = -2451545;
    const long DateEndDay = 2145031949;
    const long TimestampEndDay = 106751983;

    internal static bool Supports(string name) => name is "date" or "time" or "timetz" or "timestamp" or "timestamptz" or "interval";

    internal static byte[] Encode(string name, object value, int budget)
    {
        var length = Size(name);
        if (length > budget) throw new ArgumentException("Binary COPY row values exceed 1 MiB.");
        var bytes = new byte[length];
        switch (name)
        {
            case "date":
                var date = value switch {
                    KingbaseDate exact => exact, DateTime clr => KingbaseDate.FromDateTime(clr),
#if NET6_0_OR_GREATER
                    DateOnly only => KingbaseDate.FromDateOnly(only),
#endif
                    _ => throw WrongInput() };
                var day = !date.IsFinite ? date == KingbaseDate.PositiveInfinity ? int.MaxValue : int.MinValue : CheckedDay(date, DateEndDay);
                PgBinary.WriteInt32(bytes, 0, (int)day); break;
            case "time": WriteInt64(bytes, 0, Time(value).Ticks / 10); break;
            case "timetz":
                if (value is not KingbaseTimeTz timeTz) throw WrongInput();
                WriteInt64(bytes, 0, timeTz.Time.Ticks / 10);
                PgBinary.WriteInt32(bytes, 8, -(int)(timeTz.Offset.Ticks / TimeSpan.TicksPerSecond)); break;
            case "timestamp":
                WriteInt64(bytes, 0, Timestamp(value is KingbaseTimestamp timestamp ? timestamp : value is DateTime clrTimestamp ? KingbaseTimestamp.FromDateTime(clrTimestamp) : throw WrongInput())); break;
            case "timestamptz":
                var instant = value switch { KingbaseTimestampTz exact => exact, DateTime clr => KingbaseTimestampTz.FromDateTime(clr),
                    DateTimeOffset offset => KingbaseTimestampTz.FromDateTimeOffset(offset), _ => throw WrongInput() };
                WriteInt64(bytes, 0, Timestamp(instant.UtcValue)); break;
            case "interval":
                var interval = value is KingbaseInterval exactInterval ? exactInterval : value is TimeSpan duration ? KingbaseInterval.FromTimeSpan(duration) : throw WrongInput();
                WriteInt64(bytes, 0, interval.Microseconds); PgBinary.WriteInt32(bytes, 8, interval.Days); PgBinary.WriteInt32(bytes, 12, interval.Months); break;
            default: throw WrongInput();
        }
        return bytes;
    }

    internal static object Decode(string name, byte[] bytes)
    {
        if (bytes.Length != Size(name)) throw Invalid();
        switch (name)
        {
            case "date":
                var day = PgBinary.ReadInt32(bytes, 0);
                if (day == int.MaxValue) return KingbaseDate.PositiveInfinity;
                if (day == int.MinValue) return KingbaseDate.NegativeInfinity;
                if (day < MinDay || day >= DateEndDay) throw Invalid();
                return Date(day);
            case "time": return ReadTime(bytes);
            case "timetz":
                var secondsWest = PgBinary.ReadInt32(bytes, 8);
                if (secondsWest is <= -57600 or >= 57600) throw Invalid();
                return new KingbaseTimeTz(ReadTime(bytes), TimeSpan.FromTicks(-(long)secondsWest * TimeSpan.TicksPerSecond));
            case "timestamp": return ReadTimestamp(bytes);
            case "timestamptz": return new KingbaseTimestampTz(ReadTimestamp(bytes));
            case "interval": return new KingbaseInterval(PgBinary.ReadInt32(bytes, 12), PgBinary.ReadInt32(bytes, 8), ReadInt64(bytes));
            default: throw Invalid();
        }
    }

    static int Size(string name) => name switch { "date" => 4, "timetz" => 12, "interval" => 16, _ => 8 };
    static TimeSpan Time(object value)
    {
        var time = value switch { TimeSpan exact => exact,
#if NET6_0_OR_GREATER
            TimeOnly only => only.ToTimeSpan(),
#endif
            _ => throw WrongInput() };
        PgTimeText.Validate(time, nameof(value)); return time;
    }
    static TimeSpan ReadTime(byte[] bytes)
    {
        var micros = ReadInt64(bytes);
        if (micros < 0 || micros > MicrosecondsPerDay) throw Invalid();
        return TimeSpan.FromTicks(micros * 10);
    }
    static long Timestamp(KingbaseTimestamp timestamp) => !timestamp.IsFinite ? timestamp == KingbaseTimestamp.PositiveInfinity ? long.MaxValue : long.MinValue :
        CheckedDay(timestamp.Date, TimestampEndDay) * MicrosecondsPerDay + timestamp.Time.Ticks / 10;
    static KingbaseTimestamp ReadTimestamp(byte[] bytes)
    {
        var micros = ReadInt64(bytes);
        if (micros == long.MaxValue) return KingbaseTimestamp.PositiveInfinity;
        if (micros == long.MinValue) return KingbaseTimestamp.NegativeInfinity;
        if (micros < MinDay * MicrosecondsPerDay || micros >= TimestampEndDay * MicrosecondsPerDay) throw Invalid();
        var day = Floor(micros, MicrosecondsPerDay);
        return new KingbaseTimestamp(Date(day), TimeSpan.FromTicks((micros - day * MicrosecondsPerDay) * 10));
    }
    static long CheckedDay(KingbaseDate date, long end)
    {
        var year = date.Year < 0 ? date.Year + 1 : date.Year;
        var day = StartOfYear(year) - 730119 + date.Day - 1;
        for (var month = 1; month < date.Month; month++) day += MonthDays(year, month);
        if (day < MinDay || day >= end) throw new ArgumentOutOfRangeException(nameof(date), "The date exceeds the PG binary type range.");
        return day;
    }
    static KingbaseDate Date(long day)
    {
        var absolute = day + 730119; var lower = -9999998; var upper = 10000000;
        while (lower + 1 < upper) { var middle = lower + (upper - lower) / 2; if (StartOfYear(middle) <= absolute) lower = middle; else upper = middle; }
        var remaining = absolute - StartOfYear(lower); var month = 1;
        while (remaining >= MonthDays(lower, month)) { remaining -= MonthDays(lower, month); month++; }
        return new KingbaseDate(lower <= 0 ? lower - 1 : lower, month, (int)remaining + 1);
    }
    static long StartOfYear(int year) { var y = (long)year - 1; return 365 * y + Floor(y, 4) - Floor(y, 100) + Floor(y, 400); }
    static int MonthDays(int year, int month) => month == 2 ? year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 29 : 28 : month is 4 or 6 or 9 or 11 ? 30 : 31;
    static long Floor(long value, long divisor) => value / divisor - (value % divisor < 0 ? 1 : 0);
    static long ReadInt64(byte[] bytes) { ulong value = 0; for (var i = 0; i < 8; i++) value = value << 8 | bytes[i]; return (long)value; }
    static void WriteInt64(byte[] bytes, int offset, long value) { for (var i = 7; i >= 0; i--) { bytes[offset + i] = (byte)value; value >>= 8; } }
    static InvalidCastException WrongInput() => new("Binary temporal COPY requires an exact supported CLR or provider value.");
    static IOException Invalid() => new("Invalid binary COPY temporal value.");
}
