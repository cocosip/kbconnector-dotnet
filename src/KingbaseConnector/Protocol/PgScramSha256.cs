using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KingbaseConnector.Protocol;

/// <summary>One SCRAM-SHA-256 transcript; its caller owns authentication sequencing and wire disposal.</summary>
internal sealed class PgScramSha256 : IDisposable
{
    const int MaxPayload = 16 * 1024;
    readonly string _nonce;
    readonly string _firstBare;
    byte[]? _serverSignature;

    internal PgScramSha256(string username, string? nonce)
    {
        PgFrontendMessages.CStringLength(username);
        if (nonce is null)
        {
            var random = new byte[24];
            using var generator = RandomNumberGenerator.Create(); generator.GetBytes(random);
            nonce = Convert.ToBase64String(random); Clear(random);
        }
        if (!ValidNonce(nonce)) throw InvalidExchange();
        _nonce = nonce;
        _firstBare = "n=" + username.Replace("=", "=3D").Replace(",", "=2C") + ",r=" + nonce;
    }

    internal static void ValidateOffer(byte[] payload)
    {
        if (payload.Length < 6 || payload.Length > MaxPayload) throw InvalidExchange();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var offset = 4;
        while (offset < payload.Length && payload[offset] != 0)
        {
            var start = offset;
            while (offset < payload.Length && payload[offset] != 0)
            {
                var c = payload[offset++];
                if (!(c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_')) throw InvalidExchange();
            }
            if (offset - start > 20 || offset == payload.Length || !names.Add(Encoding.ASCII.GetString(payload, start, offset - start)))
                throw InvalidExchange();
            offset++;
        }
        if (names.Count == 0 || offset != payload.Length - 1 || payload[offset] != 0) throw InvalidExchange();
        if (!names.Contains("SCRAM-SHA-256")) throw new NotSupportedException("Unsupported PG SASL authentication mechanism.");
    }

    internal byte[] InitialResponse()
    {
        var first = Encode("n,," + _firstBare);
        var mechanism = Encoding.ASCII.GetBytes("SCRAM-SHA-256\0");
        try
        {
            if (mechanism.Length + 4 + first.Length > MaxPayload) throw InvalidExchange();
            var message = new byte[5 + mechanism.Length + 4 + first.Length];
            message[0] = (byte)'p'; PgBinary.WriteInt32(message, 1, message.Length - 1);
            Buffer.BlockCopy(mechanism, 0, message, 5, mechanism.Length);
            PgBinary.WriteInt32(message, 5 + mechanism.Length, first.Length);
            Buffer.BlockCopy(first, 0, message, 9 + mechanism.Length, first.Length);
            return message;
        }
        finally { Clear(first); }
    }

    internal byte[] Continue(byte[] payload, string password, CancellationToken token)
    {
        var text = Decode(payload);
        var attributes = Attributes(text);
        if (attributes.Count < 3 || attributes[0].Key != 'r' || attributes[1].Key != 's' || attributes[2].Key != 'i') throw InvalidExchange();
        var nonce = attributes[0].Value;
        if (!ValidNonce(nonce) || !nonce.StartsWith(_nonce, StringComparison.Ordinal) || nonce.Length == _nonce.Length) throw InvalidExchange();
        var iterationText = attributes[2].Value;
        if (iterationText[0] is < '1' or > '9' || iterationText.Any(c => c is < '0' or > '9') ||
            !int.TryParse(iterationText, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations > 1_000_000) throw InvalidExchange();
        byte[]? salt = null, passwordBytes = null, salted = null, clientKey = null, storedKey = null,
            serverKey = null, transcript = null, clientSignature = null, proof = null;
        try
        {
            salt = Base64(attributes[1].Value, 1024);
            token.ThrowIfCancellationRequested();
            passwordBytes = PgBinary.Utf8.GetBytes(PgSaslPrep.Prepare(password));
            salted = Derive(passwordBytes, salt, iterations, token);
            clientKey = Mac(salted, Encoding.ASCII.GetBytes("Client Key"));
            using (var sha = SHA256.Create()) storedKey = sha.ComputeHash(clientKey);
            serverKey = Mac(salted, Encoding.ASCII.GetBytes("Server Key"));
            var final = "c=biws,r=" + nonce;
            transcript = PgBinary.Utf8.GetBytes(_firstBare + "," + text + "," + final);
            clientSignature = Mac(storedKey, transcript); proof = new byte[32];
            for (var index = 0; index < proof.Length; index++) proof[index] = (byte)(clientKey[index] ^ clientSignature[index]);
            _serverSignature = Mac(serverKey, transcript);
            token.ThrowIfCancellationRequested();
            return Response(final + ",p=" + Convert.ToBase64String(proof));
        }
        catch { Dispose(); throw; }
        finally
        {
            Clear(salt); Clear(passwordBytes); Clear(salted); Clear(clientKey); Clear(storedKey);
            Clear(serverKey); Clear(transcript); Clear(clientSignature); Clear(proof);
        }
    }

    internal void Verify(byte[] payload)
    {
        byte[]? actual = null;
        try
        {
            var attributes = Attributes(Decode(payload));
            if (attributes[0].Key != 'v' || attributes.Any(a => a.Key == 'e') || _serverSignature is null) throw InvalidExchange();
            actual = Base64(attributes[0].Value, 32);
            if (actual.Length != 32) throw InvalidExchange();
            var difference = 0;
            for (var index = 0; index < 32; index++) difference |= actual[index] ^ _serverSignature[index];
            if (difference != 0) throw InvalidExchange();
        }
        finally { Clear(actual); Dispose(); }
    }

    static byte[] Derive(byte[] password, byte[] salt, int iterations, CancellationToken token)
    {
        var block = new byte[salt.Length + 4]; Buffer.BlockCopy(salt, 0, block, 0, salt.Length); block[block.Length - 1] = 1;
        byte[]? previous = null; byte[]? output = null;
        try
        {
            using var hmac = new HMACSHA256(password);
            previous = hmac.ComputeHash(block); output = (byte[])previous.Clone();
            for (var iteration = 1; iteration < iterations; iteration++)
            {
                if ((iteration & 63) == 0) token.ThrowIfCancellationRequested();
                var next = hmac.ComputeHash(previous); Clear(previous); previous = next;
                for (var index = 0; index < output.Length; index++) output[index] ^= previous[index];
            }
            token.ThrowIfCancellationRequested(); var result = output; output = null; return result;
        }
        finally { Clear(block); Clear(previous); Clear(output); }
    }
    static byte[] Mac(byte[] key, byte[] data) { using var hmac = new HMACSHA256(key); return hmac.ComputeHash(data); }
    static List<KeyValuePair<char, string>> Attributes(string text)
    {
        var attributes = new List<KeyValuePair<char, string>>(); var keys = new HashSet<char>();
        foreach (var item in text.Split(','))
        {
            if (item.Length < 3 || item[1] != '=' || !(item[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z') ||
                item.IndexOf('\0') >= 0 || item[0] == 'm' || !keys.Add(item[0])) throw InvalidExchange();
            attributes.Add(new KeyValuePair<char, string>(item[0], item.Substring(2)));
        }
        return attributes;
    }
    static bool ValidNonce(string value) => value.Length != 0 && value.All(c => c is >= '!' and <= '~' && c != ',');
    static byte[] Base64(string text, int maximum)
    {
        if (text.Length == 0 || text.Length > (maximum + 2) / 3 * 4 || text.Length % 4 != 0 ||
            text.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '='))) throw InvalidExchange();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); } catch (FormatException) { throw InvalidExchange(); }
        if (bytes.Length > maximum || Convert.ToBase64String(bytes) != text) { Clear(bytes); throw InvalidExchange(); }
        return bytes;
    }
    static string Decode(byte[] payload)
    {
        if (payload.Length < 5 || payload.Length > MaxPayload) throw InvalidExchange();
        try { return PgBinary.Utf8.GetString(payload, 4, payload.Length - 4); }
        catch (DecoderFallbackException) { throw InvalidExchange(); }
    }
    static byte[] Encode(string text)
    {
        if (PgFrontendMessages.CStringLength(text) - 1 > MaxPayload) throw InvalidExchange();
        return PgBinary.Utf8.GetBytes(text);
    }
    static byte[] Response(string text)
    {
        var body = Encode(text);
        try
        {
            var message = new byte[5 + body.Length]; message[0] = (byte)'p'; PgBinary.WriteInt32(message, 1, message.Length - 1);
            Buffer.BlockCopy(body, 0, message, 5, body.Length); return message;
        }
        finally { Clear(body); }
    }
    static void Clear(byte[]? bytes) { if (bytes is not null) Array.Clear(bytes, 0, bytes.Length); }
    internal static IOException InvalidExchange() => new("Invalid PG SCRAM authentication exchange.");
    public void Dispose() { Clear(_serverSignature); _serverSignature = null; }
}
