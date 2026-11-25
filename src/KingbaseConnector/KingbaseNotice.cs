using System.Collections.ObjectModel;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>A server notice. Raw fields may contain application data; avoid logging them.</summary>
public sealed class KingbaseNotice
{
    internal KingbaseNotice(PgServerError notice)
    {
        var fields = new Dictionary<char, string>();
        foreach (var field in notice.Fields) fields.Add((char)field.Key, field.Value);
        Fields = new ReadOnlyDictionary<char, string>(fields);
    }

    /// <summary>Raw protocol fields, including unknown server extensions.</summary>
    public IReadOnlyDictionary<char, string> Fields { get; }
    public string SqlState => Fields['C'];
    public string Severity => Get('S') ?? Fields['V'];
    public string? InvariantSeverity => Get('V');
    public string MessageText => Fields['M'];
    public string? Detail => Get('D');
    public string? Hint => Get('H');
    string? Get(char field) => Fields.TryGetValue(field, out var value) ? value : null;
    public override string ToString() => $"Kingbase server notice (SQLSTATE {SqlState}).";
}

public sealed class KingbaseNoticeEventArgs : EventArgs
{
    internal KingbaseNoticeEventArgs(KingbaseNotice notice) => Notice = notice;
    public KingbaseNotice Notice { get; }
}
