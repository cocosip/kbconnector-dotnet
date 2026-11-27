using System.Data;

namespace KingbaseConnector.UnitTests;

// The catalog reload needs a live session, so its offline contract is the state guard itself.
public sealed class ReloadTypesTests
{
    [Fact]
    public async Task ReloadTypesRequiresAnOpenConnection()
    {
        using var connection = new KingbaseConnection("Host=localhost;Database=app;Username=user;Password=private");
        Assert.Throws<InvalidOperationException>(() => connection.ReloadTypes());
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ReloadTypesAsync());
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task ReloadTypesHonoursCancellationBeforeTouchingTheSession()
    {
        using var connection = new KingbaseConnection("Host=localhost;Database=app;Username=user;Password=private");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.ReloadTypesAsync(cancelled.Token));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
