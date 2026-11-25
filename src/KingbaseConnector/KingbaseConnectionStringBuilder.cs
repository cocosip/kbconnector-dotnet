using System.Data.Common;
using System.Globalization;
#if !NETSTANDARD2_0
using System.Diagnostics.CodeAnalysis;
#endif

namespace KingbaseConnector;

/// <summary>
/// Builds canonical connection strings. Duplicate keys and aliases use the last assignment.
/// Call <see cref="Validate"/> to check required fields and cross-setting constraints.
/// </summary>
public sealed class KingbaseConnectionStringBuilder : DbConnectionStringBuilder
{
    static readonly Dictionary<string, object> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Host"] = "",
        ["Port"] = 54321,
        ["Database"] = "",
        ["Username"] = "",
        ["Password"] = "",
        ["WireProtocol"] = KingbaseWireProtocol.PostgreSql,
        ["CompatibilityMode"] = KingbaseCompatibilityMode.Auto,
        ["SSL Mode"] = KingbaseSslMode.VerifyFull,
        ["Root Certificate"] = "",
        ["Pooling"] = true,
        ["Minimum Pool Size"] = 0,
        ["Maximum Pool Size"] = 100,
        ["Timeout"] = 15,
        ["Command Timeout"] = 30,
        ["Max Auto Prepare"] = 0,
        ["Auto Prepare Min Usages"] = 5,
        ["Cleanup Timeout"] = 5,
        ["Connection Idle Lifetime"] = 300,
        ["Connection Pruning Interval"] = 10,
        ["Connection Lifetime"] = 0,
        ["Application Name"] = "",
        ["Search Path"] = "",
        ["Client Encoding"] = "UTF8",
        ["Timezone"] = ""
    };

    static readonly Dictionary<string, string> Keywords = CreateKeywords();

    public KingbaseConnectionStringBuilder() { }

    public KingbaseConnectionStringBuilder(string connectionString) => ConnectionString = connectionString;

#if !NETSTANDARD2_0
    [AllowNull]
#endif
    public override object this[string keyword]
    {
        get
        {
            var canonical = ResolveKeyword(keyword);
            return base.TryGetValue(canonical, out var value) ? ConvertValue(canonical, value) : Defaults[canonical];
        }
        set
        {
            var canonical = ResolveKeyword(keyword);
            if (value is null)
                base.Remove(canonical);
            else
                base[canonical] = ConvertValue(canonical, value);
        }
    }

    public string Host { get => (string)this["Host"]; set => this["Host"] = value; }
    public int Port { get => (int)this["Port"]; set => this["Port"] = value; }
    public string Database { get => (string)this["Database"]; set => this["Database"] = value; }
    public string Username { get => (string)this["Username"]; set => this["Username"] = value; }
    public string Password { get => (string)this["Password"]; set => this["Password"] = value; }
    public KingbaseWireProtocol WireProtocol { get => (KingbaseWireProtocol)this["WireProtocol"]; set => this["WireProtocol"] = value; }
    public KingbaseCompatibilityMode CompatibilityMode { get => (KingbaseCompatibilityMode)this["CompatibilityMode"]; set => this["CompatibilityMode"] = value; }
    public KingbaseSslMode SslMode { get => (KingbaseSslMode)this["SSL Mode"]; set => this["SSL Mode"] = value; }
    public string RootCertificate { get => (string)this["Root Certificate"]; set => this["Root Certificate"] = value; }
    public bool Pooling { get => (bool)this["Pooling"]; set => this["Pooling"] = value; }
    public int MinPoolSize { get => (int)this["Minimum Pool Size"]; set => this["Minimum Pool Size"] = value; }
    public int MaxPoolSize { get => (int)this["Maximum Pool Size"]; set => this["Maximum Pool Size"] = value; }
    public int Timeout { get => (int)this["Timeout"]; set => this["Timeout"] = value; }
    public int CommandTimeout { get => (int)this["Command Timeout"]; set => this["Command Timeout"] = value; }
    public int MaxAutoPrepare { get => (int)this["Max Auto Prepare"]; set => this["Max Auto Prepare"] = value; }
    public int AutoPrepareMinUsages { get => (int)this["Auto Prepare Min Usages"]; set => this["Auto Prepare Min Usages"] = value; }
    public int CleanupTimeout { get => (int)this["Cleanup Timeout"]; set => this["Cleanup Timeout"] = value; }
    public int ConnectionIdleLifetime { get => (int)this["Connection Idle Lifetime"]; set => this["Connection Idle Lifetime"] = value; }
    public int ConnectionPruningInterval { get => (int)this["Connection Pruning Interval"]; set => this["Connection Pruning Interval"] = value; }
    public int ConnectionLifetime { get => (int)this["Connection Lifetime"]; set => this["Connection Lifetime"] = value; }
    public string ApplicationName { get => (string)this["Application Name"]; set => this["Application Name"] = value; }
    public string SearchPath { get => (string)this["Search Path"]; set => this["Search Path"] = value; }
    public string ClientEncoding { get => (string)this["Client Encoding"]; set => this["Client Encoding"] = value; }
    public string Timezone { get => (string)this["Timezone"]; set => this["Timezone"] = value; }

    public override bool ContainsKey(string keyword) =>
        TryResolveKeyword(keyword, out var canonical) && base.ContainsKey(canonical);

    public override bool Remove(string keyword) =>
        TryResolveKeyword(keyword, out var canonical) && base.Remove(canonical);

    public override bool ShouldSerialize(string keyword) => ContainsKey(keyword);

#if !NETSTANDARD2_0
    public override bool TryGetValue(string keyword, [NotNullWhen(true)] out object? value)
#else
    public override bool TryGetValue(string keyword, out object value)
#endif
    {
        if (TryResolveKeyword(keyword, out var canonical))
        {
            value = this[canonical];
            return true;
        }
        value = null!;
        return false;
    }

    /// <summary>Checks a complete configuration without contacting a server.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Database) ||
            string.IsNullOrWhiteSpace(Username))
            throw new ArgumentException("Host, Database and Username must be explicitly specified.");
        if (MinPoolSize > MaxPoolSize)
            throw new ArgumentException("Minimum Pool Size must not exceed Maximum Pool Size.");
        if (RootCertificate.Length != 0 && SslMode != KingbaseSslMode.VerifyFull)
            throw new ArgumentException("Root Certificate requires SSL Mode=VerifyFull.");
        if (WireProtocol == KingbaseWireProtocol.MySql)
        {
            if (!ContainsKey("Port"))
                throw new ArgumentException("MySql wire protocol requires an explicit Port.");
            if (CompatibilityMode != KingbaseCompatibilityMode.Auto &&
                CompatibilityMode != KingbaseCompatibilityMode.MySql)
                throw new ArgumentException("MySql wire protocol supports only Auto or MySql compatibility mode.");
        }
    }

    /// <summary>Returns a copy of explicitly assigned settings with Password removed.</summary>
    /// <remarks>Other fields may contain application data; use an allowlist for diagnostic logging.</remarks>
    public string GetRedactedConnectionString()
    {
        var redacted = new DbConnectionStringBuilder();
        foreach (string key in Keys)
            if (key != "Password") redacted[key] = this[key];
        return redacted.ConnectionString;
    }

    public override string ToString() => GetRedactedConnectionString();

    internal string GetPoolKey()
    {
        var normalized = new DbConnectionStringBuilder();
        foreach (var key in Defaults.Keys.OrderBy(key => key, StringComparer.Ordinal))
            normalized[key] = key == "Host" ? Host.ToLowerInvariant() : this[key];
        return normalized.ConnectionString;
    }

    static bool TryResolveKeyword(string keyword, out string canonical)
    {
        if (keyword is null) throw new ArgumentNullException(nameof(keyword));
        return Keywords.TryGetValue(keyword.Trim(), out canonical!);
    }

    static string ResolveKeyword(string keyword) => TryResolveKeyword(keyword, out var canonical)
        ? canonical : throw new ArgumentException("Unsupported connection-string keyword.");

    static object ConvertValue(string keyword, object value)
    {
        var defaultValue = Defaults[keyword];
        if (defaultValue is string)
        {
            if (value is not string text || text.IndexOf('\0') >= 0)
                throw InvalidValue(keyword);
            if (keyword == "Client Encoding")
            {
                if (!text.Equals("UTF8", StringComparison.OrdinalIgnoreCase) &&
                    !text.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
                    throw InvalidValue(keyword);
                return "UTF8";
            }
            return text;
        }
        if (defaultValue is bool)
        {
            if (value is bool boolean) return boolean;
            if (value is string text && bool.TryParse(text, out var parsed)) return parsed;
            throw InvalidValue(keyword);
        }
        if (defaultValue is int)
        {
            if (value is not (string or byte or sbyte or short or ushort or int or uint or long or ulong) ||
                !int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var number))
                throw InvalidValue(keyword);
            var minimum = keyword is "Port" or "Maximum Pool Size" or "Cleanup Timeout" or "Connection Pruning Interval" or "Auto Prepare Min Usages" ? 1 : 0;
            if (number < minimum || (keyword == "Port" && number > 65535) || (keyword == "Max Auto Prepare" && number > 64))
                throw InvalidValue(keyword);
            return number;
        }
        if (value.GetType() == defaultValue.GetType() && Enum.IsDefined(defaultValue.GetType(), value))
            return value;
        if (value is string enumText)
            foreach (var name in Enum.GetNames(defaultValue.GetType()))
                if (name.Equals(enumText.Trim(), StringComparison.OrdinalIgnoreCase))
                    return Enum.Parse(defaultValue.GetType(), name);
        throw InvalidValue(keyword);
    }

    static ArgumentException InvalidValue(string keyword) =>
        new($"Invalid value for connection-string setting '{keyword}'.");

    static Dictionary<string, string> CreateKeywords()
    {
        var keywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in Defaults.Keys)
        {
            keywords[key] = key;
            keywords[key.Replace(" ", "")] = key;
        }
        keywords["Server"] = "Host";
        keywords["Data Source"] = "Host";
        keywords["DataSource"] = "Host";
        keywords["Initial Catalog"] = "Database";
        keywords["InitialCatalog"] = "Database";
        keywords["User ID"] = "Username";
        keywords["UserID"] = "Username";
        keywords["User Name"] = "Username";
        keywords["User"] = "Username";
        keywords["UID"] = "Username";
        keywords["Pwd"] = "Password";
        keywords["Wire Protocol"] = "WireProtocol";
        keywords["Compatibility Mode"] = "CompatibilityMode";
        keywords["Connection Timeout"] = "Timeout";
        keywords["ConnectionTimeout"] = "Timeout";
        keywords["Min Pool Size"] = "Minimum Pool Size";
        keywords["MinPoolSize"] = "Minimum Pool Size";
        keywords["Max Pool Size"] = "Maximum Pool Size";
        keywords["MaxPoolSize"] = "Maximum Pool Size";
        keywords["Time Zone"] = "Timezone";
        return keywords;
    }
}
