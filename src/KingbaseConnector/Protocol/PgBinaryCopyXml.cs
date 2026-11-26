using System.Text.RegularExpressions;

namespace KingbaseConnector.Protocol;

internal static class PgBinaryCopyXml
{
    static readonly Regex Declaration = new(
        "\\G<\\?xml[ \\t\\r\\n]+version[ \\t\\r\\n]*=[ \\t\\r\\n]*(?:'1\\.[0-9]+'|\"1\\.[0-9]+\")" +
        "(?<encoding>[ \\t\\r\\n]+encoding[ \\t\\r\\n]*=[ \\t\\r\\n]*(?:'[A-Za-z][A-Za-z0-9._-]*'|\"[A-Za-z][A-Za-z0-9._-]*\"))?" +
        "(?:[ \\t\\r\\n]+standalone[ \\t\\r\\n]*=[ \\t\\r\\n]*(?:'(?:yes|no)'|\"(?:yes|no)\"))?[ \\t\\r\\n]*\\?>",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    internal static byte[] Encode(object value, int budget)
    {
        if (value is not string text || text.IndexOf('\0') >= 0)
            throw new InvalidCastException("Binary COPY XML input requires String without literal NUL.");
        var start = text.Length > 0 && text[0] == '\ufeff' ? 1 : 0;
        Group encoding;
        try
        {
            // A CLR String already has Unicode characters; binary xml_recv otherwise trusts its encoding declaration.
            encoding = Declaration.Match(text, start).Groups["encoding"];
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ArgumentException("Binary COPY XML declaration exceeds the normalization resource limit.");
        }
        var length = PgBinary.Utf8.GetByteCount(text) - (start == 0 ? 0 : 3) - encoding.Length;
        if (length > budget) throw new ArgumentException("Binary COPY row values exceed 1 MiB.");
        var bytes = new byte[length];
        if (!encoding.Success) PgBinary.Utf8.GetBytes(text, start, text.Length - start, bytes, 0);
        else
        {
            var written = PgBinary.Utf8.GetBytes(text, start, encoding.Index - start, bytes, 0);
            var suffix = encoding.Index + encoding.Length;
            PgBinary.Utf8.GetBytes(text, suffix, text.Length - suffix, bytes, written);
        }
        return bytes;
    }
}
