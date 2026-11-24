using System.Globalization;
using System.Numerics;
using System.Text;

namespace KingbaseConnector;

/// <summary>A bounded exact coefficient and decimal scale, or an explicit numeric special value.</summary>
public readonly struct KingbaseNumeric : IEquatable<KingbaseNumeric>
{
    /// <summary>Maximum coefficient precision allowed by this bounded value representation.</summary>
    public const int MaxDigits = 16384;
    /// <summary>Maximum nonnegative decimal scale allowed by this value representation.</summary>
    public const int MaxScale = 16384;
    static readonly BigInteger CoefficientLimit = BigInteger.Pow(10, MaxDigits);
    static readonly BigInteger DecimalMaxCoefficient = (BigInteger.One << 96) - 1;
    readonly BigInteger _coefficient;
    readonly int _scale;
    readonly byte _kind;

    public KingbaseNumeric(BigInteger coefficient, int scale)
    {
        if (scale < 0 || scale > MaxScale) throw new ArgumentOutOfRangeException(nameof(scale), "Numeric scale exceeds the client value limit.");
        if (BigInteger.Abs(coefficient) >= CoefficientLimit)
            throw new ArgumentOutOfRangeException(nameof(coefficient), "Numeric coefficient exceeds the client value limit.");
        _coefficient = coefficient;
        _scale = scale;
        _kind = 0;
    }

    KingbaseNumeric(byte kind) { _kind = kind; _coefficient = BigInteger.Zero; _scale = 0; }
    public BigInteger Coefficient => IsFinite ? _coefficient : throw Nonfinite();
    public int Scale => IsFinite ? _scale : throw Nonfinite();
    public bool IsFinite => _kind == 0;
    public bool IsNaN => _kind == 1;
    public bool IsInfinity => _kind == 2 || _kind == 3;
    public static KingbaseNumeric NaN => new(1);
    public static KingbaseNumeric PositiveInfinity => new(2);
    public static KingbaseNumeric NegativeInfinity => new(3);

    public static KingbaseNumeric Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text.Length > MaxDigits + MaxScale + 32) throw OutOfRange();
        if (text == "NaN") return NaN;
        if (text == "Infinity") return PositiveInfinity;
        if (text == "-Infinity") return NegativeInfinity;
        var index = text.Length != 0 && (text[0] == '-' || text[0] == '+') ? 1 : 0;
        var digits = new StringBuilder(text.Length);
        ReadDigits();
        var fractionalDigits = 0;
        if (index < text.Length && text[index] == '.')
        {
            index++;
            var start = digits.Length;
            ReadDigits();
            fractionalDigits = digits.Length - start;
        }
        if (digits.Length == 0) throw InvalidText();
        var exponent = 0;
        if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
        {
            var start = ++index;
            if (index < text.Length && (text[index] == '-' || text[index] == '+')) index++;
            var firstDigit = index;
            while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
            if (index == firstDigit || index != text.Length) throw InvalidText();
            if (!int.TryParse(text.Substring(start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) throw OutOfRange();
        }
        if (index != text.Length) throw InvalidText();
        var coefficientText = digits.ToString().TrimStart('0');
        var scale = (long)fractionalDigits - exponent;
        if (scale > MaxScale || coefficientText.Length > MaxDigits) throw OutOfRange();
        if (coefficientText.Length == 0) return new KingbaseNumeric(BigInteger.Zero, (int)Math.Max(0, scale));
        if (scale < 0 && coefficientText.Length - scale > MaxDigits) throw OutOfRange();
        var coefficient = BigInteger.Parse(coefficientText, NumberStyles.None, CultureInfo.InvariantCulture);
        if (text[0] == '-') coefficient = -coefficient;
        if (scale < 0) { coefficient *= BigInteger.Pow(10, (int)-scale); scale = 0; }
        return new KingbaseNumeric(coefficient, (int)scale);

        void ReadDigits()
        {
            while (index < text.Length && text[index] >= '0' && text[index] <= '9') digits.Append(text[index++]);
        }
    }

    public static bool TryParse(string? text, out KingbaseNumeric value)
    {
        value = default;
        if (text is null) return false;
        try { value = Parse(text); return true; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    public static KingbaseNumeric FromDecimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        if (bits[3] < 0) coefficient = -coefficient;
        return new KingbaseNumeric(coefficient, (bits[3] >> 16) & 255);
    }

    /// <summary>Converts exactly; nonfinite values, overflow and any required rounding throw.</summary>
    public decimal ToDecimal()
    {
        if (!IsFinite) throw OutOfRange();
        var coefficient = BigInteger.Abs(_coefficient);
        var scale = _scale;
        if (coefficient.IsZero) scale = Math.Min(scale, 28);
        while (scale > 0 && (scale > 28 || coefficient > DecimalMaxCoefficient))
        {
            var reduced = BigInteger.DivRem(coefficient, 10, out var remainder);
            if (!remainder.IsZero) throw OutOfRange();
            coefficient = reduced;
            scale--;
        }
        if (scale > 28 || coefficient > DecimalMaxCoefficient) throw OutOfRange();
        return new decimal(unchecked((int)(uint)(coefficient & uint.MaxValue)),
            unchecked((int)(uint)((coefficient >> 32) & uint.MaxValue)),
            unchecked((int)(uint)(coefficient >> 64)), _coefficient.Sign < 0, (byte)scale);
    }

    public override string ToString()
    {
        if (!IsFinite) return _kind == 1 ? "NaN" : _kind == 2 ? "Infinity" : "-Infinity";
        var digits = BigInteger.Abs(_coefficient).ToString(CultureInfo.InvariantCulture);
        var sign = _coefficient.Sign < 0 ? "-" : "";
        if (_scale == 0) return sign + digits;
        if (_scale >= digits.Length) return sign + "0." + new string('0', _scale - digits.Length) + digits;
        return sign + digits.Insert(digits.Length - _scale, ".");
    }

    public bool Equals(KingbaseNumeric other)
    {
        if (_kind != other._kind) return false;
        return !IsFinite || Normalize(_coefficient, _scale) == Normalize(other._coefficient, other._scale);
    }
    public override bool Equals(object? obj) => obj is KingbaseNumeric other && Equals(other);
    public override int GetHashCode()
    {
        if (!IsFinite) return _kind;
        var (coefficient, scale) = Normalize(_coefficient, _scale);
        return unchecked(coefficient.GetHashCode() * 397 ^ scale);
    }

    static (BigInteger Coefficient, int Scale) Normalize(BigInteger coefficient, int scale)
    {
        if (coefficient.IsZero) return (BigInteger.Zero, 0);
        while (scale > 0)
        {
            var reduced = BigInteger.DivRem(coefficient, 10, out var remainder);
            if (!remainder.IsZero) break;
            coefficient = reduced;
            scale--;
        }
        return (coefficient, scale);
    }

    static FormatException InvalidText() => new("Invalid numeric text.");
    static OverflowException OutOfRange() => new("Numeric value is outside the exact conversion or client value limits.");
    static InvalidOperationException Nonfinite() => new("A nonfinite numeric has no coefficient or scale.");
    public static bool operator ==(KingbaseNumeric left, KingbaseNumeric right) => left.Equals(right);
    public static bool operator !=(KingbaseNumeric left, KingbaseNumeric right) => !left.Equals(right);
}
