using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgScramTests
{
    internal const string Nonce = "rOprNGfwEbeRWgbNEkqO";
    internal const string ServerNonce = Nonce + "%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0";
    internal const string First = "r=" + ServerNonce + ",s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096";
    internal const string Signature = "6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=";
    internal static PgMessage Auth(int algorithm, string text = "") => new((byte)'R', Join(Int32(algorithm), Encoding.UTF8.GetBytes(text)));
    static PgPasswordAuthentication Start(string mechanisms = "SCRAM-SHA-256\0\0")
    {
        var auth = new PgPasswordAuthentication(Nonce); auth.Process(Auth(10, mechanisms), "user", "pencil"); return auth;
    }

    [Fact]
    public void Rfc7677ExchangeMatchesLiteralFramesAndRequiresFinalThenAuthenticationOk()
    {
        var auth = new PgPasswordAuthentication(Nonce);
        var initial = "n,,n=user,r=" + Nonce;
        Assert.Equal(Frame('p', Join(Encoding.ASCII.GetBytes("SCRAM-SHA-256\0"), Int32(initial.Length), Encoding.ASCII.GetBytes(initial))),
            auth.Process(Auth(10, "SCRAM-SHA-256-PLUS\0SCRAM-SHA-256\0\0"), "user", "pencil"));
        Assert.False(auth.IsComplete);
        Assert.Equal(Frame('p', Encoding.ASCII.GetBytes("c=biws,r=" + ServerNonce + ",p=dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=")),
            auth.Process(Auth(11, First), "user", "pencil"));
        Assert.False(auth.IsComplete); Assert.Null(auth.Process(Auth(12, "v=" + Signature), "user", "pencil"));
        Assert.False(auth.IsComplete); Assert.Null(auth.Process(Auth(0), "user", "pencil")); Assert.True(auth.IsComplete);
        Assert.Throws<IOException>(() => auth.Process(Auth(0), "user", "pencil"));
    }

    [Theory]
    [InlineData("SCRAM-SHA-256-PLUS\0\0")]
    [InlineData("SCRAM-SM3\0\0")]
    public void UnsupportedMechanismsNeverFallback(string mechanisms)
    {
        var auth = new PgPasswordAuthentication();
        Assert.Throws<NotSupportedException>(() => auth.Process(Auth(10, mechanisms), "private-user", "private-password"));
        Assert.Throws<IOException>(() => auth.Process(Auth(3), "user", "password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0")]
    [InlineData("SCRAM-SHA-256")]
    [InlineData("SCRAM-SHA-256\0")]
    [InlineData("SCRAM-SHA-256\0\0x")]
    [InlineData("SCRAM-SHA-256\0scram-sha-256\0\0")]
    [InlineData("SCRAM-SHA-256\0SCRAM-SHA-256\0\0")]
    [InlineData("SCRAM-SHA-256\0不合法\0\0")]
    public void MalformedMechanismListsFailEvenAfterASupportedName(string mechanisms)
    {
        var auth = new PgPasswordAuthentication();
        Assert.Throws<IOException>(() => auth.Process(Auth(10, mechanisms), "user", "pencil"));
        Assert.Throws<IOException>(() => auth.Process(Auth(0), "user", "pencil"));
    }

    public static IEnumerable<object[]> BadFirsts()
    {
        foreach (var first in new[] { "", First + ",r=x", First + ",x=a,x=b", First + ",m=required", "m=required," + First,
            First + ",x=", First + ",xx=value", First + ",1=value", First + ",x=\0private-server", First.Replace("r=", "s="),
            First.Replace("i=4096", "i=0"), First.Replace("i=4096", "i=-1"), First.Replace("i=4096", "i=+4096"),
            First.Replace("i=4096", "i=04096"), First.Replace("i=4096", "i= 4096"), First.Replace("i=4096", "i=1000001"),
            First.Replace("i=4096", "i=999999999999999999"), First.Replace("i=4096", "i=٤٠٩٦"),
            First.Replace(ServerNonce, Nonce), First.Replace(ServerNonce, "wrong" + ServerNonce), First.Replace(ServerNonce, ServerNonce + " "),
            First.Replace("W22ZaJ0SNY7soEsUEjb6gQ==", ""), First.Replace("W22ZaJ0SNY7soEsUEjb6gQ==", "AA==\n"),
            First.Replace("W22ZaJ0SNY7soEsUEjb6gQ==", "AB=="), First.Replace("W22ZaJ0SNY7soEsUEjb6gQ==", "!") }) yield return [first];
        yield return ["r=" + ServerNonce + ",i=4096,s=AA=="];
        yield return ["r=" + ServerNonce + ",s=" + Convert.ToBase64String(new byte[1025]) + ",i=4096"];
        yield return [First + ",x=" + new string('a', 16 * 1024)];
    }

    [Theory]
    [MemberData(nameof(BadFirsts))]
    public void MalformedChallengesPoisonStateWithoutLeakingServerOrCredentialText(string first)
    {
        var auth = Start();
        var error = Assert.Throws<IOException>(() => auth.Process(Auth(11, first), "private-user", "private-password"));
        Assert.DoesNotContain("private-", error.ToString()); Assert.Null(error.InnerException);
        Assert.Throws<IOException>(() => auth.Process(Auth(0), "user", "pencil")); Assert.False(auth.IsComplete);
    }

    [Theory]
    [InlineData("e=private-server-error")]
    [InlineData("v=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("v=AA==")]
    [InlineData("v=!")]
    [InlineData("v=")]
    [InlineData("v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=,v=x")]
    [InlineData("v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=,e=error")]
    [InlineData("v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=,m=required")]
    public void ServerFinalErrorsAndInvalidSignaturesNeverAuthenticate(string final)
    {
        var auth = Start(); auth.Process(Auth(11, First), "user", "pencil");
        var error = Assert.Throws<IOException>(() => auth.Process(Auth(12, final), "private-user", "private-password"));
        Assert.DoesNotContain("private-", error.ToString()); Assert.Null(error.InnerException); Assert.False(auth.IsComplete);
        Assert.Throws<IOException>(() => auth.Process(Auth(0), "user", "pencil"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 12)]
    [InlineData(0, 10)]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(1, 11)]
    [InlineData(1, 5)]
    [InlineData(2, 12)]
    public void PhaseSkippingOrAlgorithmSwitchingCannotComplete(int stage, int algorithm)
    {
        var auth = Start();
        if (stage >= 1) auth.Process(Auth(11, First), "user", "pencil");
        if (stage >= 2) auth.Process(Auth(12, "v=" + Signature), "user", "pencil");
        Assert.Throws<IOException>(() => auth.Process(Auth(algorithm), "user", "pencil")); Assert.False(auth.IsComplete);
    }
}
