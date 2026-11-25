using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector;

namespace KingbaseConnector.Tests;

// The consumer compiles this against each exact archive asset, including both Standard DLLs.
internal static class PreparedAssetProbe
{
    internal static async Task RunAsync(bool automatic = false, bool date = false)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var token = guard.Token;
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(token); using var stream = socket.GetStream();
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(header);
            Require(startupLength is >= 8 and <= 4096);
            await stream.ReadExactlyAsync(new byte[startupLength - 4], token);
            await stream.WriteAsync(Join(Frame('R', Int32(0)), Status("client_encoding", "UTF8"), Status("server_version", "14"),
                Status("database_mode", "pg"), Status("standard_conforming_strings", "on"), Status("DateStyle", "ISO, MDY"), Frame('K', Join(Int32(1), Int32(2))), Ready()), token);
            var identity = await ReadFrames(stream, "Q", token);
            Require(Encoding.UTF8.GetString(identity[0].AsSpan(5)) == "SELECT pg_catalog.version();\0");
            await stream.WriteAsync(Join(Description(1), Row("KingbaseES V009R001C001B0001"), Complete(), Ready()), token);
            var catalogQuery = await ReadFrames(stream, "Q", token);
            Require(Encoding.UTF8.GetString(catalogQuery[0]).Contains("FROM sys_catalog.sys_type", StringComparison.Ordinal));
            var names = new[] { "bool", "int2", "int4", "int8", "text", "bytea", "date" };
            var sizes = new[] { "1", "2", "4", "8", "-1", "-1", "4" };
            var rows = names.Select((name, i) => Row((12000 + i).ToString(CultureInfo.InvariantCulture), "90", "pg_catalog", name,
                sizes[i], "b", "U", "t", "0", "0", "0", "-1")).ToArray();
            await stream.WriteAsync(Join(Description(12), Join(rows), Complete(), Ready()), token);
            if (date)
            {
                foreach (var value in DateValues)
                {
                    var execute = await ReadFrames(stream, "PBDES", token);
                    Require(execute[0].AsSpan(execute[0].Length - 4).SequenceEqual(Int32(12006)));
                    Require(Encoding.UTF8.GetString(execute[1]).Contains(value, StringComparison.Ordinal));
                    await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 12006, 4), Row(value), Complete(), Ready()), token);
                }
                Require(await stream.ReadAsync(new byte[1], token) == 0);
                return;
            }
            if (automatic)
            {
                await ReadFrames(stream, "PBDES", token);
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 12002, 4), Row("41"), Complete(), Ready()), token);
            }
            var prepare = await ReadFrames(stream, "PS", token);
            Require(prepare[0].SequenceEqual(Frame('P', Join(Encoding.UTF8.GetBytes("kbp_1\0SELECT $1\0"), Count(1), Int32(12002)))));
            await stream.WriteAsync(Join(Frame('1'), Ready()), token);
            foreach (var value in new[] { "42", "43" })
            {
                var execute = await ReadFrames(stream, "BDES", token);
                Require(execute[0].SequenceEqual(Frame('B', Join(Encoding.UTF8.GetBytes("\0kbp_1\0"), Count(0), Count(1),
                    Int32(2), Encoding.UTF8.GetBytes(value), Count(0)))));
                await stream.WriteAsync(Join(Frame('2'), Description(1, 12002, 4), Row(value), Complete(), Ready()), token);
            }
            var close = await ReadFrames(stream, "CS", token);
            Require(close[0].SequenceEqual(Frame('C', Encoding.UTF8.GetBytes("Skbp_1\0"))));
            await stream.WriteAsync(Join(Frame('3'), Ready()), token);
            await ReadFrames(stream, "PBDES", token);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 12002, 4), Row("43"), Complete(), Ready()), token);
            Require(await stream.ReadAsync(new byte[1], token) == 0);
        }, token);
        using var connection = new KingbaseConnection(new KingbaseConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Database = "db", Username = "user",
            Pooling = false, SslMode = KingbaseSslMode.Disable,
            MaxAutoPrepare = automatic ? 1 : 0, AutoPrepareMinUsages = 2
        }.ConnectionString);
        await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT :p", connection);
        if (date)
        {
            command.Parameters.AddWithValue("p", default(KingbaseDate));
            foreach (var value in DateValues)
            {
                command.Parameters[0].Value = KingbaseDate.Parse(value);
                using var reader = await command.ExecuteReaderAsync(token);
                Require(reader.Read());
                Require(reader.GetFieldType(0) == typeof(DateTime) && reader.GetProviderSpecificFieldType(0) == typeof(KingbaseDate));
                Require(reader.GetFieldValue<KingbaseDate>(0).ToString() == value);
                if (value == "2000-02-29") Require(reader.GetDateTime(0) == new DateTime(2000, 2, 29) && reader.GetDateTime(0).Kind == DateTimeKind.Unspecified);
                else
                {
                    try { reader.GetDateTime(0); throw new InvalidOperationException("A non-CLR date was silently converted."); }
                    catch (OverflowException) { }
                }
            }
            connection.Close(); await peer.WaitAsync(token); return;
        }
        command.Parameters.AddWithValue("p", 42);
        if (automatic)
        {
            command.Parameters[0].Value = 41;
            Require(Equals(await command.ExecuteScalarAsync(token), 41));
            command.Parameters[0].Value = 42;
        }
        else
        {
            ((DbCommand)command).Prepare();
            await command.PrepareAsync(token); // Typed async counterpart also exists on Standard 2.0.
        }
        Require(Equals(await command.ExecuteScalarAsync(token), 42));
        command.Parameters[0].Value = 43;
        Require(Equals(command.ExecuteScalar(), 43));
        await command.UnprepareAsync(token); command.Unprepare();
        Require(Equals(await command.ExecuteScalarAsync(token), 43));
        connection.Close(); await peer.WaitAsync(token);
    }

    static readonly string[] DateValues = ["2000-02-29", "0001-01-01 BC", "infinity", "-infinity"];

    static async Task<byte[][]> ReadFrames(Stream stream, string codes, CancellationToken token)
    {
        var result = new List<byte[]>();
        foreach (var code in codes)
        {
            var header = new byte[5]; await stream.ReadExactlyAsync(header, token); Require(header[0] == (byte)code);
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)); Require(length is >= 4 and <= 1048576);
            var payload = new byte[length - 4]; await stream.ReadExactlyAsync(payload, token); result.Add(Join(header, payload));
        }
        return result.ToArray();
    }
    static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Exact-asset public preparation probe failed."); }
    static byte[] Int32(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    static byte[] Count(int value) => [(byte)(value >> 8), (byte)value];
    static byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    static byte[] Frame(char code, params byte[] payload) => Join([(byte)code], Int32(payload.Length + 4), payload);
    static byte[] Ready() => Frame('Z', [(byte)'I']);
    static byte[] Complete() => Frame('C', Encoding.ASCII.GetBytes("SELECT 1\0"));
    static byte[] Status(string key, string value) => Frame('S', Encoding.UTF8.GetBytes(key + "\0" + value + "\0"));
    static byte[] Row(params string[] values) => Frame('D', Join(Count(values.Length), Join(values.Select(value =>
        Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray())));
    static byte[] Description(int count, int oid = 25, int size = -1) => Frame('T', Join(Count(count), Join(Enumerable.Range(0, count)
        .Select(i => Join(Encoding.UTF8.GetBytes("c" + i + "\0"), new byte[6], Int32(oid), Count(size), Int32(-1), Count(0))).ToArray())));
}
