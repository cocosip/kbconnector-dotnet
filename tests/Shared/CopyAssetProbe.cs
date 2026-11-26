using System.Buffers.Binary;
using System.Collections;
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
        var typed = Join(new byte[] {75,66,67,79,80,89,10,255,13,10,0,0,0,0,0,0,0,0,0}, new byte[] {0,2}, Int32(4), Int32(-42), Int32(3), new byte[] {231,149,140}, new byte[] {0,2}, Int32(-1), Int32(0), new byte[] {255,255});
        KingbaseDbType[] extendedTypes = [KingbaseDbType.Numeric, KingbaseDbType.Date, KingbaseDbType.Time, KingbaseDbType.TimeTz, KingbaseDbType.Timestamp, KingbaseDbType.TimestampTz, KingbaseDbType.Interval,
            KingbaseDbType.Json, KingbaseDbType.Jsonb, KingbaseDbType.Bit, KingbaseDbType.Varbit, KingbaseDbType.Xml];
        object[] extendedValues = [KingbaseNumeric.Parse("12345.6789"), new KingbaseDate(-1, 12, 31), TimeSpan.FromDays(1),
            new KingbaseTimeTz(TimeSpan.FromTicks(10), TimeSpan.FromSeconds(19801)), KingbaseTimestamp.Parse("1999-12-31 23:59:59.999999"),
            KingbaseTimestampTz.Parse("2000-01-01 00:00:00+00"), new KingbaseInterval(-2, 3, -1), "{\"x\":1}", "[1]", new BitArray(new[] {false,false,true,false,true,true,false,false,true}), new BitArray(0), "\ufeff<?xml version='1.0' encoding='UTF-16'?><x>界</x>"];
        byte[][] extendedFields = [[0,3,0,1,0,0,0,4,0,1,9,41,26,133], [255,244,219,248], [0,0,0,20,29,215,96,0],
            [0,0,0,0,0,0,0,1,255,255,178,167], [255,255,255,255,255,255,255,255], new byte[8], [255,255,255,255,255,255,255,255,0,0,0,3,255,255,255,254],
            [123,34,120,34,58,49,125], [1,91,49,93], [0,0,0,9,44,128], [0,0,0,0], Encoding.UTF8.GetBytes("<?xml version='1.0'?><x>界</x>")];
        var extended = Join(typed[..19], new byte[] {0,(byte)extendedTypes.Length}, Join(extendedFields.Select(field => Join(Int32(field.Length), field)).ToArray()),
            new byte[] {0,(byte)extendedTypes.Length}, Join(Enumerable.Repeat(Int32(-1), extendedTypes.Length).ToArray()), new byte[] {255,255});
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(token); using var stream = socket.GetStream();
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token); var length = BinaryPrimitives.ReadInt32BigEndian(header); Check(length is >= 8 and <= 4096); await stream.ReadExactlyAsync(new byte[length - 4], token);
            await stream.WriteAsync(Join(Frame('R', Int32(0)), Status("client_encoding", "UTF8"), Status("server_version", "14"), Status("database_mode", wireMode), Status("standard_conforming_strings", "on"), Status("DateStyle", "ISO, MDY"), Status("integer_datetimes", "on"), Frame('K', Join(Int32(1), Int32(2))), Ready()), token);
            await Identity(stream); await Catalog(stream); await State(stream);
            for (var lease = 0; lease < 2; lease++)
            {
                if (lease != 0) { await Query(stream, "SELECT 1"); await stream.WriteAsync(Join(Description(1, 15002, 4), Row("1"), Complete(), Ready()), token); }
                await Query(stream, "COPY items FROM STDIN (FORMAT BINARY)"); await stream.WriteAsync(Join(Response(true, true), Notice("import")), token);
                Check((await Import(stream)).SequenceEqual(raw)); await stream.WriteAsync(Tail(1), token);
                await Query(stream, "COPY items TO STDOUT (FORMAT BINARY)"); await stream.WriteAsync(Join(Response(false, true), Frame('d', raw), Frame('c'), Tail(1)), token);
                await Query(stream, "COPY typed FROM STDIN (FORMAT BINARY)"); await stream.WriteAsync(Response(true, true), token);
                Check((await Import(stream)).SequenceEqual(typed)); await stream.WriteAsync(Tail(2), token);
                await Query(stream, "COPY typed TO STDOUT (FORMAT BINARY)"); await stream.WriteAsync(Join(Response(false, true), Frame('d', typed[..17]), Frame('d', typed[17..]), Frame('c'), Tail(2)), token);
                await Query(stream, "COPY extended FROM STDIN (FORMAT BINARY)"); await stream.WriteAsync(Response(true, true, extendedTypes.Length), token);
                Check((await Import(stream)).SequenceEqual(extended)); await stream.WriteAsync(Tail(2), token);
                await Query(stream, "COPY extended TO STDOUT (FORMAT BINARY)"); await stream.WriteAsync(Join(Response(false, true, extendedTypes.Length), Frame('d', extended[..31]), Frame('d', extended[31..]), Frame('c'), Tail(2)), token);
                await Query(stream, "COPY items FROM STDIN"); await stream.WriteAsync(Response(true, false), token); Check(Encoding.UTF8.GetString(await Import(stream)) == text); await stream.WriteAsync(Tail(2), token);
                await Query(stream, "COPY items TO STDOUT"); var utf8 = Encoding.UTF8.GetBytes(text); await stream.WriteAsync(Join(Response(false, false), Frame('d', utf8[..6]), Frame('d', utf8[6..]), Frame('c'), Tail(2)), token);
                await Query(stream, "COPY items FROM STDIN (FORMAT BINARY)"); await stream.WriteAsync(Response(true, true), token); Check((await Frontend(stream)).Code == 'f');
                await stream.WriteAsync(Join(Notice("cancel"), Frame('E', Encoding.ASCII.GetBytes("SERROR\0C57014\0Mcopy-cancelled\0\0")), Ready()), token);
                await ReadFrames(stream, "PBDES", token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 15002, 4), Row("42"), Complete(), Ready()), token);
                await Query(stream, "DISCARD ALL"); await stream.WriteAsync(Join(Notice("reset"), Tag("DISCARD ALL"), Ready()), token);
                await Catalog(stream); await ReadFrames(stream, "PBDES", token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(10), Row(baseline[4..14]), Complete(), Ready()), token);
                await State(stream); await Identity(stream);
            }
            await Query(stream, "SELECT 1"); await stream.WriteAsync(Join(Description(1, 15002, 4), Row("1"), Complete(), Ready()), token);
            await Query(stream, "COPY items TO STDOUT");
            await stream.WriteAsync(Join(Response(false, false), Frame('d', Encoding.ASCII.GetBytes("ab"))), token);
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
            using (var input = await connection.BeginBinaryImportAsync("COPY typed FROM STDIN (FORMAT BINARY)", new[] {KingbaseDbType.Integer, KingbaseDbType.Text}, token))
            { await input.WriteRowAsync(new object?[] {-42, "界"}, token); input.WriteRow(DBNull.Value, ""); Check(input.RowsCopied is null && await input.CompleteAsync(token) == 2); }
            using (var output = await connection.BeginBinaryExportAsync("COPY typed TO STDOUT (FORMAT BINARY)", new[] {KingbaseDbType.Integer, KingbaseDbType.Text}, token))
            { Check((await output.ReadRowAsync(token))!.SequenceEqual(new object[] {-42, "界"})); Check(output.ReadRow()!.SequenceEqual(new object[] {DBNull.Value, ""})); Check(await output.ReadRowAsync(token) is null && output.RowsCopied == 2); }
            using (var input = await connection.BeginBinaryImportAsync("COPY extended FROM STDIN (FORMAT BINARY)", extendedTypes, token))
            { await input.WriteRowAsync(extendedValues, token); input.WriteRow(Enumerable.Repeat<object?>(DBNull.Value, extendedTypes.Length).ToArray()); Check(await input.CompleteAsync(token) == 2); }
            using (var output = await connection.BeginBinaryExportAsync("COPY extended TO STDOUT (FORMAT BINARY)", extendedTypes, token))
            { var row = output.ReadRow()!; Check(row.Take(9).SequenceEqual(extendedValues.Take(9))); for (var i = 9; i < 11; i++) Check(((BitArray)row[i]).Cast<bool>().SequenceEqual(((BitArray)extendedValues[i]).Cast<bool>())); Check(Equals(row[11], "<?xml version='1.0'?><x>界</x>")); Check(((KingbaseNumeric)row[0]).Scale == 4 && ((KingbaseNumeric)row[0]).Coefficient == 123456789); Check((await output.ReadRowAsync(token))!.All(value => value is DBNull)); Check(output.ReadRow() is null && output.RowsCopied == 2); }
            using (var writer = await connection.BeginTextImportAsync("COPY items FROM STDIN", token)) { writer.Write(text); Check(await writer.CompleteAsync(token) == 2); }
            using (var reader = await connection.BeginTextExportAsync("COPY items TO STDOUT", token)) Check(await reader.ReadToEndAsync() == text);
            using (var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token))
            { try { await copy.CancelAsync(token); throw new Exception("COPY callback failure was lost."); } catch (InvalidOperationException error) { Check(ReferenceEquals(error, failure)); } }
            using var command = new KingbaseCommand("SELECT 42", connection); Check(Equals(42, await command.ExecuteScalarAsync(token))); await connection.CloseAsync();
        }
        Check(notices.SequenceEqual(new[] { 2, 2 }));
        using (var connection = await source.OpenConnectionAsync(token))
        {
            using var reader = await connection.BeginTextExportAsync("COPY items TO STDOUT", token); Check(reader.Read() == 'a'); connection.Close();
            var canceled = false;
            try { reader.Read(); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled);
        }
        source.Dispose(); await peer.WaitAsync(token);

        async Task Query(Stream stream, string sql) { var request = await ReadFrames(stream, "Q", token); Check(Encoding.UTF8.GetString(request[0].AsSpan(5)) == sql + '\0'); }
        async Task Identity(Stream stream) { await Query(stream, "SELECT pg_catalog.version();"); await stream.WriteAsync(Join(Description(1), Row("KingbaseES V009R001C001B0001"), Complete(), Ready()), token); }
        async Task State(Stream stream) { await Query(stream, PoolAssetProbe.StateSql); await stream.WriteAsync(Join(Description(17), Row(baseline), Complete(), Ready()), token); }
        async Task Catalog(Stream stream)
        {
            await ReadFrames(stream, "Q", token); var names = new[] { "bool", "int2", "int4", "int8", "text", "bytea", "numeric", "date", "time", "timetz", "timestamp", "timestamptz", "interval", "json", "jsonb", "bit", "varbit", "xml" }; var sizes = new[] { "1", "2", "4", "8", "-1", "-1", "-1", "4", "8", "12", "8", "8", "16", "-1", "-1", "-1", "-1", "-1" };
            var rows = names.Select((name, i) => Row((15000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "90", "pg_catalog", name, sizes[i], "b", name is "bit" or "varbit" ? "V" : "U", "t", "0", "0", "0", "-1")).ToArray(); await stream.WriteAsync(Join(Description(12), Join(rows), Complete(), Ready()), token);
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
    static byte[] Response(bool input, bool binary, int columns = 2) => Frame(input ? 'G' : 'H', [binary ? (byte)1 : (byte)0, 0, (byte)columns, .. Enumerable.Range(0, columns).SelectMany(_ => new byte[] {0, binary ? (byte)1 : (byte)0})]);
    static byte[] Tail(int rows) => Join(Tag("COPY " + rows), Ready());
    static byte[] Notice(string text) => Frame('N', Encoding.UTF8.GetBytes("SNOTICE\0C00000\0M" + text + "\0\0"));
    static byte[] Tag(string text) => Frame('C', Encoding.ASCII.GetBytes(text + '\0'));
    static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Exact-asset COPY probe failed."); }
}
