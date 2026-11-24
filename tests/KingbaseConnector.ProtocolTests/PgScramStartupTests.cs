using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.PgScramTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgScramStartupTests
{
    internal static async Task<byte[]> ReadPassword(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[5]; await stream.ReadExactlyAsync(header, token);
        Assert.Equal((byte)'p', header[0]); var length = PgBinary.ReadInt32(header, 1);
        Assert.InRange(length, 4, 16388); var body = new byte[length - 4]; await stream.ReadExactlyAsync(body, token); return body;
    }
    internal static string Initial(byte[] body, string username = "user")
    {
        Assert.Equal("SCRAM-SHA-256", Encoding.ASCII.GetString(body[..13])); Assert.Equal(0, body[13]);
        Assert.Equal(body.Length - 18, PgBinary.ReadInt32(body, 14));
        var text = Encoding.UTF8.GetString(body[18..]);
        var prefix = "n,,n=" + username.Replace("=", "=3D").Replace(",", "=2C") + ",r=";
        Assert.StartsWith(prefix, text); Assert.Equal(32, text[prefix.Length..].Length);
        return text;
    }
    internal static byte[] Authentication(int algorithm, string text) => Frame('R', Join(Int32(algorithm), Encoding.UTF8.GetBytes(text)));
    internal static (string First, string Final, string Signature) Server(string initial, string password, int iterations = 4096, bool extensions = false,
        string salt = "W22ZaJ0SNY7soEsUEjb6gQ==")
    {
        var nonce = initial[(initial.LastIndexOf(",r=", StringComparison.Ordinal) + 3)..] + "server-suffix";
        var first = "r=" + nonce + ",s=" + salt + ",i=" + iterations + (extensions ? ",x=附加=" : "");
        var final = "c=biws,r=" + nonce;
        var transcript = Encoding.UTF8.GetBytes(initial[3..] + "," + first + "," + final);
        // Independent BCL PBKDF2 is the peer oracle; product uses a cancellable HMAC loop.
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(salt), iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(salted, "Client Key"u8); var storedKey = SHA256.HashData(clientKey);
        var clientSignature = HMACSHA256.HashData(storedKey, transcript);
        var proof = clientKey.Zip(clientSignature, (a, b) => (byte)(a ^ b)).ToArray();
        var signature = HMACSHA256.HashData(HMACSHA256.HashData(salted, "Server Key"u8), transcript);
        return (first, final + ",p=" + Convert.ToBase64String(proof), Convert.ToBase64String(signature));
    }
    internal static async Task<string> Authenticate(NetworkStream stream, CancellationToken token, string password = "pencil", string username = "user", string failure = "")
    {
        await ReadStartupAsync(stream, token);
        await stream.WriteAsync(Authentication(10, "SCRAM-SHA-256-PLUS\0SCRAM-SHA-256\0\0"), token);
        var initial = Initial(await ReadPassword(stream, token), username);
        var server = Server(initial, password, extensions: true);
        await stream.WriteAsync(Authentication(11, server.First), token);
        Assert.Equal(server.Final, Encoding.UTF8.GetString(await ReadPassword(stream, token)));
        if (failure == "eof") return initial;
        await stream.WriteAsync(Authentication(failure == "premature-ok" ? 0 : 12,
            failure == "premature-ok" ? "" : failure == "signature" ? "v=" + Convert.ToBase64String(new byte[32]) : "v=" + server.Signature + ",x=ignored"), token);
        return initial;
    }

    [Theory]
    [InlineData(false, "pencil", "pencil", "user")]
    [InlineData(true, "pencil", "pencil", "user")]
    [InlineData(false, "I\u00ADX", "IX", "user")]
    [InlineData(false, "a\u00AD\u0007", "a\u00AD\u0007", "user")]
    [InlineData(false, "密码", "密码", "应用,=用户")]
    public async Task PublicOpenCompletesScramBeforeIdentityCatalogAndExecution(bool sync, string password, string prepared, string username)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Settings(listener); settings.Password = password; settings.Username = username;
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = socket.GetStream(); await Authenticate(stream, guard.Token, prepared, username);
            await Initialize(stream, guard.Token, readStartup: false);
            await PoolExecutionTests.Query(stream, "SELECT 7", uint.MaxValue, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var connection = new KingbaseConnection(settings.ConnectionString);
        if (sync) connection.Open(); else await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 7", connection); Assert.Equal(7, await command.ExecuteScalarAsync(guard.Token));
        await connection.CloseAsync(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("premature-ok")]
    [InlineData("eof")]
    public async Task FailedServerProofNeverPublishesOpenOrStartsIdentityDiscovery(string failure)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = socket.GetStream(); await Authenticate(stream, guard.Token, password: "private-password", failure: failure);
            if (failure == "eof") socket.Client.Shutdown(SocketShutdown.Send);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var error = await Assert.ThrowsAnyAsync<IOException>(() => connection.OpenAsync(guard.Token));
        Assert.DoesNotContain("private-", error.ToString()); Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenBudgetAndCallerCancellationAbortTheScramExchange(bool callerCancel)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource(); var settings = Settings(listener); settings.Timeout = 1;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
            await ReadStartupAsync(stream, guard.Token); await stream.WriteAsync(Authentication(10, "SCRAM-SHA-256\0\0"), guard.Token);
            Initial(await ReadPassword(stream, guard.Token)); waiting.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var connection = new KingbaseConnection(settings.ConnectionString); var opening = connection.OpenAsync(caller.Token);
        await waiting.Task.WaitAsync(guard.Token);
        if (callerCancel) { caller.Cancel(); Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening)).CancellationToken); }
        else await Assert.ThrowsAsync<TimeoutException>(() => opening);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task StartupClearsBothSaslFramesEvenWhenAWireWriteFails(int failedWrite)
    {
        using var stream = new ScramStream(failedWrite); using var wire = new PgWire(stream);
        if (failedWrite != 0) await Assert.ThrowsAsync<IOException>(() => PgStartup.RunAsync(wire, "user", "db", "pencil"));
        else await PgStartup.RunAsync(wire, "user", "db", "pencil");
        Assert.All(stream.Borrowed.Skip(1), buffer => Assert.All(buffer, b => Assert.Equal(0, b)));
        Assert.Equal(failedWrite != 0, stream.Closed);
    }

    sealed class ScramStream(int failedWrite) : Stream
    {
        MemoryStream _input = new(); (string First, string Final, string Signature) _server;
        internal List<byte[]> Borrowed { get; } = [];
        internal bool Closed { get; private set; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Borrowed.Add(buffer);
            if (Borrowed.Count == failedWrite) throw new IOException("Scripted write failed.");
            byte[] response;
            if (Borrowed.Count == 1) response = Authentication(10, "SCRAM-SHA-256\0\0");
            else if (Borrowed.Count == 2)
            {
                _server = Server(Initial(buffer[(offset + 5)..(offset + count)]), "pencil"); response = Authentication(11, _server.First);
            }
            else
            {
                Assert.Equal(_server.Final, Encoding.UTF8.GetString(buffer, offset + 5, count - 5));
                response = Join(Authentication(12, "v=" + _server.Signature), Authentication(0, ""), Status("client_encoding", "UTF8"), Frame('K', Join(Int32(1), Int32(2))), Ready());
            }
            _input.Dispose(); _input = new MemoryStream(response);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) { token.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, Math.Min(1, count));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
        protected override void Dispose(bool disposing) { Closed = true; if (disposing) _input.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public sealed partial class PoolExecutionTests
{
    [Fact]
    public async Task PooledScramAuthenticatesOnlyOnPhysicalCreationAndReauthenticatesAfterClearWithFreshNonce()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Configuration(listener); settings.Password = "pencil";
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            string? previous = null;
            for (var physical = 0; physical < 2; physical++)
            {
                using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
                var initial = await PgScramStartupTests.Authenticate(stream, guard.Token); Assert.NotEqual(previous, initial); previous = initial;
                await Initialize(stream, guard.Token, readStartup: false); await PgSessionResetTests.Capture(stream, guard.Token);
                await Reset(stream, guard.Token); await Ping(stream, guard.Token); await Reset(stream, guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
        }, guard.Token);
        for (var physical = 0; physical < 2; physical++)
        {
            for (var logical = 0; logical < 2; logical++) { using var connection = await source.OpenConnectionAsync(guard.Token); await connection.CloseAsync(); }
            source.ClearPool();
        }
        await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }
}
