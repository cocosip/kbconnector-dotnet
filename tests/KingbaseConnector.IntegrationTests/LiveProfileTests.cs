namespace KingbaseConnector.IntegrationTests;

public class LiveProfileTests
{
    [LiveFact]
    public void EnabledLiveTestingRequiresARegisteredProfile()
    {
        Assert.Fail("No live profile or server tests are registered. Register a profile in " +
            "docs/kbconnector-development-progress.md and implement its acceptance tests first.");
    }
}

internal sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("KINGBASE_TEST_LIVE"), "1",
                StringComparison.Ordinal))
            Skip = "LIVE not run: set KINGBASE_TEST_LIVE=1 only after registering a live profile.";
    }
}
