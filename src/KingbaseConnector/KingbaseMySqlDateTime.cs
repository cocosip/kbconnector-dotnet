using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>A MySQL wall-clock calendar value, preserving zero dates and exact microseconds.</summary>
public readonly struct KingbaseMySqlDateTime : IEquatable<KingbaseMySqlDateTime>
{
    public KingbaseMySqlDateTime(KingbaseMySqlDate date, TimeSpan time)
    {
        PgTimeText.Validate(time, nameof(time));
        if (time.Ticks == TimeSpan.TicksPerDay) throw new ArgumentOutOfRangeException(nameof(time));
        Date = date; Time = time;
    }
    public KingbaseMySqlDate Date { get; }
    public TimeSpan Time { get; }
    public static KingbaseMySqlDateTime Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text.Length is < 19 or > 29) throw InvalidText();
        var separator = text.IndexOf(' ');
        if (separator is < 10 or > 13 || !KingbaseMySqlDate.TryParse(text.Substring(0, separator), out var date) ||
            !PgTimeText.TryRead(text.Substring(separator + 1), out var time, out var end) || end != text.Length - separator - 1 || time.Ticks == TimeSpan.TicksPerDay) throw InvalidText();
        return new(date, time);
    }
    public static bool TryParse(string? text, out KingbaseMySqlDateTime value)
    {
        value = default;
        if (text is null) return false;
        try { value = Parse(text); return true; } catch (FormatException) { return false; }
    }
    public static KingbaseMySqlDateTime FromDateTime(DateTime value)
    {
        var timestamp = KingbaseTimestamp.FromDateTime(value);
        return new(new(timestamp.Date.Year, timestamp.Date.Month, timestamp.Date.Day), timestamp.Time);
    }
    /// <summary>Returns Unspecified Kind without a local-time or UTC conversion.</summary>
    public DateTime ToDateTime() => Date.ToDateTime().AddTicks(Time.Ticks);
    public override string ToString() => Date + " " + PgTimeText.Format(Time);
    public bool Equals(KingbaseMySqlDateTime other) => Date == other.Date && Time == other.Time;
    public override bool Equals(object? obj) => obj is KingbaseMySqlDateTime other && Equals(other);
    public override int GetHashCode() => unchecked(Date.GetHashCode() * 397 ^ Time.GetHashCode());
    public static bool operator ==(KingbaseMySqlDateTime left, KingbaseMySqlDateTime right) => left.Equals(right);
    public static bool operator !=(KingbaseMySqlDateTime left, KingbaseMySqlDateTime right) => !left.Equals(right);
    static FormatException InvalidText() => new("Invalid MySQL wall-clock timestamp text.");
}
