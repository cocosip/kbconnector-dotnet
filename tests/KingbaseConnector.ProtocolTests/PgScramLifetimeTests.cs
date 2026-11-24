using System.Reflection;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgScramTests;
using static KingbaseConnector.ProtocolTests.PgScramStartupTests;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgScramLifetimeTests
{
    [Fact]
    public void InitialPayloadBudgetIncludesMechanismAndEmbeddedLength()
    {
        using var auth = new PgPasswordAuthentication(Nonce);
        Assert.Throws<IOException>(() => auth.Process(Auth(10, "SCRAM-SHA-256\0\0"), new string('x', 16350), "pencil"));
        Assert.Throws<IOException>(() => auth.Process(Auth(0), "user", "pencil"));
    }

    [Theory]
    [InlineData(1, "", "", "AA==")]
    [InlineData(4096, "\u00AD", "", "AA==")]
    [InlineData(4096, "pencil", "pencil", "W22ZaJ0SNY7soEsUEjb6gQ==")]
    [InlineData(1000000, "pencil", "pencil", "AA==")]
    public void DerivationMatchesIndependentBclForEmptyPasswordAndIterationBounds(int iterations, string password, string prepared, string salt)
    {
        using var auth = new PgPasswordAuthentication(Nonce);
        var first = "n,,n=user,r=" + Nonce;
        var server = Server(first, prepared, iterations, extensions: true, salt);
        auth.Process(Auth(10, "SCRAM-SHA-256\0\0"), "user", password);
        Assert.Equal(Frame('p', Encoding.UTF8.GetBytes(server.Final)), auth.Process(Auth(11, server.First), "user", password));
        auth.Process(Auth(12, "v=" + server.Signature + ",x=扩展"), "user", password);
        auth.Process(Auth(0), "user", password); Assert.True(auth.IsComplete);
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("bad-signature")]
    [InlineData("bad-message")]
    [InlineData("dispose")]
    [InlineData("cancel")]
    public void ExpectedSignatureIsClearedOnSuccessFailureDisposalAndCancellation(string end)
    {
        using var auth = new PgPasswordAuthentication(Nonce);
        auth.Process(Auth(10, "SCRAM-SHA-256\0\0"), "user", "pencil");
        auth.Process(Auth(11, First), "user", "pencil");
        var scram = typeof(PgPasswordAuthentication).GetField("_scram", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(auth)!;
        var signature = (byte[])typeof(PgScramSha256).GetField("_serverSignature", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scram)!;
        Assert.Contains(signature, b => b != 0);
        if (end == "verified") auth.Process(Auth(12, "v=" + Signature), "user", "pencil");
        else if (end == "bad-signature") Assert.Throws<IOException>(() => auth.Process(Auth(12, "v=" + Convert.ToBase64String(new byte[32])), "user", "pencil"));
        else if (end == "bad-message") Assert.Throws<IOException>(() => auth.Process(Auth(0), "user", "pencil"));
        else if (end == "dispose") auth.Dispose();
        else
        {
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => auth.Process(Auth(12, "v=" + Signature), "user", "pencil", canceled.Token));
        }
        Assert.All(signature, b => Assert.Equal(0, b));
        if (end != "verified") Assert.Throws<IOException>(() => auth.Process(Auth(0), "user", "pencil"));
    }

    [Fact]
    public async Task CancellationInterruptsTheBoundedCpuDerivationWithTheOriginalToken()
    {
        using var auth = new PgPasswordAuthentication(Nonce); using var caller = new CancellationTokenSource();
        auth.Process(Auth(10, "SCRAM-SHA-256\0\0"), "user", "pencil");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deriving = Task.Run(() => { entered.SetResult(); return auth.Process(Auth(11, First.Replace("i=4096", "i=1000000")), "user", "pencil", caller.Token); });
        await entered.Task; await Task.Delay(10); caller.Cancel();
        Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deriving.WaitAsync(TimeSpan.FromSeconds(3)))).CancellationToken);
        Assert.Throws<IOException>(() => auth.Process(Auth(0), "user", "pencil"));
    }

    [Fact]
    public void EveryNewHandlerUsesItsOwnCryptographicNonce()
    {
        var nonces = new HashSet<string>();
        for (var index = 0; index < 32; index++)
        {
            using var auth = new PgPasswordAuthentication();
            var first = Initial(auth.Process(Auth(10, "SCRAM-SHA-256\0\0"), "user", "pencil")![5..]);
            var nonce = first[(first.LastIndexOf(",r=", StringComparison.Ordinal) + 3)..];
            Assert.Equal(24, Convert.FromBase64String(nonce).Length); Assert.True(nonces.Add(nonce));
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void InvalidUtf8AndOversizedSaslPayloadsRejectWithoutEchoingBytes(int stage)
    {
        foreach (var bytes in new[] { new byte[] { 0xFF }, new byte[16385] })
        {
            using var auth = new PgPasswordAuthentication(Nonce);
            if (stage >= 11) auth.Process(Auth(10, "SCRAM-SHA-256\0\0"), "user", "pencil");
            if (stage >= 12) auth.Process(Auth(11, First), "user", "pencil");
            var error = Assert.Throws<IOException>(() => auth.Process(new PgMessage((byte)'R', Join(Int32(stage), bytes)), "private-user", "private-password"));
            Assert.DoesNotContain("private-", error.ToString()); Assert.Null(error.InnerException);
            Assert.Throws<IOException>(() => auth.Process(Auth(0), "user", "pencil"));
        }
    }
}
