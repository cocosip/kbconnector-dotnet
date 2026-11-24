using System.Data.Common;

namespace KingbaseConnector.UnitTests;

public class ConnectionStringBuilderTests
{
    [Fact]
    public void DefaultsAreExplicitAndDoNotPopulateTheConnectionString()
    {
        var builder = new KingbaseConnectionStringBuilder();
        Assert.Equal("", builder.ConnectionString);
        Assert.Equal(54321, builder.Port);
        Assert.Equal(KingbaseWireProtocol.PostgreSql, builder.WireProtocol);
        Assert.Equal(KingbaseCompatibilityMode.Auto, builder.CompatibilityMode);
        Assert.Equal(KingbaseSslMode.VerifyFull, builder.SslMode);
        Assert.True(builder.Pooling);
        Assert.Equal(0, builder.MinPoolSize);
        Assert.Equal(100, builder.MaxPoolSize);
        Assert.Equal(15, builder.Timeout);
        Assert.Equal(30, builder.CommandTimeout);
        Assert.Equal(5, builder.CleanupTimeout);
        Assert.Equal(300, builder.ConnectionIdleLifetime);
        Assert.Equal(10, builder.ConnectionPruningInterval);
        Assert.Equal(0, builder.ConnectionLifetime);
        Assert.Equal("UTF8", builder.ClientEncoding);
        Assert.False(builder.ContainsKey("Port"));
        Assert.False(builder.ShouldSerialize("Port"));
    }

    [Fact]
    public void QuotedUnicodeAndPasswordRoundTripThroughCanonicalKeywords()
    {
        var builder = new KingbaseConnectionStringBuilder(
            "Server=localhost;Initial Catalog=数据库;User ID=app;Pwd=\"a;\"\"b=密码\";ApplicationName='my;app'");
        Assert.Equal("a;\"b=密码", builder.Password);
        Assert.Equal("数据库", builder.Database);
        Assert.Equal("my;app", builder.ApplicationName);
        Assert.True(builder.ContainsKey("Host"));
        Assert.True(builder.ContainsKey("Server"));
        Assert.DoesNotContain("Pwd=", builder.ConnectionString);
        var copy = new KingbaseConnectionStringBuilder(builder.ConnectionString);
        Assert.Equal(builder.ConnectionString, copy.ConnectionString);
        copy.Validate();
    }

    [Fact]
    public void DuplicateCanonicalAndAliasKeysUseLastAssignmentIncludingThroughTheBaseClass()
    {
        DbConnectionStringBuilder builder = new KingbaseConnectionStringBuilder();
        builder.ConnectionString = "Host=first;Server=second;Host=third;Database=db;Username=user";
        Assert.Equal("third", builder["Data Source"]);
        Assert.Equal(3, builder.Count);
        builder["SERVER"] = "fourth";
        Assert.Equal("fourth", builder["Host"]);
        Assert.True(builder.Remove("data source"));
        Assert.False(builder.ContainsKey("Server"));
        Assert.Equal("", builder["Host"]);
        builder.Clear();
        Assert.Empty(builder.ConnectionString);
    }

    [Theory]
    [InlineData("WireProtocol=MySql", false)]
    [InlineData("WireProtocol=MySql;Port=3307", true)]
    [InlineData("WireProtocol=MySql;Port=54321;CompatibilityMode=MySql", true)]
    [InlineData("WireProtocol=MySql;Port=3307;CompatibilityMode=Oracle", false)]
    [InlineData("WireProtocol=MySql;Port=3307;CompatibilityMode=PostgreSql", false)]
    [InlineData("WireProtocol=MySql;Port=3307;CompatibilityMode=SqlServer", false)]
    [InlineData("CompatibilityMode=MySql", true)]
    [InlineData("CompatibilityMode=Oracle", true)]
    public void ProtocolAndModeAreIndependentButUnsupportedCombinationsFail(string options, bool valid)
    {
        var builder = new KingbaseConnectionStringBuilder("Host=host;Database=db;Username=app;" + options);
        if (valid) builder.Validate();
        else Assert.Throws<ArgumentException>(builder.Validate);
    }

    [Theory]
    [InlineData("Database=db;Username=app")]
    [InlineData("Host=host;Username=app")]
    [InlineData("Host=host;Database=db")]
    [InlineData("Host=' ';Database=db;Username=app")]
    [InlineData("Host=host;Database=' ';Username=app")]
    [InlineData("Host=host;Database=db;Username=' '")]
    [InlineData("Host=host;Database=db;Username=app;Minimum Pool Size=101")]
    public void ValidationRejectsMissingIdentityAndConflictingPoolBounds(string connectionString)
    {
        var builder = new KingbaseConnectionStringBuilder(connectionString);
        Assert.Throws<ArgumentException>(builder.Validate);
    }

    [Theory]
    [InlineData("Port", "0")]
    [InlineData("Port", "65536")]
    [InlineData("Timeout", "-1")]
    [InlineData("Command Timeout", "-1")]
    [InlineData("Cleanup Timeout", "0")]
    [InlineData("Minimum Pool Size", "-1")]
    [InlineData("Maximum Pool Size", "0")]
    [InlineData("Connection Idle Lifetime", "-1")]
    [InlineData("Connection Pruning Interval", "0")]
    [InlineData("Connection Lifetime", "-1")]
    [InlineData("Pooling", "maybe-secret")]
    [InlineData("WireProtocol", "Auto")]
    [InlineData("WireProtocol", "1")]
    [InlineData("CompatibilityMode", "5")]
    [InlineData("CompatibilityMode", "ExpectedMode")]
    [InlineData("Ssl Mode", "Prefer")]
    [InlineData("Client Encoding", "LATIN1")]
    [InlineData("Timeout", "2147483648")]
    [InlineData("Timeout", "1.5")]
    [InlineData("Timeout", "not-a-number-secret")]
    [InlineData("ExpectedMode", "Oracle")]
    [InlineData("secret-key", "secret-value")]
    public void InvalidValuesNeverEchoInputOrExposeAnInnerException(string keyword, string value)
    {
        DbConnectionStringBuilder builder = new KingbaseConnectionStringBuilder();
        var exception = Assert.Throws<ArgumentException>(() => builder[keyword] = value);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(exception.InnerException);
        Assert.Empty(builder.ConnectionString);
    }

    [Fact]
    public void ObjectAssignmentsDoNotTruncateNumbersOrAcceptUndefinedEnumValues()
    {
        var builder = new KingbaseConnectionStringBuilder();
        Assert.Throws<ArgumentException>(() => builder["Port"] = 42.5);
        Assert.Throws<ArgumentException>(() => builder["Port"] = true);
        Assert.Throws<ArgumentException>(() => builder["WireProtocol"] = (KingbaseWireProtocol)42);
        Assert.Throws<ArgumentException>(() => builder["WireProtocol"] = KingbaseCompatibilityMode.Oracle);
        Assert.Throws<ArgumentException>(() => builder["Pooling"] = 1);
        builder["Port"] = 65535L;
        Assert.Equal(65535, builder.Port);
        builder.Timeout = 0;
        builder.CommandTimeout = 0;
        Assert.Equal(0, builder.Timeout);
    }

    [Fact]
    public void NullRemovesExplicitValuesAndRestoresDefaults()
    {
        DbConnectionStringBuilder builder = new KingbaseConnectionStringBuilder("Port=3307;Password=secret");
        builder["Port"] = null!;
        Assert.False(builder.ContainsKey("Port"));
        Assert.Equal(54321, builder["Port"]);
        Assert.True(builder.Remove("Pwd"));
        Assert.False(builder.Remove("Pwd"));
        Assert.False(builder.TryGetValue("unknown", out _));
        Assert.False(builder.ContainsKey("unknown"));
        Assert.False(builder.Remove("unknown"));
        Assert.False(builder.ShouldSerialize("unknown"));
    }

    [Fact]
    public void PasswordIsOnlyIncludedWhenExplicitlyRequestingTheConnectionString()
    {
        var builder = new KingbaseConnectionStringBuilder("Host=host;Password=\"super;secret\";Username=app");
        Assert.Contains("super;secret", builder.ConnectionString);
        Assert.DoesNotContain("super", builder.ToString());
        Assert.DoesNotContain("secret", builder.GetRedactedConnectionString());
        Assert.DoesNotContain("Password", builder.GetRedactedConnectionString());
        Assert.Equal("super;secret", builder.Password);
    }

    [Fact]
    public void RejectedMutationsRetainThePreviousValidValue()
    {
        var builder = new KingbaseConnectionStringBuilder("Port=1234;Password=secret");
        Assert.Throws<ArgumentException>(() => builder["Port"] = "bad-secret");
        Assert.Equal(1234, builder.Port);
        Assert.Equal("secret", builder.Password);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Database")]
    [InlineData("Username")]
    [InlineData("Password")]
    [InlineData("Application Name")]
    [InlineData("Search Path")]
    [InlineData("Timezone")]
    [InlineData("Root Certificate")]
    public void ZeroBytesAreRejectedBeforeProtocolEncoding(string keyword)
    {
        var builder = new KingbaseConnectionStringBuilder();
        Assert.Throws<ArgumentException>(() => builder[keyword] = "value\0secret");
    }

    [Fact]
    public void ValuesUseInvariantCultureAndCaseInsensitiveEnumNames()
    {
        var builder = new KingbaseConnectionStringBuilder(
            "host=h;database=d;username=u;wire protocol=postgresql;compatibility mode=oracle;sslmode=verifyfull;pooling=FALSE");
        builder.Validate();
        Assert.Equal(KingbaseCompatibilityMode.Oracle, builder.CompatibilityMode);
        Assert.Equal(KingbaseSslMode.VerifyFull, builder.SslMode);
        Assert.False(builder.Pooling);
    }
}
