using System.Security.Cryptography;

namespace KingbaseConnector.Protocol;

/// <summary>Handles only server-requested PG password/MD5 exchanges; no I/O or fallback.</summary>
internal sealed class PgPasswordAuthentication
{
    enum State { Initial, AwaitingOk, Complete, Failed }
    State _state;

    internal bool IsComplete => _state == State.Complete;

    // The caller clears a returned secret-bearing frame after transmission, including failed writes.
    internal byte[]? Process(PgMessage message, string username, string password)
    {
        try
        {
            if (_state == State.Complete || _state == State.Failed || message.Code != (byte)'R' ||
                message.Payload is null || message.Payload.Length < 4)
                throw InvalidExchange();

            var algorithm = PgBinary.ReadInt32(message.Payload, 0);
            if (algorithm == 0)
            {
                if (message.Payload.Length != 4) throw InvalidExchange();
                _state = State.Complete;
                return null;
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
                default:
                    throw new NotSupportedException("Unsupported PG authentication algorithm.");
            }
            _state = State.AwaitingOk;
            return response;
        }
        catch
        {
            _state = State.Failed;
            throw;
        }
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
