using System.Text;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public class PgPasswordAuthenticationTests
{
    [Theory]
    [InlineData("user", "password", "01020304", "md5a3576f1ae039b8996bc4fc2720f9c71a")]
    [InlineData("应用", "密码", "00FF8001", "md5298a7c2dd16e578035ae63f094b1a991")]
    [InlineData("user", "", "00000000", "md5b75ca9d91ebb57709ebf097edf967910")]
    public void Md5MatchesIndependentUtf8AndRawSaltVectors(string user, string password, string saltHex, string expected)
    {
        var auth = new PgPasswordAuthentication();
        var payload = new byte[] { 0, 0, 0, 5 }.Concat(Convert.FromHexString(saltHex)).ToArray();
        var response = auth.Process(new PgMessage((byte)'R', payload), user, password);
        var expectedFrame = new byte[] { (byte)'p', 0, 0, 0, 40 }
            .Concat(Encoding.ASCII.GetBytes(expected)).Append((byte)0).ToArray();
        Assert.Equal(expectedFrame, response);
        Assert.False(auth.IsComplete);
        Assert.Null(auth.Process(Request(0), user, password));
        Assert.True(auth.IsComplete);
    }

    [Fact]
    public void CleartextChallengeProducesExactlyOneUtf8PasswordFrame()
    {
        var auth = new PgPasswordAuthentication();
        var response = auth.Process(Request(3), "user", "密码");
        Assert.Equal(new byte[] { (byte)'p', 0, 0, 0, 11, 0xe5, 0xaf, 0x86, 0xe7, 0xa0, 0x81, 0 }, response);
        Assert.False(auth.IsComplete);
        Assert.Null(auth.Process(Request(0), "user", "密码"));
        Assert.True(auth.IsComplete);
    }

    [Fact]
    public void EmptyPasswordStillHasItsCStringTerminator()
    {
        Assert.Equal(new byte[] { (byte)'p', 0, 0, 0, 5, 0 }, PgFrontendMessages.Password(""));
    }

    [Fact]
    public void TrustAuthenticationCompletesWithoutSendingCredentials()
    {
        var auth = new PgPasswordAuthentication();
        Assert.False(auth.IsComplete);
        Assert.Null(auth.Process(Request(0), "user", "secret"));
        Assert.True(auth.IsComplete);
        Assert.Throws<IOException>(() => auth.Process(Request(3), "user", "secret"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 3)]
    [InlineData(3, 3)]
    [InlineData(3, 5)]
    [InlineData(5, 3)]
    [InlineData(5, 5)]
    public void RepeatedOrPostCompletionChallengesCannotSendAnotherPassword(int first, int second)
    {
        var auth = new PgPasswordAuthentication();
        auth.Process(Request(first), "user", "secret");
        Assert.Throws<IOException>(() => auth.Process(Request(second), "user", "secret"));
        Assert.Throws<IOException>(() => auth.Process(Request(0), "user", "secret"));
        Assert.False(auth.IsComplete);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(100)]
    public void UnsupportedAlgorithmsFailClosedWithoutFallback(int algorithm)
    {
        var auth = new PgPasswordAuthentication();
        var error = Assert.Throws<NotSupportedException>(() => auth.Process(Request(algorithm), "private-user", "private-password"));
        Assert.DoesNotContain("private-", error.ToString());
        Assert.Throws<IOException>(() => auth.Process(Request(3), "private-user", "private-password"));
        Assert.False(auth.IsComplete);
    }

    [Fact]
    public void MalformedRequestsPoisonTheExchangeWithoutLeakingPayloads()
    {
        var cases = new List<PgMessage>
        {
            new((byte)'E', new byte[] { 0, 0, 0, 3 }),
            new((byte)'R', Array.Empty<byte>()),
            new((byte)'R', new byte[] { 0 }),
            new((byte)'R', new byte[] { 0, 0, 0 }),
            new((byte)'R', new byte[] { 0, 0, 0, 0, 99 }),
            new((byte)'R', new byte[] { 0, 0, 0, 3, 99 }),
            new((byte)'R', new byte[] { 0, 0, 0, 5 }),
            new((byte)'R', new byte[] { 0, 0, 0, 5, 1, 2, 3 }),
            new((byte)'R', new byte[] { 0, 0, 0, 5, 1, 2, 3, 4, 99 }),
            default
        };
        foreach (var message in cases)
        {
            var auth = new PgPasswordAuthentication();
            var error = Assert.Throws<IOException>(() => auth.Process(message, "private-user", "private-password"));
            Assert.DoesNotContain("private-", error.ToString());
            Assert.Null(error.InnerException);
            Assert.Throws<IOException>(() => auth.Process(Request(0), "user", "password"));
            Assert.False(auth.IsComplete);
        }
    }

    [Fact]
    public void InvalidCredentialsNeverCreateAPartialResponseAndPoisonTheExchange()
    {
        // Construct invalid Unicode at runtime; xUnit attribute serialization replaces surrogates.
        foreach (var invalid in new[] { "prefix\0private-password", "private-password" + new string((char)0xd800, 1) })
        {
            var frontendError = Assert.Throws<ArgumentException>(() => PgFrontendMessages.Password(invalid));
            Assert.DoesNotContain("private-password", frontendError.ToString());
            foreach (var algorithm in new[] { 3, 5 })
            {
                var auth = new PgPasswordAuthentication();
                var error = Assert.Throws<ArgumentException>(() => auth.Process(Request(algorithm), "user", invalid));
                Assert.DoesNotContain("private-password", error.ToString());
                Assert.Throws<IOException>(() => auth.Process(Request(0), "user", "password"));
            }
            var md5 = new PgPasswordAuthentication();
            Assert.Throws<ArgumentException>(() => md5.Process(Request(5), invalid, "password"));
        }
    }

    [Fact]
    public void Md5SaltIsNotModifiedOrReplacedByItsTextRepresentation()
    {
        var auth = new PgPasswordAuthentication();
        var request = Request(5);
        var original = request.Payload.ToArray();
        auth.Process(request, "user", "password");
        Assert.Equal(original, request.Payload);
    }

    static PgMessage Request(int algorithm)
    {
        var payload = new byte[algorithm == 5 ? 8 : 4];
        PgBinary.WriteInt32(payload, 0, algorithm);
        if (algorithm == 5) new byte[] { 1, 2, 3, 4 }.CopyTo(payload, 4);
        return new PgMessage((byte)'R', payload);
    }
}
