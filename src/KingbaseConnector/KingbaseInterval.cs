using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>An exact interval with independent calendar months, days and clock microseconds.</summary>
public readonly struct KingbaseInterval : IEquatable<KingbaseInterval>
{
    public KingbaseInterval(int months, int days, long microseconds)
    {
        Months = months; Days = days; Microseconds = microseconds;
    }

    public int Months { get; }
    public int Days { get; }
    public long Microseconds { get; }

    /// <summary>Requires exact microseconds and stores the duration without converting clock hours into calendar days.</summary>
    public static KingbaseInterval FromTimeSpan(TimeSpan value)
    {
        if (value.Ticks % 10 != 0) throw new ArgumentException("INTERVAL input requires exact microsecond precision.", nameof(value));
        return new(0, 0, value.Ticks / 10);
    }

    /// <summary>Requires zero months. Combines days and clock microseconds exactly, with range checking.</summary>
    public TimeSpan ToTimeSpan()
    {
        if (Months != 0) throw new InvalidCastException("Calendar months cannot be represented by a fixed TimeSpan.");
        var ticks = ((decimal)Days * 86400000000m + Microseconds) * 10;
        if (ticks < long.MinValue || ticks > long.MaxValue) throw new OverflowException("INTERVAL exceeds the TimeSpan range.");
        return TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>Parses bounded ISO duration fields without rounding or a month-to-day conversion.</summary>
    public static KingbaseInterval Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        return PgIntervalText.Parse(text, "iso_8601");
    }

    public static bool TryParse(string? text, out KingbaseInterval value)
    {
        value = default;
        if (text is null) return false;
        try { value = Parse(text); return true; }
        catch (FormatException) { return false; }
    }

    public override string ToString() => PgIntervalText.Format(this);
    public bool Equals(KingbaseInterval other) => Months == other.Months && Days == other.Days && Microseconds == other.Microseconds;
    public override bool Equals(object? obj) => obj is KingbaseInterval other && Equals(other);
    public override int GetHashCode() => unchecked((Months * 397 ^ Days) * 397 ^ Microseconds.GetHashCode());
    public static bool operator ==(KingbaseInterval left, KingbaseInterval right) => left.Equals(right);
    public static bool operator !=(KingbaseInterval left, KingbaseInterval right) => !left.Equals(right);
}
