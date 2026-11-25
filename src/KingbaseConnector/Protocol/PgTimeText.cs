using System.Globalization;

namespace KingbaseConnector.Protocol;

// Shared by TIME and the wall-clock component of TIMETZ; never uses local time or a date.
internal static class PgTimeText
{
    internal static void Validate(TimeSpan value, string parameterName = "value")
    {
        if (value.Ticks < 0 || value.Ticks > TimeSpan.TicksPerDay)
            throw new ArgumentOutOfRangeException(parameterName, "PG time must be between zero and 24 hours.");
        if (value.Ticks % 10 != 0)
            throw new ArgumentException("PG time requires exact microsecond precision.", parameterName);
    }

    internal static string Format(TimeSpan value)
    {
        Validate(value);
        var ticks = value.Ticks;
        var text = (ticks / TimeSpan.TicksPerHour).ToString("D2", CultureInfo.InvariantCulture) + ":" +
            value.Minutes.ToString("D2", CultureInfo.InvariantCulture) + ":" + value.Seconds.ToString("D2", CultureInfo.InvariantCulture);
        var micros = ticks % TimeSpan.TicksPerSecond / 10;
        return micros == 0 ? text : text + "." + micros.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    internal static TimeSpan Parse(string text)
    {
        if (!TryRead(text, out var value, out var end) || end != text.Length) throw InvalidText();
        return value;
    }

    internal static bool TryRead(string text, out TimeSpan value, out int end)
    {
        value = default; end = 0;
        if (text.Length < 8 || text[2] != ':' || text[5] != ':' ||
            !ReadPair(text, 0, out var hour) || !ReadPair(text, 3, out var minute) || !ReadPair(text, 6, out var second) ||
            hour > 24 || minute > 59 || second > 59) return false;
        end = 8;
        var micros = 0;
        if (end < text.Length && text[end] == '.')
        {
            var start = ++end;
            while (end < text.Length && text[end] is >= '0' and <= '9')
            {
                if (end - start == 6) return false;
                micros = micros * 10 + text[end++] - '0';
            }
            if (end == start) return false;
            for (var digits = end - start; digits < 6; digits++) micros *= 10;
        }
        if (hour == 24 && (minute != 0 || second != 0 || micros != 0)) return false;
        value = new TimeSpan(hour * TimeSpan.TicksPerHour + minute * TimeSpan.TicksPerMinute + second * TimeSpan.TicksPerSecond + micros * 10L);
        return true;
    }

    internal static bool ReadPair(string text, int start, out int value)
    {
        value = 0;
        if (start + 1 >= text.Length || text[start] is < '0' or > '9' || text[start + 1] is < '0' or > '9') return false;
        value = (text[start] - '0') * 10 + text[start + 1] - '0'; return true;
    }
    internal static FormatException InvalidText() => new("Invalid PG time text.");
}
