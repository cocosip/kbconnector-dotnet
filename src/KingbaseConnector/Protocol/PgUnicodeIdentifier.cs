using System.Globalization;
using System.Text;

namespace KingbaseConnector.Protocol;

internal static class PgUnicodeIdentifier
{
    internal static void Validate(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (value.IndexOf('\0') >= 0) throw new ArgumentException("Identifiers cannot contain NUL.", nameof(value));
        try { _ = PgBinary.Utf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ArgumentException("Identifiers require valid Unicode.", nameof(value)); }
    }
    internal static string Decode(string value, bool doubledQuotes = true)
    {
        var output = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '"' && doubledQuotes)
            {
                if (++i == value.Length || value[i] != '"') throw new ArgumentException("Invalid quoted identifier.");
                output.Append('"');
            }
            else if (value[i] != '\\') output.Append(value[i]);
            else
            {
                if (++i == value.Length) throw new ArgumentException("Invalid Unicode identifier escape.");
                if (value[i] == '\\') { output.Append('\\'); continue; }
                var length = value[i] == '+' ? 6 : 4;
                if (length == 6) i++;
                if (i + length > value.Length || !int.TryParse(value.Substring(i, length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) || code == 0 || code > 0x10ffff)
                    throw new ArgumentException("Invalid Unicode identifier escape.");
                output.Append(code <= 0xffff ? ((char)code).ToString() : char.ConvertFromUtf32(code)); i += length - 1;
            }
        }
        var result = output.ToString(); Validate(result); return result;
    }
}
