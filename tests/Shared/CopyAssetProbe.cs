using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector;
using static KingbaseConnector.Tests.PreparedAssetProbe;

namespace KingbaseConnector.Tests;

internal static class CopyAssetProbe
{
    internal static async Task RunAsync(KingbaseCompatibilityMode mode)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var token = guard.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var wireMode = mode switch { KingbaseCompatibilityMode.Oracle => "oracle", KingbaseCompatibilityMode.MySql => "mysql", KingbaseCompatibilityMode.SqlServer => "sqlserver", _ => "pg" };
        string[] baseline = ["db", "user", "user", wireMode, "UTF8", "on", "copy_probe", "pg_catalog, public", "UTC", "read committed", "off", "off", "ISO, MDY", "postgres", "read committed", "off", ""];
        var raw = Enumerable.Range(0, 131079).Select(i => (byte)(i % 251)).ToArray(); var text = "1\t文字😀\n2\t\\N\n";
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(token); using var stream = socket.GetStream();
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token); var length = BinaryPrimitives.ReadInt32BigEndian(header); Check(length is >= 8 and <= 4096); await stream.ReadExactlyAsync(new byte[length - 4], token);
            await stream.WriteAsync(Join(Frame('R', Int32(0)), Status("client_encoding", "UTF8"), Status("server_version", "14"), Status("database_mode", wireMode), Status("standard_conforming_strings", "on"), Status("DateStyle", "ISO, MDY"), Frame('K', Join(Int32(1), Int32(2))), Ready()), token);
            await Identity(stream); await Catalog(stream); await State(stream);
            for (var lease = 0; lease < 2; lease++)
            {
                if (lease != 0) { await Query(stream, "SELECT 1"); await stream.WriteAsync(Join(Description(1, 15002, 4), Row("1"), Complete(), Ready()), token); }
                await Query(stream, "COPY items FROM STDIN (FORMAT BINARY)"); await stream.WriteAsync(Join(Response(true, true), Notice("import")), token);
                Check((await Import(stream)).SequenceEqual(raw)); await stream.WriteAsync(Tail(1), token);
                await Query(stream, "COPY items TO STDOUT (FORMAT BINARY)"); await stream.WriteAsync(Join(Response(false, true), Frame('d', raw), Frame('c'), Tail(1)), token);
                await Query(stream, "COPY items FROM STDIN"); await stream.WriteAsync(Response(true, false), token); Check(Encoding.UTF8.GetString(await Import(stream)) == text); await stream.WriteAsync(Tail(2), token);
                await Query(stream, "COPY items TO STDOUT"); var utf8 = Encoding.UTF8.GetBytes(text); await stream.WriteAsync(Join(Response(false, false), Frame('d', utf8[..6]), Frame('d', utf8[6..]), Frame('c'), Tail(2)), token);
                await Query(stream, "COPY items FROM STDIN (FORMAT BINARY)"); await stream.WriteAsync(Response(true, true), token); Check((await Frontend(stream)).Code == 'f');
                await stream.WriteAsync(Join(Notice("cancel"), Frame('E', Encoding.ASCII.GetBytes("SERROR\0C57014\0Mcopy-cancelled\0\0")), Ready()), token);
                await ReadFrames(stream, "PBDES", token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 15002, 4), Row("42"), Complete(), Ready()), token);
                await Query(stream, "DISCARD ALL"); await stream.WriteAsync(Join(Notice("reset"), Tag("DISCARD ALL"), Ready()), token);
                await Catalog(stream); await ReadFrames(stream, "PBDES", token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(10), Row(baseline[4..14]), Complete(), Ready()), token);
                await State(stream); await Identity(stream);
            }
            Check(await stream.ReadAsync(new byte[1], token) == 0);
        }, token);
        var settings = new KingbaseConnectionStringBuilder { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "user", Database = "db", SslMode = KingbaseSslMode.Disable,
            Pooling = true, MaxPoolSize = 1, CompatibilityMode = mode, ApplicationName = baseline[6], SearchPath = baseline[7], Timezone = baseline[8] };
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build(); var notices = new int[2];
        for (var lease = 0; lease < 2; lease++)
        {
            using var connection = await source.OpenConnectionAsync(token); var owner = lease; var failure = new InvalidOperationException("COPY asset callback");
            connection.Notice += (_, args) => { notices[owner]++; Check(args.Notice.SqlState == "00000"); try { connection.Wait(0); throw new Exception("COPY reentry accepted."); } catch (InvalidOperationException) { } if (args.Notice.MessageText == "cancel") throw failure; };
            using (var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token))
            { Check(copy.IsImport && copy.IsBinary && copy.ColumnCount == 2 && copy.RowsCopied is null); await copy.WriteAsync(raw, 0, raw.Length, token); Check(await copy.CompleteAsync(token) == 1 && copy.RowsCopied == 1); }
            using (var copy = await connection.BeginRawBinaryCopyAsync("COPY items TO STDOUT (FORMAT BINARY)", token))
            { using var actual = new MemoryStream(); await copy.CopyToAsync(actual, 7919, token); Check(actual.ToArray().SequenceEqual(raw) && copy.RowsCopied == 1); }
            using (var writer = await connection.BeginTextImportAsync("COPY items FROM STDIN", token)) { writer.Write(text); Check(await writer.CompleteAsync(token) == 2); }
            using (var reader = await connection.BeginTextExportAsync("COPY items TO STDOUT", token)) Check(await reader.ReadToEndAsync() == text);
            using (var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token))
            { try { await copy.CancelAsync(token); throw new Exception("COPY callback failure was lost."); } catch (InvalidOperationException error) { Check(ReferenceEquals(error, failure)); } }
            using var command = new KingbaseCommand("SELECT 42", connection); Check(Equals(42, await command.ExecuteScalarAsync(token))); await connection.CloseAsync();
        }
        Check(notices.SequenceEqual(new[] { 2, 2 })); source.Dispose(); await peer.WaitAsync(token);

        async Task Query(Stream stream, string sql) { var request = await ReadFrames(stream, "Q", token); Check(Encoding.UTF8.GetString(request[0].AsSpan(5)) == sql + '\0'); }
        async Task Identity(Stream stream) { await Query(stream, "SELECT pg_catalog.version();"); await stream.WriteAsync(Join(Description(1), Row("KingbaseES V009R001C001B0001"), Complete(), Ready()), token); }
        async Task State(Stream stream) { await Query(stream, PoolAssetProbe.StateSql); await stream.WriteAsync(Join(Description(17), Row(baseline), Complete(), Ready()), token); }
        async Task Catalog(Stream stream)
        {
            await ReadFrames(stream, "Q", token); var names = new[] { "bool", "int2", "int4", "int8", "text", "bytea" }; var sizes = new[] { "1", "2", "4", "8", "-1", "-1" };
            var rows = names.Select((name, i) => Row((15000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "90", "pg_catalog", name, sizes[i], "b", "U", "t", "0", "0", "0", "-1")).ToArray(); await stream.WriteAsync(Join(Description(12), Join(rows), Complete(), Ready()), token);
            if (mode != KingbaseCompatibilityMode.PostgreSql) { await ReadFrames(stream, "Q", token); await stream.WriteAsync(Join(Description(16), Tag("SELECT 0"), Ready()), token); }
        }
        async Task<byte[]> Import(Stream stream)
        {
            using var result = new MemoryStream();
            while (true) { var frame = await Frontend(stream); if (frame.Code == 'c') { Check(frame.Payload.Length == 0); return result.ToArray(); } Check(frame.Code == 'd' && frame.Payload.Length <= 65536); result.Write(frame.Payload); }
        }
        async Task<(char Code, byte[] Payload)> Frontend(Stream stream)
        { var header = new byte[5]; await stream.ReadExactlyAsync(header, token); var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)); Check(length is >= 4 and <= 65540); var payload = new byte[length - 4]; await stream.ReadExactlyAsync(payload, token); return ((char)header[0], payload); }
    }
    static byte[] Response(bool input, bool binary) => Frame(input ? 'G' : 'H', [binary ? (byte)1 : (byte)0, 0, 2, 0, binary ? (byte)1 : (byte)0, 0, binary ? (byte)1 : (byte)0]);
    static byte[] Tail(int rows) => Join(Tag("COPY " + rows), Ready());
    static byte[] Notice(string text) => Frame('N', Encoding.UTF8.GetBytes("SNOTICE\0C00000\0M" + text + "\0\0"));
    static byte[] Tag(string text) => Frame('C', Encoding.ASCII.GetBytes(text + '\0'));
    static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Exact-asset COPY probe failed."); }
}
