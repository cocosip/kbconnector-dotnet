using System.Text;

namespace KingbaseConnector.Protocol;

internal static partial class PgSaslPrep
{
    internal static string Prepare(string password)
    {
        PgFrontendMessages.PasswordTextLength(password);
        var mapped = new StringBuilder(password.Length);
        for (var offset = 0; offset < password.Length; offset++)
        {
            var scalar = char.ConvertToUtf32(password, offset);
            if (scalar > 0xFFFF) offset++;
            if (Contains(Unassigned, scalar)) return password;
            if (Contains(MappedToNothing, scalar)) continue;
            if (Contains(NonAsciiSpace, scalar)) scalar = ' ';
            // Unicode 4.0 corrected these decompositions; SASLprep requires the 3.2 forms.
            scalar = scalar switch { 0x2F868 => 0x2136A, 0x2F874 => 0x5F33, 0x2F91F => 0x43AB,
                0x2F95F => 0x7AAE, 0x2F9BF => 0x4D57, _ => scalar };
            mapped.Append(char.ConvertFromUtf32(scalar));
        }
        string normalized;
        try { normalized = mapped.ToString().Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { return password; }
        var randal = false; var leftToRight = false; var first = -1; var last = -1;
        for (var offset = 0; offset < normalized.Length; offset++)
        {
            var scalar = char.ConvertToUtf32(normalized, offset);
            if (scalar > 0xFFFF) offset++;
            if (Contains(Unassigned, scalar) || Contains(Prohibited, scalar)) return password;
            if (first == -1) first = scalar;
            last = scalar;
            randal |= Contains(RandALCat, scalar); leftToRight |= Contains(LCat, scalar);
        }
        if (randal && (leftToRight || !Contains(RandALCat, first) || !Contains(RandALCat, last))) return password;
        PgFrontendMessages.PasswordTextLength(normalized);
        return normalized;
    }

    static bool Contains(int[] ranges, int scalar)
    {
        var low = 0; var high = ranges.Length / 2 - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (scalar < ranges[mid * 2]) high = mid - 1;
            else if (scalar > ranges[mid * 2 + 1]) low = mid + 1;
            else return true;
        }
        return false;
    }
}
