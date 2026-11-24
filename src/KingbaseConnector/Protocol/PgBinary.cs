using System.Text;

namespace KingbaseConnector.Protocol;

internal static class PgBinary
{
    internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static int ReadInt32(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    internal static void WriteInt32(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    internal static string ReadCString(byte[] payload, ref int offset)
    {
        var end = Array.IndexOf(payload, (byte)0, offset);
        if (end < 0) throw new IOException("Unterminated PG text field.");
        string text;
        try { text = Utf8.GetString(payload, offset, end - offset); }
        catch (DecoderFallbackException) { throw new IOException("Invalid UTF8 in PG text field."); }
        offset = end + 1;
        return text;
    }
}
