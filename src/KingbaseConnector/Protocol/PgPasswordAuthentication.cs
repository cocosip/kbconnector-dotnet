using System.Security.Cryptography;

namespace KingbaseConnector.Protocol;

/// <summary>Handles server-requested PG password/MD5/SCRAM exchanges; no I/O or fallback.</summary>
internal sealed class PgPasswordAuthentication : IDisposable
{
    enum State { Initial, AwaitingOk, ScramContinue, ScramFinal, ScramOk, Complete, Failed }
    State _state;
    readonly string? _clientNonce;
    PgScramSha256? _scram;
    internal PgPasswordAuthentication(string? clientNonce = null) => _clientNonce = clientNonce;

    internal bool IsComplete => _state == State.Complete;

    // The caller clears a returned secret-bearing frame after transmission, including failed writes.
    internal byte[]? Process(PgMessage message, string username, string password, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_state == State.Complete || _state == State.Failed || message.Code != (byte)'R' ||
                message.Payload is null || message.Payload.Length < 4)
                throw InvalidExchange();

            var algorithm = PgBinary.ReadInt32(message.Payload, 0);
            if (algorithm == 0)
            {
                if (message.Payload.Length != 4 || _state is not (State.Initial or State.AwaitingOk or State.ScramOk)) throw InvalidExchange();
                _state = State.Complete;
                return null;
            }
            if (_state == State.ScramContinue && algorithm == 11)
            {
                var final = _scram!.Continue(message.Payload, password, cancellationToken);
                _state = State.ScramFinal; return final;
            }
            if (_state == State.ScramFinal && algorithm == 12)
            {
                _scram!.Verify(message.Payload); _state = State.ScramOk; return null;
            }
            if (_state != State.Initial) throw InvalidExchange();
            byte[] response;
            switch (algorithm)
            {
                case 3:
                    if (message.Payload.Length != 4) throw InvalidExchange();
                    response = PgFrontendMessages.Password(password);
                    break;
                case 5:
                    if (message.Payload.Length != 8) throw InvalidExchange();
                    response = Md5Response(username, password, message.Payload);
                    break;
                case 10:
                    PgScramSha256.ValidateOffer(message.Payload);
                    _scram = new PgScramSha256(username, _clientNonce);
                    var initial = _scram.InitialResponse();
                    _state = State.ScramContinue; return initial;
                default:
                    throw new NotSupportedException("Unsupported PG authentication algorithm.");
            }
            _state = State.AwaitingOk;
            return response;
        }
        catch
        {
            _state = State.Failed;
            _scram?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _scram?.Dispose();
        if (_state != State.Complete) _state = State.Failed;
    }

    internal static (int Password, int User) Md5InputLengths(string username, string password)
    {
        var passwordLength = PgFrontendMessages.CStringLength(password) - 1;
        var userLength = PgFrontendMessages.CStringLength(username) - 1;
        if ((long)passwordLength + userLength > PgWire.MaxMessageLength - 4)
            throw new ArgumentException("Oversized PG authentication input.");
        return (passwordLength, userLength);
    }

    static byte[] Md5Response(string username, string password, byte[] payload)
    {
        var (passwordLength, userLength) = Md5InputLengths(username, password);
        var credentials = new byte[passwordLength + userLength];
        var saltedHash = new byte[36];
        byte[]? innerHash = null;
        byte[]? outerHash = null;
        try
        {
            PgBinary.Utf8.GetBytes(password, 0, password.Length, credentials, 0);
            PgBinary.Utf8.GetBytes(username, 0, username.Length, credentials, passwordLength);
            using var md5 = MD5.Create();
            innerHash = md5.ComputeHash(credentials);
            WriteHex(innerHash, saltedHash, 0);
            Buffer.BlockCopy(payload, 4, saltedHash, 32, 4);
            outerHash = md5.ComputeHash(saltedHash);
            var response = new byte[41];
            response[0] = (byte)'p';
            PgBinary.WriteInt32(response, 1, 40);
            response[5] = (byte)'m';
            response[6] = (byte)'d';
            response[7] = (byte)'5';
            WriteHex(outerHash, response, 8);
            return response;
        }
        finally
        {
            Array.Clear(credentials, 0, credentials.Length);
            Array.Clear(saltedHash, 0, saltedHash.Length);
            if (innerHash is not null) Array.Clear(innerHash, 0, innerHash.Length);
            if (outerHash is not null) Array.Clear(outerHash, 0, outerHash.Length);
        }
    }

    static void WriteHex(byte[] input, byte[] output, int offset)
    {
        const string hex = "0123456789abcdef";
        foreach (var value in input)
        {
            output[offset++] = (byte)hex[value >> 4];
            output[offset++] = (byte)hex[value & 15];
        }
    }

    static IOException InvalidExchange() => new("Invalid PG authentication exchange.");
}
