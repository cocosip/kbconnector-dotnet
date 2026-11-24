using System.Data;
using Microsoft.Extensions.DependencyInjection;

namespace KingbaseConnector.UnitTests;

public sealed class PasswordProviderTests
{
    [Fact]
    public void ContextHasOnlyImmutableMetadataAndNoPublicConstructorOrPassword()
    {
        Assert.Empty(typeof(KingbasePasswordContext).GetConstructors());
        var properties = typeof(KingbasePasswordContext).GetProperties();
        Assert.Equal(new[] { "Database", "Host", "Port", "Username", "WireProtocol" }, properties.Select(p => p.Name).Order().ToArray());
        Assert.All(properties, property => Assert.Null(property.SetMethod));
    }
    const string Configuration = "Host=localhost;Database=app;Username=user";
    [Theory]
    [InlineData("Password=private-value")]
    [InlineData("Pwd=\"\"")]
    public void BuildRejectsExplicitPasswordTogetherWithAProviderIncludingEmpty(string password)
    {
        var builder = new KingbaseDataSourceBuilder(Configuration + ";" + password) { PasswordProvider = (_, _) => Task.FromResult("secret") };
        var error = Assert.Throws<ArgumentException>(() => builder.Build());
        Assert.DoesNotContain("private-value", error.ToString()); Assert.Null(error.InnerException);
    }

    [Fact]
    public void BuildCreateAndDiResolutionNeverInvokeTheProvider()
    {
        var calls = 0;
        var builder = new KingbaseDataSourceBuilder(Configuration) { PasswordProvider = (_, _) => { calls++; throw new Exception("must remain lazy"); } };
        using var source = builder.Build(); using var connection = source.CreateConnection();
        var services = new ServiceCollection(); services.AddKingbaseDataSource(Configuration, (_, b) => b.PasswordProvider = builder.PasswordProvider);
        using var container = services.BuildServiceProvider(); using var owned = container.GetRequiredService<KingbaseDataSource>().CreateConnection();
        Assert.Equal(0, calls); Assert.Equal(ConnectionState.Closed, connection.State); Assert.Equal(ConnectionState.Closed, owned.State);
    }

    [Fact]
    public void ExplicitTypedEmptyPasswordRejectsBeforeSerializationAndRemoveClearsTheConflict()
    {
        var builder = new KingbaseDataSourceBuilder(Configuration) { PasswordProvider = (_, _) => Task.FromResult("secret") };
        builder.ConnectionStringBuilder.Password = "";
        Assert.Throws<ArgumentException>(() => builder.Build());
        builder.ConnectionStringBuilder.Remove("Password"); using var source = builder.Build();
        using var bclEmpty = new KingbaseDataSourceBuilder(Configuration + ";Pwd=") { PasswordProvider = builder.PasswordProvider }.Build();
    }
}
