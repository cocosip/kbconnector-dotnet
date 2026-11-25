using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>An exact timestamp instant stored as a UTC calendar value, including BC years and infinities.</summary>
public readonly struct KingbaseTimestampTz : IEquatable<KingbaseTimestampTz>
{
    /// <summary>Interprets an explicitly supplied calendar timestamp as UTC without applying a local timezone.</summary>
    public KingbaseTimestampTz(KingbaseTimestamp utcValue) => UtcValue = utcValue;
    public KingbaseTimestamp UtcValue { get; }
    public bool IsFinite => UtcValue.IsFinite;
    public bool IsInfinity => UtcValue.IsInfinity;
    public static KingbaseTimestampTz PositiveInfinity => new(KingbaseTimestamp.PositiveInfinity);
    public static KingbaseTimestampTz NegativeInfinity => new(KingbaseTimestamp.NegativeInfinity);

    /// <summary>Parses ISO numeric-offset text and normalizes the instant to UTC.</summary>
    public static KingbaseTimestampTz Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text == "infinity") return PositiveInfinity;
        if (text == "-infinity") return NegativeInfinity;
        if (!PgTimestampText.TryRead(text, true, out var date, out var time, out var offset)) throw PgTimestampText.InvalidText();
        try { return new(PgTimestampText.ToUtc(date, time, offset)); }
        catch (OverflowException) { throw PgTimestampText.InvalidText(); }
    }
    public static bool TryParse(string? text, out KingbaseTimestampTz value)
    {
        value = default;
        if (text is null) return false;
        try { value = Parse(text); return true; }
        catch (FormatException) { return false; }
    }
    /// <summary>Requires UTC Kind and exact microsecond precision.</summary>
    public static KingbaseTimestampTz FromDateTime(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc) throw new InvalidCastException("TIMESTAMPTZ input requires UTC DateTime Kind.");
        return new(KingbaseTimestamp.FromDateTime(DateTime.SpecifyKind(value, DateTimeKind.Unspecified)));
    }
    /// <summary>Normalizes the explicitly supplied offset to UTC without consulting the machine timezone.</summary>
    public static KingbaseTimestampTz FromDateTimeOffset(DateTimeOffset value) => FromDateTime(value.UtcDateTime);
    public DateTime ToDateTime() => DateTime.SpecifyKind(UtcValue.ToDateTime(), DateTimeKind.Utc);
    public DateTimeOffset ToDateTimeOffset() => new(ToDateTime());
    public override string ToString() => PgTimestampText.Format(UtcValue, true);
    public bool Equals(KingbaseTimestampTz other) => UtcValue == other.UtcValue;
    public override bool Equals(object? obj) => obj is KingbaseTimestampTz other && Equals(other);
    public override int GetHashCode() => UtcValue.GetHashCode();
    public static bool operator ==(KingbaseTimestampTz left, KingbaseTimestampTz right) => left.Equals(right);
    public static bool operator !=(KingbaseTimestampTz left, KingbaseTimestampTz right) => !left.Equals(right);
}
