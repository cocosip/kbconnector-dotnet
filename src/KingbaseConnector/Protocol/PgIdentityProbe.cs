namespace KingbaseConnector.Protocol;

internal enum PgModeSource { ParameterStatus, Show }

internal sealed class PgIdentityResult(KingbaseProductVersion version, string? startupVersion,
    KingbaseCompatibilityMode configuredMode, KingbaseCompatibilityMode effectiveMode,
    KingbaseCompatibilityMode? reportedMode, PgModeSource? reportedModeSource)
{
    internal KingbaseProductVersion Version { get; } = version;
    internal string? StartupVersion { get; } = startupVersion;
    internal KingbaseCompatibilityMode ConfiguredMode { get; } = configuredMode;
    internal KingbaseCompatibilityMode EffectiveMode { get; } = effectiveMode;
    internal KingbaseCompatibilityMode? ReportedMode { get; } = reportedMode;
    internal PgModeSource? ReportedModeSource { get; } = reportedModeSource;
}

internal static class PgIdentityProbe
{
    // Initialization owns this session exclusively until catalog loading and public Open finish.
    internal static async Task<PgIdentityResult> RunAsync(PgQuerySession session, KingbaseCompatibilityMode configuredMode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (!Enum.IsDefined(typeof(KingbaseCompatibilityMode), configuredMode))
            throw new ArgumentOutOfRangeException(nameof(configuredMode));
        if (session.TransactionStatus != PgTransactionStatus.Idle)
            throw new InvalidOperationException("PG identity initialization requires an idle session.");
        session.Parameters.TryGetValue("server_version", out var startupVersion);
        try
        {
            // A configured search_path may put borrower-writable schemas before the system catalog.
            var text = await ReadScalarAsync(session, "SELECT pg_catalog.version();", cancellationToken).ConfigureAwait(false);
            if (!KingbaseProductVersion.TryParse(text, out var version) || version is null || version.Major != 9)
                throw new NotSupportedException("The endpoint did not report a supported Kingbase V9 product identity.");

            KingbaseCompatibilityMode? reported = null;
            PgModeSource? source = null;
            if (session.Parameters.TryGetValue("database_mode", out var mode))
            {
                reported = ParseMode(mode);
                source = PgModeSource.ParameterStatus;
            }
            else if (configuredMode == KingbaseCompatibilityMode.Auto)
            {
                mode = await ReadScalarAsync(session, "SHOW database_mode", cancellationToken).ConfigureAwait(false);
                reported = ParseMode(mode);
                source = PgModeSource.Show;
                if (session.Parameters.TryGetValue("database_mode", out var current) && ParseMode(current) != reported)
                    throw UnsupportedMode();
            }
            if (configuredMode != KingbaseCompatibilityMode.Auto && reported.HasValue && reported.Value != configuredMode)
                throw UnsupportedMode();
            cancellationToken.ThrowIfCancellationRequested();
            return new PgIdentityResult(version, startupVersion, configuredMode,
                configuredMode == KingbaseCompatibilityMode.Auto ? reported!.Value : configuredMode, reported, source);
        }
        catch
        {
            try { session.Dispose(); } catch { }
            throw;
        }
    }

    static async Task<string> ReadScalarAsync(PgQuerySession session, string sql, CancellationToken cancellationToken)
    {
        var results = await session.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        if (session.TransactionStatus != PgTransactionStatus.Idle || results.Count != 1 || results[0].Columns.Count != 1 ||
            results[0].Rows.Count != 1 || results[0].Rows[0].Count != 1 || results[0].Rows[0][0] is not string value)
            throw new IOException("Invalid PG identity-probe result.");
        return value;
    }

    internal static KingbaseCompatibilityMode ParseMode(string mode)
    {
        if (mode.Equals("pg", StringComparison.OrdinalIgnoreCase)) return KingbaseCompatibilityMode.PostgreSql;
        if (mode.Equals("oracle", StringComparison.OrdinalIgnoreCase)) return KingbaseCompatibilityMode.Oracle;
        if (mode.Equals("mysql", StringComparison.OrdinalIgnoreCase)) return KingbaseCompatibilityMode.MySql;
        if (mode.Equals("sqlserver", StringComparison.OrdinalIgnoreCase)) return KingbaseCompatibilityMode.SqlServer;
        throw UnsupportedMode();
    }

    internal static bool MatchesMode(string value, KingbaseCompatibilityMode expected)
    {
        try { return ParseMode(value) == expected; }
        catch (NotSupportedException) { return false; }
    }

    static NotSupportedException UnsupportedMode() => new("The reported database mode is unsupported or conflicts with configuration.");
}
