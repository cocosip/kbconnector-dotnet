using KingbaseConnector.Protocol;
using Xunit.Sdk;

namespace KingbaseConnector.IntegrationTests;

internal static class LiveProfile
{
    internal static Task RunPg(Func<KingbaseConnectionStringBuilder, CancellationToken, Task> action) =>
        Run(KingbaseCompatibilityMode.PostgreSql, action);

    internal static async Task Run(KingbaseCompatibilityMode mode,
        Func<KingbaseConnectionStringBuilder, CancellationToken, Task> action)
    {
        try
        {
            var variable = "KINGBASE_TEST_" + mode.ToString().ToUpperInvariant();
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Required LIVE profile is not configured.");
            var settings = new KingbaseConnectionStringBuilder(value)
            {
                CompatibilityMode = KingbaseCompatibilityMode.Auto,
                Pooling = false,
                Timeout = 10,
                CommandTimeout = 10,
                CleanupTimeout = 5
            };
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await action(settings, budget.Token);
        }
        catch (Exception error)
        {
            // Never attach the original exception: raw server text/assertion values may identify the deployment.
            var state = error switch
            {
                KingbaseException server => server.SqlState,
                PgQueryException query => query.ServerError.SqlState,
                PgStartupException startup => startup.ServerError.SqlState,
                _ => "unavailable"
            };
            throw new XunitException($"LIVE {mode}: {error.GetType().Name}; SQLSTATE {state}. Private details are suppressed.");
        }
    }

    internal static string? SkipReason(bool writes) =>
        Environment.GetEnvironmentVariable("KINGBASE_TEST_LIVE") != "1" ? "LIVE not enabled." :
        writes && Environment.GetEnvironmentVariable("KINGBASE_TEST_WRITES") != "1" ? "LIVE temporary-table writes not enabled." : null;
}

internal sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(bool writes = false) => Skip = LiveProfile.SkipReason(writes);
}

internal sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute(bool writes = false) => Skip = LiveProfile.SkipReason(writes);
}
