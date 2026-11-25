using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>An exact calendar timestamp without a timezone, including BC years and infinities.</summary>
public readonly struct KingbaseTimestamp : IEquatable<KingbaseTimestamp>
{
    readonly KingbaseDate _date;
    readonly TimeSpan _time;

    public KingbaseTimestamp(KingbaseDate date, TimeSpan time)
    {
        if (!date.IsFinite) throw new ArgumentException("A finite timestamp requires a finite date.", nameof(date));
        PgTimeText.Validate(time, nameof(time));
        if (time.Ticks == TimeSpan.TicksPerDay) throw new ArgumentOutOfRangeException(nameof(time), "Timestamp time must be below 24 hours.");
        _date = date; _time = time;
    }
    KingbaseTimestamp(KingbaseDate infinity) { _date = infinity; _time = default; }
    public bool IsFinite => _date.IsFinite;
    public bool IsInfinity => !IsFinite;
    public static KingbaseTimestamp PositiveInfinity => new(KingbaseDate.PositiveInfinity);
    public static KingbaseTimestamp NegativeInfinity => new(KingbaseDate.NegativeInfinity);
    public KingbaseDate Date => IsFinite ? _date : throw Nonfinite();
    public TimeSpan Time => IsFinite ? _time : throw Nonfinite();

    public static KingbaseTimestamp Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text == "infinity") return PositiveInfinity;
        if (text == "-infinity") return NegativeInfinity;
        if (!PgTimestampText.TryRead(text, false, out var date, out var time, out _)) throw PgTimestampText.InvalidText();
        return new(date, time);
    }
    public static bool TryParse(string? text, out KingbaseTimestamp value)
    {
        value = default;
        if (text is null) return false;
        try { value = Parse(text); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>Requires Unspecified Kind and exact microsecond precision; never truncates or converts.</summary>
    public static KingbaseTimestamp FromDateTime(DateTime value)
    {
        if (value.Kind != DateTimeKind.Unspecified) throw new InvalidCastException("TIMESTAMP input requires Unspecified DateTime Kind.");
        return new(new KingbaseDate(value.Year, value.Month, value.Day), value.TimeOfDay);
    }
    /// <summary>Returns Unspecified Kind; non-CLR calendar values and infinities throw OverflowException.</summary>
    public DateTime ToDateTime() => _date.ToDateTime().AddTicks(_time.Ticks);
    public override string ToString() => PgTimestampText.Format(this, false);
    public bool Equals(KingbaseTimestamp other) => _date == other._date && _time == other._time;
    public override bool Equals(object? obj) => obj is KingbaseTimestamp other && Equals(other);
    public override int GetHashCode() => unchecked(_date.GetHashCode() * 397 ^ _time.GetHashCode());
    public static bool operator ==(KingbaseTimestamp left, KingbaseTimestamp right) => left.Equals(right);
    public static bool operator !=(KingbaseTimestamp left, KingbaseTimestamp right) => !left.Equals(right);
    internal string InfinityText => _date.ToString();
    static InvalidOperationException Nonfinite() => new("An infinite timestamp has no calendar or clock components.");
}
