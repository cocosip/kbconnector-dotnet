namespace KingbaseConnector.Protocol;

/// <summary>Canonical state captured before the first borrower; no credentials are retained.</summary>
internal sealed class PgSessionResetState
{
    static readonly string[] Settings = ["client_encoding", "standard_conforming_strings", "application_name", "search_path", "TimeZone",
        "default_transaction_isolation", "default_transaction_read_only", "default_transaction_deferrable", "DateStyle", "IntervalStyle"];
    static readonly string StateSql = "SELECT pg_catalog.current_database(),SESSION_USER,CURRENT_USER,pg_catalog.current_setting('database_mode')," +
        string.Join(",", Settings.Select(name => "pg_catalog.current_setting('" + name + "')")) +
        ",pg_catalog.current_setting('transaction_isolation'),pg_catalog.current_setting('transaction_read_only')";
    static readonly string RestoreSql = "SELECT " + string.Join(",", Settings.Select((name, i) =>
        "pg_catalog.set_config('" + name + "',$" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",false)"));
    readonly string[] _values;
    readonly Dictionary<string, string> _reported;

    PgSessionResetState(string[] values, PgQuerySession session)
    {
        _values = values;
        _reported = session.Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    internal static async Task<PgSessionResetState> CaptureAsync(PgQuerySession session, PgIdentityResult identity, CancellationToken token)
    {
        if (identity.EffectiveMode != KingbaseCompatibilityMode.PostgreSql)
            throw new NotSupportedException("PG session reset requires PostgreSql compatibility mode.");
        var values = await ReadStateAsync(session, token).ConfigureAwait(false);
        var state = new PgSessionResetState(values, session);
        state.ValidateReported(session);
        return state;
    }

    // The pool must hold exclusive ownership and revoke every logical child before entering.
    internal async Task<PgTypeCatalog> RestoreAsync(PgQuerySession session, PgIdentityResult identity, CancellationToken token)
    {
        if (session.Parameters.TryGetValue("database_mode", out var mode) && !mode.Equals("pg", StringComparison.OrdinalIgnoreCase))
            throw InvalidState();
        if (session.TransactionStatus != PgTransactionStatus.Idle)
            ValidateControl(await session.ExecuteAsync("ROLLBACK", token).ConfigureAwait(false), session, "ROLLBACK");
        ValidateControl(await session.ExecuteAsync("DISCARD ALL", token).ConfigureAwait(false), session, "DISCARD ALL");
        session.InvalidatePreparedStatements();
        var catalog = await PgTypeCatalog.LoadAsync(session, identity, token).ConfigureAwait(false);
        var textOid = catalog.ByName["pg_catalog.text"].Oid;
        var parameters = _values.Skip(4).Take(Settings.Length).Select(value => new PgQueryParameter(textOid, value)).ToArray();
        var restored = await session.ExecuteExtendedAsync(RestoreSql, catalog, parameters, token).ConfigureAwait(false);
        if (!ReadVector(restored, session, Settings.Length).SequenceEqual(_values.Skip(4).Take(Settings.Length), StringComparer.Ordinal))
            throw InvalidState();
        if (!(await ReadStateAsync(session, token).ConfigureAwait(false)).SequenceEqual(_values, StringComparer.Ordinal)) throw InvalidState();
        ValidateReported(session);
        var current = await PgIdentityProbe.RunAsync(session, identity.ConfiguredMode, token).ConfigureAwait(false);
        if (current.EffectiveMode != identity.EffectiveMode || current.Version.RawText != identity.Version.RawText || current.StartupVersion != identity.StartupVersion)
            throw InvalidState();
        ValidateReported(session);
        return catalog;
    }

    static async Task<string[]> ReadStateAsync(PgQuerySession session, CancellationToken token)
    {
        var values = ReadVector(await session.ExecuteAsync(StateSql, token).ConfigureAwait(false), session, 16);
        if (values.Where((value, i) => i is not 6 and not 7).Any(value => value.Length == 0) ||
            !values[3].Equals("pg", StringComparison.OrdinalIgnoreCase) || values[4] != "UTF8" ||
            !new[] { 5, 10, 11, 15 }.All(i => values[i] is "on" or "off") || values[9] != values[14] || values[10] != values[15] ||
            values[9] is not ("read committed" or "read uncommitted" or "repeatable read" or "serializable")) throw InvalidState();
        return values;
    }

    static string[] ReadVector(IReadOnlyList<PgTextResult> results, PgQuerySession session, int width)
    {
        if (session.TransactionStatus != PgTransactionStatus.Idle || results.Count != 1 || results[0].CommandTag != "SELECT 1" ||
            results[0].Columns.Count != width || results[0].Rows.Count != 1 || results[0].Rows[0].Count != width ||
            results[0].Rows[0].Any(value => value is null || value.IndexOf('\0') >= 0)) throw InvalidState();
        return results[0].Rows[0].Select(value => value!).ToArray();
    }

    static void ValidateControl(IReadOnlyList<PgTextResult> results, PgQuerySession session, string tag)
    {
        if (results.Count != 1 || results[0].CommandTag != tag || results[0].Columns.Count != 0 || results[0].Rows.Count != 0 ||
            session.TransactionStatus != PgTransactionStatus.Idle) throw InvalidState();
    }

    void ValidateReported(PgQuerySession session)
    {
        foreach (var parameter in session.Parameters)
        {
            var index = Array.IndexOf(Settings, parameter.Key);
            if (index >= 0) { if (parameter.Value != _values[index + 4]) throw InvalidState(); }
            else if (parameter.Key == "database_mode")
            { if (!parameter.Value.Equals("pg", StringComparison.OrdinalIgnoreCase)) throw InvalidState(); }
            else if (parameter.Key == "session_authorization" && !_reported.ContainsKey(parameter.Key))
            { if (parameter.Value != _values[1]) throw InvalidState(); }
            else if (!_reported.TryGetValue(parameter.Key, out var initial) || parameter.Value != initial) throw InvalidState();
        }
    }

    static IOException InvalidState() => new("Invalid or unrestored Kingbase PG session state.");
}
