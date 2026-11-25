using System.Globalization;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>An exact PG time of day with an explicit numeric offset east of UTC; no date or timezone rules.</summary>
public readonly struct KingbaseTimeTz : IEquatable<KingbaseTimeTz>
{
    /// <summary>Creates a value with microsecond time precision and whole-second offset precision.</summary>
    public KingbaseTimeTz(TimeSpan time, TimeSpan offset)
    {
        PgTimeText.Validate(time, nameof(time));
        if (offset.Ticks <= -16 * TimeSpan.TicksPerHour || offset.Ticks >= 16 * TimeSpan.TicksPerHour)
            throw new ArgumentOutOfRangeException(nameof(offset), "PG time offset magnitude must be below 16 hours.");
        if (offset.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentException("PG time offset requires whole seconds.", nameof(offset));
        Time = time; Offset = offset;
    }

    /// <summary>Wall-clock time, from midnight through 24:00:00 inclusive.</summary>
    public TimeSpan Time { get; }
    /// <summary>Signed numeric offset east of UTC; independent of the local timezone.</summary>
    public TimeSpan Offset { get; }

    public static KingbaseTimeTz Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (!TryParse(text, out var value)) throw PgTimeText.InvalidText();
        return value;
    }

    public static bool TryParse(string? text, out KingbaseTimeTz value)
    {
        value = default;
        if (text is null || text.Length > 24 || !PgTimeText.TryRead(text, out var time, out var end)) return false;
        var length = text.Length - end;
        if (length is not (3 or 6 or 9) || text[end] is not ('+' or '-') || !PgTimeText.ReadPair(text, end + 1, out var hour) || hour > 15) return false;
        var minute = 0; var second = 0;
        if (length >= 6 && (text[end + 3] != ':' || !PgTimeText.ReadPair(text, end + 4, out minute) || minute > 59)) return false;
        if (length == 9 && (text[end + 6] != ':' || !PgTimeText.ReadPair(text, end + 7, out second) || second > 59)) return false;
        var offset = new TimeSpan(hour * TimeSpan.TicksPerHour + minute * TimeSpan.TicksPerMinute + second * TimeSpan.TicksPerSecond);
        value = new KingbaseTimeTz(time, text[end] == '-' ? -offset : offset); return true;
    }

    public override string ToString()
    {
        var seconds = (int)(Offset.Ticks / TimeSpan.TicksPerSecond);
        var absolute = Math.Abs(seconds);
        var text = PgTimeText.Format(Time) + (seconds < 0 ? "-" : "+") + (absolute / 3600).ToString("D2", CultureInfo.InvariantCulture);
        if (absolute % 3600 != 0) text += ":" + (absolute / 60 % 60).ToString("D2", CultureInfo.InvariantCulture);
        if (absolute % 60 != 0) text += ":" + (absolute % 60).ToString("D2", CultureInfo.InvariantCulture);
        return text;
    }
    /// <summary>Compares the exact wall-clock time and offset without normalization.</summary>
    public bool Equals(KingbaseTimeTz other) => Time == other.Time && Offset == other.Offset;
    public override bool Equals(object? obj) => obj is KingbaseTimeTz other && Equals(other);
    public override int GetHashCode() => unchecked(Time.GetHashCode() * 397 ^ Offset.GetHashCode());
    public static bool operator ==(KingbaseTimeTz left, KingbaseTimeTz right) => left.Equals(right);
    public static bool operator !=(KingbaseTimeTz left, KingbaseTimeTz right) => !left.Equals(right);
}
