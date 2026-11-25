using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector;
using static KingbaseConnector.Tests.PreparedAssetProbe;

namespace KingbaseConnector.Tests;

internal static class NotificationAssetProbe
{
    internal static async Task RunAsync(KingbaseCompatibilityMode mode)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var token = guard.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var wireMode = mode switch { KingbaseCompatibilityMode.Oracle => "oracle", KingbaseCompatibilityMode.MySql => "mysql", KingbaseCompatibilityMode.SqlServer => "sqlserver", _ => "pg" };
        string[] baseline = ["db", "user", "user", wireMode, "UTF8", "on", "notify_probe", "pg_catalog, public", "UTC", "read committed", "off", "off", "ISO, MDY", "postgres", "read committed", "off", ""];
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(token); using var stream = socket.GetStream();
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token); var length = BinaryPrimitives.ReadInt32BigEndian(header); Check(length is >= 8 and <= 4096); await stream.ReadExactlyAsync(new byte[length - 4], token);
            await stream.WriteAsync(Join(Frame('R', Int32(0)), Status("client_encoding", "UTF8"), Status("server_version", "14"), Status("database_mode", wireMode), Status("standard_conforming_strings", "on"), Status("DateStyle", "ISO, MDY"), Frame('K', Join(Int32(1), Int32(2))), Ready()), token);
            await Identity(stream); await Catalog(stream); await State(stream);
            for (var lease = 0; lease < 2; lease++)
            {
                if (lease != 0) { await Query(stream, "SELECT 1"); await stream.WriteAsync(Join(Description(1, 15002, 4), Row("1"), Complete(), Ready()), token); }
                await ReadFrames(stream, "PBDES", token);
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('n'), Tag("LISTEN"), Ready(), Notification(lease == 0 ? "wait" : "new")), token);
                if (lease == 0)
                {
                    foreach (var (value, payload) in new[] { (42, "command"), (43, "throw"), (44, "") })
                    { await ReadFrames(stream, "PBDES", token); await stream.WriteAsync(Join(payload.Length == 0 ? [] : Notification(payload), Frame('1'), Frame('2'), Description(1, 15002, 4), Row(value.ToString(System.Globalization.CultureInfo.InvariantCulture)), Complete(), Ready()), token); }
                }
                else { await ReadFrames(stream, "PBDES", token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 15002, 4), Row("45"), Complete(), Ready()), token); }
                await Query(stream, "DISCARD ALL"); await stream.WriteAsync(Join(Notification("old"), Tag("DISCARD ALL"), Ready()), token);
                await Catalog(stream); await ReadFrames(stream, "PBDES", token);
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(10), Row(baseline[4..14]), Complete(), Ready()), token);
                await State(stream); await Identity(stream);
            }
            Check(await stream.ReadAsync(new byte[1], token) == 0);
        }, token);
        var settings = new KingbaseConnectionStringBuilder { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "user", Database = "db", SslMode = KingbaseSslMode.Disable,
            Pooling = true, MaxPoolSize = 1, CompatibilityMode = mode, ApplicationName = baseline[6], SearchPath = baseline[7], Timezone = baseline[8] };
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build(); var firstEvents = new List<string>(); var secondEvents = new List<string>();
        for (var lease = 0; lease < 2; lease++)
        {
            using var connection = await source.OpenConnectionAsync(token); var events = lease == 0 ? firstEvents : secondEvents;
            var failure = new InvalidOperationException("asset notification callback");
            connection.Notification += (sender, args) =>
            {
                Check(ReferenceEquals(sender, connection) && args.ProcessId == 42 && args.Channel == "asset_ch"); events.Add(args.Payload);
                try { connection.Wait(0); throw new Exception("Callback reentrancy was accepted."); } catch (InvalidOperationException) { }
                using var nested = new KingbaseCommand("SELECT 99", connection);
                try { nested.ExecuteScalar(); throw new Exception("Callback command reentrancy was accepted."); } catch (InvalidOperationException) { }
                if (args.Payload == "throw") throw failure;
            };
            Check(!await connection.WaitAsync(10, token));
            using (var cancel = new CancellationTokenSource(10))
            { try { await connection.WaitAsync(cancel.Token); throw new Exception("Notification cancellation was ignored."); } catch (OperationCanceledException error) { Check(error.CancellationToken == cancel.Token); } }
            using var command = new KingbaseCommand("LISTEN \"asset_ch\"", connection); await command.ExecuteNonQueryAsync(token);
            Check(await connection.WaitAsync(TimeSpan.FromSeconds(2), token));
            command.CommandText = "SELECT 42";
            if (lease == 0)
            {
                Check(Equals(42, await command.ExecuteScalarAsync(token))); command.CommandText = "SELECT 43";
                try { await command.ExecuteScalarAsync(token); throw new Exception("Callback failure was lost."); } catch (InvalidOperationException error) { Check(ReferenceEquals(error, failure)); }
                command.CommandText = "SELECT 44"; Check(Equals(44, await command.ExecuteScalarAsync(token)));
            }
            else { command.CommandText = "SELECT 45"; Check(Equals(45, await command.ExecuteScalarAsync(token))); }
            await connection.CloseAsync();
        }
        Check(firstEvents.SequenceEqual(new[] { "wait", "command", "throw" }) && secondEvents.SequenceEqual(new[] { "new" }));
        source.Dispose(); await peer.WaitAsync(token);

        async Task Query(Stream stream, string sql) { var request = await ReadFrames(stream, "Q", token); Check(Encoding.UTF8.GetString(request[0].AsSpan(5)) == sql + '\0'); }
        async Task Identity(Stream stream) { await Query(stream, "SELECT pg_catalog.version();"); await stream.WriteAsync(Join(Description(1), Row("KingbaseES V009R001C001B0001"), Complete(), Ready()), token); }
        async Task State(Stream stream) { await Query(stream, PoolAssetProbe.StateSql); await stream.WriteAsync(Join(Description(17), Row(baseline), Complete(), Ready()), token); }
        async Task Catalog(Stream stream)
        {
            await ReadFrames(stream, "Q", token); var names = new[] { "bool", "int2", "int4", "int8", "text", "bytea" }; var sizes = new[] { "1", "2", "4", "8", "-1", "-1" };
            var rows = names.Select((name, i) => Row((15000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "90", "pg_catalog", name, sizes[i], "b", "U", "t", "0", "0", "0", "-1")).ToArray();
            await stream.WriteAsync(Join(Description(12), Join(rows), Complete(), Ready()), token);
            if (mode != KingbaseCompatibilityMode.PostgreSql) { await ReadFrames(stream, "Q", token); await stream.WriteAsync(Join(Description(16), Tag("SELECT 0"), Ready()), token); }
        }
    }
    static byte[] Notification(string payload) => Frame('A', Join(Int32(42), Encoding.UTF8.GetBytes("asset_ch\0" + payload + '\0')));
    static byte[] Tag(string text) => Frame('C', Encoding.ASCII.GetBytes(text + '\0'));
    static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Exact-asset notification probe failed."); }
}
