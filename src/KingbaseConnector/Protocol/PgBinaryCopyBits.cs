using System.Collections;

namespace KingbaseConnector.Protocol;

internal static class PgBinaryCopyBits
{
    internal static byte[] Encode(object value, int budget)
    {
        if (value is not BitArray bits) throw new InvalidCastException("Binary bit COPY requires BitArray.");
        PgTextCodecs.ValidateBitLength(bits);
        var length = 4 + (bits.Length + 7) / 8;
        if (length > budget) throw new ArgumentException("Binary COPY row values exceed 1 MiB.");
        var bytes = new byte[length]; PgBinary.WriteInt32(bytes, 0, bits.Length);
        for (var i = 0; i < bits.Length; i++) if (bits[i]) bytes[4 + i / 8] |= (byte)(0x80 >> (i % 8));
        return bytes;
    }

    internal static BitArray Decode(byte[] bytes)
    {
        if (bytes.Length < 4) throw Invalid();
        var count = PgBinary.ReadInt32(bytes, 0);
        if (count < 0 || bytes.Length != 4 + (count + 7L) / 8) throw Invalid();
        if (count % 8 != 0 && (bytes[bytes.Length - 1] & ((1 << (8 - count % 8)) - 1)) != 0) throw Invalid();
        if (count > PgBinaryCopyCodec.MaxRowBytes) throw new OverflowException("Binary bit COPY exceeds the client bit-count limit.");
        var bits = new BitArray(count);
        for (var i = 0; i < count; i++) bits[i] = (bytes[4 + i / 8] & (0x80 >> (i % 8))) != 0;
        return bits;
    }

    static IOException Invalid() => new("Invalid binary COPY bit string.");
}
