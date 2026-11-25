using System.Data.Common;

namespace KingbaseConnector.UnitTests;

public sealed class CommandBuilderTests
{
    [Fact]
    public void FactoryCreatesACommandBuilderWithPgQuoting()
    {
        DbProviderFactory factory = KingbaseFactory.Instance;
        Assert.True(factory.CanCreateCommandBuilder);
        using var builder = Assert.IsAssignableFrom<DbCommandBuilder>(factory.CreateCommandBuilder());
        Assert.Equal("KingbaseCommandBuilder", builder.GetType().Name);
        Assert.Equal("U&\"a\\0022汉\"", builder.QuoteIdentifier("a\"汉"));
        Assert.Equal("a\"汉", builder.UnquoteIdentifier(builder.QuoteIdentifier("a\"汉")));
        Assert.Equal("a\"汉", builder.UnquoteIdentifier("\"a\"\"汉\""));
        Assert.Equal("plain", builder.UnquoteIdentifier("plain"));
        Assert.Throws<ArgumentNullException>(() => builder.QuoteIdentifier(null!));
        Assert.Throws<ArgumentException>(() => builder.QuoteIdentifier("bad\0name"));
        Assert.Throws<NotSupportedException>(() => builder.QuotePrefix = "[");
        Assert.Throws<NotSupportedException>(() => builder.QuoteSuffix = "]");
    }

    [Theory]
    [InlineData("a\\0022b", "U&\"a\\005c0022b\"")]
    [InlineData("\";--", "U&\"\\0022;--\"")]
    [InlineData("schema.table", "U&\"schema.table\"")]
    [InlineData("😀汉", "U&\"😀汉\"")]
    public void UnicodeQuotingKeepsEscapesAndPunctuationInsideOneIdentifier(string identifier, string expected)
    {
        using var builder = new KingbaseCommandBuilder(); Assert.Equal(expected, builder.QuoteIdentifier(identifier)); Assert.Equal(identifier, builder.UnquoteIdentifier(expected));
    }

    [Theory]
    [InlineData("U&\"\\0061\"", "a")]
    [InlineData("U&\"\\+01f600\"", "😀")]
    [InlineData("U&\"a\"\"b\"", "a\"b")]
    public void UnquoteUnderstandsPgUnicodeAndDoubledQuoteSyntax(string quoted, string expected)
    { using var builder = new KingbaseCommandBuilder(); Assert.Equal(expected, builder.UnquoteIdentifier(quoted)); }

    [Theory]
    [InlineData("U&\"")]
    [InlineData("U&\"\\0000\"")]
    [InlineData("U&\"\\zzzz\"")]
    [InlineData("U&\"\\D800\"")]
    [InlineData("U&\"\\+110000\"")]
    [InlineData("U&\"\\\"")]
    public void MalformedUnicodeIdentifiersRejectWithoutPayload(string quoted)
    { using var builder = new KingbaseCommandBuilder(); Assert.Throws<ArgumentException>(() => builder.UnquoteIdentifier(quoted)); }
}
