using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgSaslPrepTests
{
    [Theory]
    [InlineData("I\u00ADX", "IX")]
    [InlineData("user", "user")]
    [InlineData("USER", "USER")]
    [InlineData("\u00AA", "a")]
    [InlineData("\u2168", "IX")]
    [InlineData("a\u00A0b\u1680c\u2000d\u3000e", "a b c d e")]
    [InlineData("\u00AD\u034F\u1806\u200B\uFEFF", "")]
    [InlineData("a\u030A", "\u00E5")]
    [InlineData("\u0627\u00AD\u0628", "\u0627\u0628")]
    [InlineData("a\u00AD\u0007", "a\u00AD\u0007")]
    [InlineData("a\u00AD\uE000", "a\u00AD\uE000")]
    [InlineData("a\u00AD\uFDD0", "a\u00AD\uFDD0")]
    [InlineData("a\u00AD\u202E", "a\u00AD\u202E")]
    [InlineData("a\u00AD\u0221", "a\u00AD\u0221")]
    [InlineData("a\u00AD\U0001F600", "a\u00AD\U0001F600")]
    [InlineData("\u0627A\u00AD\u0628", "\u0627A\u00AD\u0628")]
    [InlineData("1\u0627\u00AD\u0628", "1\u0627\u00AD\u0628")]
    [InlineData("\u0627\u00AD\u06281", "\u0627\u00AD\u06281")]
    [InlineData("\U0002F868", "\U0002136A")]
    [InlineData("\U0002F874", "\u5F33")]
    [InlineData("\U0002F91F", "\u43AB")]
    [InlineData("\U0002F95F", "\u7AAE")]
    [InlineData("\U0002F9BF", "\u4D57")]
    public void UsesRfc4013ExamplesAndFrozenUnicode32WithPgRawFallback(string password, string expected) =>
        Assert.Equal(expected, PgSaslPrep.Prepare(password));

    [Fact]
    public void ExistingLocalCredentialRulesRejectNulAndInvalidUtf16BeforePreparation()
    {
        foreach (var password in new[] { "private-password\0", "private-password" + new string((char)0xD800, 1) })
        {
            var error = Assert.Throws<ArgumentException>(() => PgSaslPrep.Prepare(password));
            Assert.DoesNotContain("private-password", error.ToString()); Assert.Null(error.InnerException);
        }
    }
}
