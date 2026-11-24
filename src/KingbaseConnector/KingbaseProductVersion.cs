using System.Globalization;
using System.Text.RegularExpressions;

namespace KingbaseConnector;

/// <summary>Preserves a full product identity independently of the PG handshake version.</summary>
internal sealed class KingbaseProductVersion
{
    const string Prefix = "KingbaseES ";
    static readonly Regex ProductToken = new(
        @"\AV(?<major>[0-9]{3})R(?<release>[0-9]{3})C(?<general>[0-9]{3})(?:M(?<custom>[0-9]{3})(?<customer>[A-Za-z0-9_]{0,20}))?B(?<build>[0-9]{3})(?<revision>[0-9])(?:PS(?<patch>[0-9]{3}))?\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    KingbaseProductVersion(string rawText, string token, Match match)
    {
        RawText = rawText;
        Token = token;
        Major = Number(match, "major");
        Release = Number(match, "release");
        GeneralRelease = Number(match, "general");
        Customization = match.Groups["custom"].Success ? Number(match, "custom") : null;
        CustomerCode = match.Groups["customer"].Length == 0 ? null : match.Groups["customer"].Value;
        Build = Number(match, "build");
        BuildRevision = Number(match, "revision");
        PatchSet = match.Groups["patch"].Success ? Number(match, "patch") : null;
    }

    internal string Token { get; }
    internal string RawText { get; }
    internal int Major { get; }
    internal int Release { get; }
    internal int GeneralRelease { get; }
    internal int? Customization { get; }
    internal string? CustomerCode { get; }
    internal int Build { get; }
    internal int BuildRevision { get; }
    internal int? PatchSet { get; }

    internal static bool TryParse(string? text, out KingbaseProductVersion? version)
    {
        version = null;
        if (text is null || text.Length > 4096 || text.IndexOf('\0') >= 0 || !text.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var end = text.IndexOf(' ', Prefix.Length);
        if (end < 0) end = text.Length;
        var length = end - Prefix.Length;
        // Fixed-width components plus at most twenty customer characters bound regex work.
        if (length < 17 || length > 46) return false;
        var token = text.Substring(Prefix.Length, length);
        var match = ProductToken.Match(token);
        if (!match.Success) return false;
        version = new KingbaseProductVersion(text, token, match);
        return true;
    }

    static int Number(Match match, string group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
    public override string ToString() => Token;
}
