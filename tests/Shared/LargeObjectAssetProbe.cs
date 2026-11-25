using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector;
using static KingbaseConnector.Tests.PreparedAssetProbe;

namespace KingbaseConnector.Tests;

internal static class LargeObjectAssetProbe
{
    internal static async Task RunAsync(KingbaseCompatibilityMode mode)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var token = guard.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var wireMode = mode switch { KingbaseCompatibilityMode.Oracle => "oracle", KingbaseCompatibilityMode.MySql => "mysql", KingbaseCompatibilityMode.SqlServer => "sqlserver", _ => "pg" };
        string[] baseline = ["db", "user", "user", wireMode, "UTF8", "on", "lob_probe", "pg_catalog, public", "UTC", "read committed", "off", "off", "ISO, MDY", "postgres", "read committed", "off", ""];
        var data = Enumerable.Range(0, 65553).Select(i => (byte)(i % 251)).ToArray();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(token); using var stream = socket.GetStream();
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token); var length = BinaryPrimitives.ReadInt32BigEndian(header); Check(length is >= 8 and <= 4096); await stream.ReadExactlyAsync(new byte[length - 4], token);
            await stream.WriteAsync(Join(Frame('R', Int32(0)), Status("client_encoding", "UTF8"), Status("server_version", "14"), Status("database_mode", wireMode), Status("standard_conforming_strings", "on"), Status("DateStyle", "ISO, MDY"), Frame('K', Join(Int32(1), Int32(2))), Ready()), token);
            await Identity(stream); await Catalog(stream); await State(stream);
            for (var lease = 0; lease < 2; lease++)
            {
                if (lease != 0) { await Query(stream, "SELECT 1"); await stream.WriteAsync(Join(Description(1, 15002, 4), Row("1"), Complete(), Ready()), token); }
                else { await Query(stream, "SHOW max_identifier_length"); await stream.WriteAsync(Join(Description(1, 15004), Row("63"), Tag("SHOW"), Ready()), token); }
                await Control(stream, "BEGIN", 'T');
                await Function(stream, "lo_create", ["4000000000"], "4000000000", 15006);
                await Function(stream, "lo_open", ["4000000000", "393216"], "0", 15002);
                await Function(stream, "lowrite", ["0", Hex(data[..65536])], "65536", 15002);
                await Function(stream, "lowrite", ["0", Hex(data[65536..])], "17", 15002);
                await Function(stream, "lo_tell64", ["0"], "65553", 15003);
                await Function(stream, "lo_lseek64", ["0", "0", "2"], "65553", 15003);
                await Function(stream, "lo_lseek64", ["0", "65553", "0"], "65553", 15003);
                await Function(stream, "lo_lseek64", ["0", "0", "0"], "0", 15003);
                await Function(stream, "loread", ["0", "65536"], Hex(data[..65536]), 15005);
                await Function(stream, "loread", ["0", "17"], Hex(data[65536..]), 15005);
                await Function(stream, "lo_lseek64", ["0", "2147483660", "0"], "2147483660", 15003);
                await Function(stream, "lo_truncate64", ["0", "3"], "0", 15002);
                await Control(stream, "SAVEPOINT", 'T');
                await Function(stream, "lo_open", ["4000000000", "262144"], "1", 15002);
                await Control(stream, "ROLLBACK", 'T');
                await Function(stream, "lo_open", ["4000000000", "262144"], "1", 15002);
                await Function(stream, "lo_tell64", ["1"], "0", 15003);
                await Function(stream, "lo_close", ["1"], "0", 15002);
                await Function(stream, "lo_unlink", ["4000000000"], "1", 15002);
                await Function(stream, "lo_open", ["123", "262144"], "0", 15002);
                await Query(stream, "ROLLBACK"); await stream.WriteAsync(Join(Tag("ROLLBACK"), Ready()), token);
                await Query(stream, "DISCARD ALL"); await stream.WriteAsync(Join(Tag("DISCARD ALL"), Ready()), token);
                await Catalog(stream); await ReadFrames(stream, "PBDES", token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(10), Row(baseline[4..14]), Complete(), Ready()), token);
                await State(stream); await Identity(stream);
            }
            Check(await stream.ReadAsync(new byte[1], token) == 0);
        }, token);
        var settings = new KingbaseConnectionStringBuilder { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "user", Database = "db", SslMode = KingbaseSslMode.Disable,
            Pooling = true, MaxPoolSize = 1, CompatibilityMode = mode, ApplicationName = baseline[6], SearchPath = baseline[7], Timezone = baseline[8] };
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build(); KingbaseLargeObjectStream? stale = null;
        for (var lease = 0; lease < 2; lease++)
        {
            using var connection = await source.OpenConnectionAsync(token); using var transaction = await connection.BeginTransactionAsync(token); var manager = new KingbaseLargeObjectManager(connection);
            Check(await manager.CreateAsync(4000000000, token) == 4000000000); using var root = await manager.OpenReadWriteAsync(4000000000, token);
            stale?.Dispose(); Check(root.Oid == 4000000000 && root.CanRead && root.CanWrite && root.CanSeek);
            await root.WriteAsync(data, 0, data.Length, token); Check(root.Length == data.Length);
            root.Position = 0; var read = new byte[data.Length]; Check(await root.ReadAsync(read, 0, read.Length, token) == 65536); Check(root.Read(read, 65536, 17) == 17 && data.SequenceEqual(read));
            Check(await root.SeekAsync(2147483660L, SeekOrigin.Begin, token) == 2147483660L); await root.SetLengthAsync(3, token);
            await transaction.SaveAsync("s", token); var child = await manager.OpenReadAsync(4000000000, token); await transaction.RollbackAsync("s", token);
            using (var replacement = manager.OpenRead(4000000000)) { child.Dispose(); Check(!child.CanRead && replacement.Position == 0); }
            await manager.UnlinkAsync(4000000000, token); Check(!root.CanSeek); root.Dispose();
            stale = await manager.OpenReadAsync(123, token); await connection.CloseAsync(); Check(!stale.CanRead);
        }
        stale?.Dispose(); source.Dispose(); await peer.WaitAsync(token);

        async Task Query(Stream stream, string sql) { var request = await ReadFrames(stream, "Q", token); Check(Encoding.UTF8.GetString(request[0].AsSpan(5)) == sql + '\0'); }
        async Task Identity(Stream stream) { await Query(stream, "SELECT pg_catalog.version();"); await stream.WriteAsync(Join(Description(1), Row("KingbaseES V009R001C001B0001"), Complete(), Ready()), token); }
        async Task State(Stream stream) { await Query(stream, PoolAssetProbe.StateSql); await stream.WriteAsync(Join(Description(17), Row(baseline), Complete(), Ready()), token); }
        async Task Catalog(Stream stream)
        {
            await ReadFrames(stream, "Q", token); var names = new[] { "bool", "int2", "int4", "int8", "text", "bytea", "oid" }; var sizes = new[] { "1", "2", "4", "8", "-1", "-1", "4" };
            var rows = names.Select((name, i) => Row((15000 + i).ToString(CultureInfo.InvariantCulture), "90", "pg_catalog", name, sizes[i], "b", "U", "t", "0", "0", "0", "-1")).ToArray(); await stream.WriteAsync(Join(Description(12), Join(rows), Complete(), Ready()), token);
            if (mode != KingbaseCompatibilityMode.PostgreSql) { await ReadFrames(stream, "Q", token); await stream.WriteAsync(Join(Description(16), Tag("SELECT 0"), Ready()), token); }
        }
        async Task<string> Request(Stream stream)
        { var frames = await ReadFrames(stream, "PBDES", token); var end = Array.IndexOf(frames[0], (byte)0, 6); return Encoding.UTF8.GetString(frames[0], 6, end - 6); }
        async Task Control(Stream stream, string tag, char status)
        { Check((await Request(stream)).StartsWith(tag, StringComparison.Ordinal)); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('n'), Tag(tag), Frame('Z', [(byte)status])), token); }
        async Task Function(Stream stream, string name, string[] values, string result, int oid)
        {
            var frames = await ReadFrames(stream, "PBDES", token); var end = Array.IndexOf(frames[0], (byte)0, 6); var sql = Encoding.UTF8.GetString(frames[0], 6, end - 6); Check(sql.StartsWith("SELECT pg_catalog." + name + "(", StringComparison.Ordinal));
            var bind = frames[1]; Check(BinaryPrimitives.ReadInt16BigEndian(bind.AsSpan(9)) == values.Length); var offset = 11;
            foreach (var value in values) { var count = BinaryPrimitives.ReadInt32BigEndian(bind.AsSpan(offset)); offset += 4; Check(count >= 0 && Encoding.UTF8.GetString(bind, offset, count) == value); offset += count; }
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, oid, oid == 15003 ? 8 : oid == 15005 ? -1 : 4), Row(result), Complete(), Frame('Z', [(byte)'T'])), token);
        }
    }
    static string Hex(byte[] value) => "\\x" + Convert.ToHexString(value).ToLowerInvariant();
    static byte[] Tag(string text) => Frame('C', Encoding.ASCII.GetBytes(text + '\0'));
    static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Exact-asset large-object probe failed."); }
}
