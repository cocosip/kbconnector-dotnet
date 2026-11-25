using System.Globalization;

namespace KingbaseConnector.Protocol;

internal static class PgNativeTemporalText
{
    const long MySqlTimeLimit = (838 * 3600L + 59 * 60 + 59) * TimeSpan.TicksPerSecond;
    internal static bool Supports(string name) => name is "oracle_date" or "sqlserver_date" or "sqlserver_datetime" or "sqlserver_datetime2" or
        "sqlserver_smalldatetime" or "sqlserver_time" or "mysql_date" or "mysql_datetime" or "mysql_timestamp" or "mysql_time" or "mysql_year";

    internal static object Decode(string name, string text, string? style, bool provider)
    {
        if (name == "mysql_time") return ParseMySqlTime(text);
        if (name == "mysql_year")
        {
            if (text.Length is < 1 or > 4 || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var year) || !ValidYear(year)) throw InvalidText();
            return year;
        }
        if (name == "sqlserver_time") return ParseSqlTime(text);
        PgTextCodecs.RequireIsoDateStyle(style);
        if (name == "oracle_date")
        {
            var value = KingbaseTimestamp.Parse(text);
            if (value.IsFinite && value.Time.Ticks % TimeSpan.TicksPerSecond != 0) throw InvalidText();
            return provider ? value : value.ToDateTime();
        }
        if (name == "mysql_date") { var value = KingbaseMySqlDate.Parse(text); return provider ? value : value.ToDateTime(); }
        if (name is "mysql_datetime" or "mysql_timestamp") { var value = KingbaseMySqlDateTime.Parse(text); return provider ? value : value.ToDateTime(); }
        if (name == "sqlserver_date") { var value = KingbaseDate.Parse(text); var clr = value.ToDateTime(); return provider ? value : clr; }
        var precision = name == "sqlserver_datetime2" ? 7 : 3;
        if (text.Length > 27 || !ValidFraction(text) || !DateTime.TryParseExact(text, new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss." + new string('F', precision) }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)) throw InvalidText();
        ValidateSqlCalendar(name, result);
        if (name == "sqlserver_smalldatetime" && result.TimeOfDay.Ticks % TimeSpan.TicksPerMinute != 0) throw InvalidText();
        return result;
    }

    internal static string Encode(string name, object value)
    {
        if (name == "oracle_date")
        {
            var timestamp = value is KingbaseTimestamp supplied ? supplied : value is DateTime date ? KingbaseTimestamp.FromDateTime(date) : throw WrongInput();
            if (timestamp.IsFinite && timestamp.Time.Ticks % TimeSpan.TicksPerSecond != 0) throw new ArgumentException("Oracle DATE requires whole seconds.");
            return timestamp.ToString();
        }
        if (name == "mysql_date") return (value switch
        {
            KingbaseMySqlDate date => date, DateTime clr => KingbaseMySqlDate.FromDateTime(clr),
#if NET6_0_OR_GREATER
            DateOnly only => new KingbaseMySqlDate(only.Year, only.Month, only.Day),
#endif
            _ => throw WrongInput()
        }).ToString();
        if (name is "mysql_datetime" or "mysql_timestamp") return (value is KingbaseMySqlDateTime date ? date : value is DateTime clr ? KingbaseMySqlDateTime.FromDateTime(clr) : throw WrongInput()).ToString();
        if (name == "mysql_year") { if (value is not int year) throw WrongInput(); if (!ValidYear(year)) throw new ArgumentOutOfRangeException(nameof(value), "MySQL YEAR requires zero or 1901 through 2155."); return year.ToString(CultureInfo.InvariantCulture); }
        if (name == "sqlserver_date")
        {
            var date = value switch
            {
                KingbaseDate supplied => supplied, DateTime clr => KingbaseDate.FromDateTime(clr),
#if NET6_0_OR_GREATER
                DateOnly only => KingbaseDate.FromDateOnly(only),
#endif
                _ => throw WrongInput()
            };
            _ = date.ToDateTime();
            return date.ToString();
        }
        if (name is "mysql_time" or "sqlserver_time")
        {
            var time = value switch
            {
                TimeSpan span => span,
#if NET6_0_OR_GREATER
                TimeOnly clock => clock.ToTimeSpan(),
#endif
                _ => throw WrongInput()
            };
            if (name == "mysql_time") return FormatMySqlTime(time);
            if (time.Ticks < 0 || time.Ticks >= TimeSpan.TicksPerDay) throw new ArgumentOutOfRangeException(nameof(value), "SQL Server TIME requires a clock below 24 hours.");
            return FormatSqlClock(time, 7);
        }
        if (value is not DateTime calendar) throw WrongInput();
        if (calendar.Kind != DateTimeKind.Unspecified) throw WrongInput();
        ValidateSqlCalendar(name, calendar);
        var digits = name == "sqlserver_datetime2" ? 7 : 3;
        if (digits == 3 && calendar.Ticks % TimeSpan.TicksPerMillisecond != 0) throw new ArgumentException("This SQL Server temporal input requires exact milliseconds.");
        return calendar.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + FormatSqlClock(calendar.TimeOfDay, digits);
    }

    static bool ValidYear(int year) => year == 0 || year is >= 1901 and <= 2155;
    static void ValidateSqlCalendar(string name, DateTime value)
    {
        if ((name == "sqlserver_datetime" && value.Year < 1753) || (name == "sqlserver_smalldatetime" && (value < new DateTime(1900, 1, 1) || value.Date > new DateTime(2079, 6, 6))))
            throw new ArgumentOutOfRangeException(nameof(value), "SQL Server temporal value is outside the supported calendar range.");
    }
    static string FormatSqlClock(TimeSpan value, int digits) => value.ToString("hh\\:mm\\:ss\\." + new string('f', digits), CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    static TimeSpan ParseSqlTime(string text)
    {
        if (text.Length is < 8 or > 16 || !ValidFraction(text) || !TimeSpan.TryParseExact(text, new[] { "hh\\:mm\\:ss", "hh\\:mm\\:ss\\.FFFFFFF" }, CultureInfo.InvariantCulture, out var time) || time.Ticks < 0 || time.Ticks >= TimeSpan.TicksPerDay) throw InvalidText();
        return time;
    }
    static string FormatMySqlTime(TimeSpan time)
    {
        if (time.Ticks < -MySqlTimeLimit || time.Ticks > MySqlTimeLimit) throw new ArgumentOutOfRangeException(nameof(time), "MySQL TIME exceeds the bounded native duration range.");
        if (time.Ticks % 10 != 0) throw new ArgumentException("MySQL TIME requires exact microseconds.");
        var ticks = Math.Abs(time.Ticks);
        var clock = (ticks / TimeSpan.TicksPerHour).ToString("D2", CultureInfo.InvariantCulture) + ":" + ((ticks / TimeSpan.TicksPerMinute) % 60).ToString("D2", CultureInfo.InvariantCulture) + ":" + ((ticks / TimeSpan.TicksPerSecond) % 60).ToString("D2", CultureInfo.InvariantCulture);
        var fraction = ticks % TimeSpan.TicksPerSecond / 10;
        return (time.Ticks < 0 ? "-" : "") + clock + (fraction == 0 ? "" : "." + fraction.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0'));
    }
    static TimeSpan ParseMySqlTime(string text)
    {
        if (text.Length is < 8 or > 17) throw InvalidText();
        var negative = text[0] == '-'; var start = negative ? 1 : 0; var colon = text.IndexOf(':');
        if (colon - start is < 2 or > 3) throw InvalidText();
        var hours = 0;
        for (var i = start; i < colon; i++) { if (text[i] is < '0' or > '9') throw InvalidText(); hours = hours * 10 + text[i] - '0'; }
        var clock = "00" + text.Substring(colon);
        if (!PgTimeText.TryRead(clock, out var remainder, out var end) || end != clock.Length) throw InvalidText();
        var ticks = hours * TimeSpan.TicksPerHour + remainder.Ticks;
        if (ticks > MySqlTimeLimit) throw InvalidText();
        return TimeSpan.FromTicks(negative ? -ticks : ticks);
    }
    static bool ValidFraction(string text)
    {
        var dot = text.IndexOf('.');
        if (dot < 0) return true;
        if (dot == text.Length - 1) return false;
        for (var i = dot + 1; i < text.Length; i++) if (text[i] is < '0' or > '9') return false;
        return true;
    }
    static InvalidCastException WrongInput() => new("Native temporal input requires its exact calendar or clock representation and Unspecified DateTime Kind.");
    static FormatException InvalidText() => new("Invalid native temporal text or range.");
}
