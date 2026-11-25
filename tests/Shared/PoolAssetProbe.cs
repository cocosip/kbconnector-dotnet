using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector;
using static KingbaseConnector.Tests.PreparedAssetProbe;

namespace KingbaseConnector.Tests;

// Compile unchanged against each exact package asset; do not reference provider internals.
internal static class PoolAssetProbe
{
    internal const string StateSql = "SELECT pg_catalog.current_database(),SESSION_USER,CURRENT_USER,pg_catalog.current_setting('database_mode')," +
        "pg_catalog.current_setting('client_encoding'),pg_catalog.current_setting('standard_conforming_strings')," +
        "pg_catalog.current_setting('application_name'),pg_catalog.current_setting('search_path'),pg_catalog.current_setting('TimeZone')," +
        "pg_catalog.current_setting('default_transaction_isolation'),pg_catalog.current_setting('default_transaction_read_only')," +
        "pg_catalog.current_setting('default_transaction_deferrable'),pg_catalog.current_setting('DateStyle'),pg_catalog.current_setting('IntervalStyle')," +
        "pg_catalog.current_setting('transaction_isolation'),pg_catalog.current_setting('transaction_read_only'),pg_catalog.current_setting('sql_mode',true)";
    internal static readonly string RestoreSql = "SELECT " + string.Join(",", new[] { "client_encoding", "standard_conforming_strings", "application_name", "search_path", "TimeZone",
        "default_transaction_isolation", "default_transaction_read_only", "default_transaction_deferrable", "DateStyle", "IntervalStyle" }
        .Select((name, i) => "pg_catalog.set_config('" + name + "',$" + (i + 1).ToString(CultureInfo.InvariantCulture) + ",false)"));

    internal static async Task RunAsync(KingbaseCompatibilityMode mode)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var token = guard.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var wireMode = mode switch { KingbaseCompatibilityMode.Oracle => "oracle", KingbaseCompatibilityMode.MySql => "mysql", KingbaseCompatibilityMode.SqlServer => "sqlserver", _ => "pg" };
        string[] baseline = ["db", "user", "user", wireMode, "UTF8", "on", "pool';--\\", "\"kb_汉\", pg_catalog, public", "Asia/Shanghai",
            "read committed", "off", "off", "ISO, MDY", "postgres", "read committed", "off", ""];
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(token); using var stream = socket.GetStream();
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(header); Check(startupLength is >= 8 and <= 4096);
            var startup = new byte[startupLength - 4]; await stream.ReadExactlyAsync(startup, token);
            Check(startup.AsSpan(0, 4).SequenceEqual(Int32(196608)));
            var pairs = Encoding.UTF8.GetString(startup.AsSpan(4)).Split('\0'); Check(pairs.Length % 2 == 0 && pairs[^1] == "" && pairs[^2] == "");
            var fields = Enumerable.Range(0, (pairs.Length - 2) / 2).ToDictionary(i => pairs[2 * i], i => pairs[2 * i + 1]);
            Check(fields["application_name"] == baseline[6] && fields["search_path"] == baseline[7] && fields["TimeZone"] == baseline[8] && fields["client_encoding"] == "UTF8");
            await stream.WriteAsync(Join(Frame('R', Int32(0)), Status("client_encoding", "UTF8"), Status("server_version", "14"),
                Status("database_mode", wireMode), Status("standard_conforming_strings", "on"), Status("DateStyle", baseline[12]),
                Frame('K', Join(Int32(1), Int32(2))), Ready()), token);
            await Identity(stream); await Catalog(stream, 12000); await State(stream);
            for (var borrower = 0; borrower < 2; borrower++)
            {
                var oid = 12002 + borrower * 10000;
                if (borrower == 0)
                {
                    var prepare = await ReadFrames(stream, "PS", token);
                    Check(prepare[0].SequenceEqual(Frame('P', Join(Encoding.UTF8.GetBytes("kbp_1\0SELECT $1\0"), Count(1), Int32(oid)))));
                    await stream.WriteAsync(Join(Frame('1'), Ready()), token);
                }
                else
                {
                    await Query(stream, "SELECT 1"); await stream.WriteAsync(Join(Description(1), Row("1"), Complete(), Ready()), token);
                }
                var execute = await ReadFrames(stream, borrower == 0 ? "BDES" : "PBDES", token);
                if (borrower != 0) Check(execute[0].SequenceEqual(Frame('P', Join([0], Encoding.UTF8.GetBytes("SELECT $1\0"), Count(1), Int32(oid)))));
                var bind = execute[borrower == 0 ? 0 : 1]; var value = borrower == 0 ? "42" : "43";
                Check(bind.SequenceEqual(Frame('B', Join(Encoding.UTF8.GetBytes(borrower == 0 ? "\0kbp_1\0" : "\0\0"), Count(0), Count(1), Int32(2), Encoding.UTF8.GetBytes(value), Count(0)))));
                await stream.WriteAsync(Join(borrower == 0 ? [] : Frame('1'), Frame('2'), Description(1, oid, 4), Row(value), Complete(), Ready()), token);
                await Query(stream, "DISCARD ALL"); await stream.WriteAsync(Join(Frame('C', Encoding.ASCII.GetBytes("DISCARD ALL\0")), Ready()), token);
                var fresh = 22000 + borrower * 10000; await Catalog(stream, fresh);
                var restore = await ReadFrames(stream, "PBDES", token);
                Check(restore[0].SequenceEqual(Frame('P', Join([0], Encoding.UTF8.GetBytes(RestoreSql + "\0"), Count(10), Join(Enumerable.Repeat(Int32(fresh + 4), 10).ToArray())))));
                var values = baseline.Skip(4).Take(10).Select(item => Join(Int32(Encoding.UTF8.GetByteCount(item)), Encoding.UTF8.GetBytes(item))).ToArray();
                Check(restore[1].SequenceEqual(Frame('B', Join([0, 0], Count(0), Count(10), Join(values), Count(0)))));
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(10), Row(baseline[4..14]), Complete(), Ready()), token);
                await State(stream); await Identity(stream);
            }
            Check(await stream.ReadAsync(new byte[1], token) == 0);
        }, token);
        var settings = new KingbaseConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "user", Database = "db",
            SslMode = KingbaseSslMode.Disable, CompatibilityMode = mode, Pooling = true, MaxPoolSize = 1,
            ApplicationName = baseline[6], SearchPath = baseline[7], Timezone = baseline[8]
        };
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        using var connection = source.CreateConnection(); using var command = new KingbaseCommand("SELECT :p", connection);
        command.Parameters.AddWithValue("p", 42);
        for (var borrower = 0; borrower < 2; borrower++)
        {
            await connection.OpenAsync(token); if (borrower == 0) await command.PrepareAsync(token);
            command.Parameters[0].Value = 42 + borrower; Check(Equals(42 + borrower, await command.ExecuteScalarAsync(token)));
            await connection.CloseAsync();
        }
        source.Dispose(); await peer.WaitAsync(token);

        async Task Query(Stream stream, string expected)
        {
            var query = await ReadFrames(stream, "Q", token); Check(Encoding.UTF8.GetString(query[0].AsSpan(5)) == expected + "\0");
        }
        async Task Identity(Stream stream)
        {
            await Query(stream, "SELECT pg_catalog.version();");
            await stream.WriteAsync(Join(Description(1), Row("KingbaseES V009R001C001B0001"), Complete(), Ready()), token);
        }
        async Task Catalog(Stream stream, int start)
        {
            var query = await ReadFrames(stream, "Q", token); Check(Encoding.UTF8.GetString(query[0]).Contains("FROM sys_catalog.sys_type", StringComparison.Ordinal));
            var names = new[] { "bool", "int2", "int4", "int8", "text", "bytea" }; var sizes = new[] { "1", "2", "4", "8", "-1", "-1" };
            var rows = names.Select((name, i) => Row((start + i).ToString(CultureInfo.InvariantCulture), "90", "pg_catalog", name, sizes[i], "b", "U", "t", "0", "0", "0", "-1")).ToArray();
            await stream.WriteAsync(Join(Description(12), Join(rows), Complete(), Ready()), token);
            if (mode != KingbaseCompatibilityMode.PostgreSql)
            {
                var native = await ReadFrames(stream, "Q", token); Check(Encoding.UTF8.GetString(native[0]).Contains("JOIN sys_catalog.sys_proc pi", StringComparison.Ordinal));
                await stream.WriteAsync(Join(Description(16), Frame('C', Encoding.ASCII.GetBytes("SELECT 0\0")), Ready()), token);
            }
        }
        async Task State(Stream stream)
        {
            await Query(stream, StateSql); await stream.WriteAsync(Join(Description(17), Row(baseline), Complete(), Ready()), token);
        }
    }
    static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Exact-asset pool probe failed."); }
}
