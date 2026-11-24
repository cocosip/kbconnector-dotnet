using System.Net.Sockets;
using System.Text;

namespace KingbaseConnector.ProtocolTests;

internal static class PgTestPeer
{
    internal static byte[] StartupResponse(bool mode = true) => Join(Frame('R', Int32(0)),
        Status("client_encoding", "UTF8"), Status("server_version", "14.0-kernel"),
        mode ? Status("database_mode", "pg") : [], Frame('K', Join(Int32(1), Int32(2))), Ready());
    internal static byte[] Scalar(string value) => Join(Description(1), Row([value]), Complete(), Ready());
    internal static byte[] Catalog() => Join(Description(12), Join(new[] { "bool", "int2", "int4", "int8", "text", "bytea" }
        .Select((name, i) => Row([(6000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "90", "sys_catalog", name, "4", "b", "N", "t", "0", "0", "0", "-1"])).ToArray()), Complete(), Ready());
    internal static async Task<byte[]> ReadStartupAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = ReadInt(header);
        Assert.InRange(length, 8, 4096);
        var payload = new byte[length - 4];
        await stream.ReadExactlyAsync(payload, token);
        Assert.Equal(new byte[] { 0, 3, 0, 0 }, payload[..4]);
        return payload[4..];
    }
    internal static async Task<string> ReadQueryAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, token);
        Assert.Equal((byte)'Q', header[0]);
        var length = ReadInt(header[1..]);
        Assert.InRange(length, 5, 4096);
        var payload = new byte[length - 4];
        await stream.ReadExactlyAsync(payload, token);
        Assert.Equal(0, payload[^1]);
        return Encoding.UTF8.GetString(payload[..^1]);
    }
    internal static byte[] StartupError() => Frame('E', Encoding.ASCII.GetBytes("SERROR\0C28000\0Mprivate-auth-error\0\0"));
    static byte[] Description(int count) => Frame('T', Join(Count(count), Join(Enumerable.Range(0, count).Select(i =>
        Join(Encoding.UTF8.GetBytes("c" + i + "\0"), new byte[6], Int32(25), Count(-1), Int32(-1), Count(0))).ToArray())));
    static byte[] Row(string[] values) => Frame('D', Join(Count(values.Length), Join(values.Select(value =>
        Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray())));
    static byte[] Complete() => Frame('C', Encoding.ASCII.GetBytes("SELECT 1\0"));
    static byte[] Status(string key, string value) => Frame('S', Encoding.UTF8.GetBytes(key + "\0" + value + "\0"));
    static byte[] Ready() => Frame('Z', [(byte)'I']);
    static byte[] Count(int value) => [(byte)(value >> 8), (byte)value];
    static byte[] Int32(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    static int ReadInt(byte[] bytes) => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    static byte[] Frame(char code, byte[] payload) => Join([(byte)code], Int32(payload.Length + 4), payload);
    static byte[] Join(params byte[][] pieces) => pieces.SelectMany(x => x).ToArray();
}
