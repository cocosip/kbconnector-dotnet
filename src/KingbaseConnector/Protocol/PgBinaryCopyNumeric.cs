using System.Globalization;
using System.Numerics;

namespace KingbaseConnector.Protocol;

internal static class PgBinaryCopyNumeric
{
    internal static byte[] Encode(object value, int budget)
    {
        var number = value switch { KingbaseNumeric exact => exact, decimal clr => KingbaseNumeric.FromDecimal(clr),
            _ => throw new InvalidCastException("Binary numeric COPY requires decimal or KingbaseNumeric.") };
        if (!number.IsFinite)
        {
            var special = Allocate(0, budget);
            Write(special, 4, number.IsNaN ? 0xC000 : number == KingbaseNumeric.PositiveInfinity ? 0xD000 : 0xF000);
            return special;
        }
        if (number.Scale > 16383) throw new ArgumentOutOfRangeException(nameof(value), "Binary numeric scale must be at most 16383.");
        if (number.Coefficient.IsZero) { var zero = Allocate(0, budget); Write(zero, 6, number.Scale); return zero; }
        var text = BigInteger.Abs(number.Coefficient).ToString(CultureInfo.InvariantCulture);
        var padding = (4 - number.Scale % 4) % 4;
        var weight = (text.Length + padding - 1) / 4 - (number.Scale + padding) / 4;
        text += new string('0', padding);
        var count = (text.Length + 3) / 4;
        var first = text.Length - (count - 1) * 4;
        while (count > 1 && text.Substring(first + (count - 2) * 4, 4) == "0000") count--;
        var bytes = Allocate(count, budget);
        Write(bytes, 0, count); Write(bytes, 2, weight); Write(bytes, 4, number.Coefficient.Sign < 0 ? 0x4000 : 0); Write(bytes, 6, number.Scale);
        for (var i = 0; i < count; i++)
            Write(bytes, 8 + i * 2, int.Parse(text.Substring(i == 0 ? 0 : first + (i - 1) * 4, i == 0 ? first : 4), CultureInfo.InvariantCulture));
        return bytes;
    }

    internal static KingbaseNumeric Decode(byte[] bytes)
    {
        if (bytes.Length < 8) throw Invalid();
        var count = Read(bytes, 0); var weight = (short)Read(bytes, 2); var sign = Read(bytes, 4); var scale = Read(bytes, 6);
        if (bytes.Length != 8 + count * 2 || scale > 16383) throw Invalid();
        if (sign is 0xC000 or 0xD000 or 0xF000)
        {
            if (count != 0 || weight != 0 || scale != 0) throw Invalid();
            return sign == 0xC000 ? KingbaseNumeric.NaN : sign == 0xD000 ? KingbaseNumeric.PositiveInfinity : KingbaseNumeric.NegativeInfinity;
        }
        if (sign is not (0 or 0x4000)) throw Invalid();
        var first = count; var last = -1;
        for (var i = 0; i < count; i++)
        {
            var digit = Read(bytes, 8 + i * 2);
            if (digit > 9999) throw Invalid();
            if (digit != 0) { if (first == count) first = i; last = i; }
        }
        if (last == -1) return new KingbaseNumeric(BigInteger.Zero, scale);
        var exponent = (weight - last) * 4 + scale;
        // A nonzero base-10000 group cannot hide four or more decimal places.
        if (exponent < -3) throw Invalid();
        var divisor = exponent < 0 ? (int)Math.Pow(10, -exponent) : 1;
        if (Read(bytes, 8 + last * 2) % divisor != 0) throw Invalid();
        var leading = Read(bytes, 8 + first * 2);
        var leadingDigits = leading >= 1000 ? 4 : leading >= 100 ? 3 : leading >= 10 ? 2 : 1;
        if ((last - first) * 4 + leadingDigits + exponent > KingbaseNumeric.MaxDigits)
            throw new OverflowException("Binary numeric exceeds the client coefficient limit.");
        var coefficient = BigInteger.Zero;
        for (var i = first; i <= last; i++) coefficient = coefficient * 10000 + Read(bytes, 8 + i * 2);
        if (exponent < 0) coefficient /= divisor;
        else if (exponent > 0) coefficient *= BigInteger.Pow(10, exponent);
        return new KingbaseNumeric(sign == 0x4000 ? -coefficient : coefficient, scale);
    }

    static byte[] Allocate(int count, int budget)
    {
        var length = 8 + count * 2;
        if (length > budget) throw new ArgumentException("Binary COPY row values exceed 1 MiB.");
        return new byte[length];
    }
    static int Read(byte[] bytes, int offset) => bytes[offset] << 8 | bytes[offset + 1];
    static void Write(byte[] bytes, int offset, int value) { bytes[offset] = (byte)(value >> 8); bytes[offset + 1] = (byte)value; }
    static IOException Invalid() => new("Invalid binary COPY numeric.");
}
