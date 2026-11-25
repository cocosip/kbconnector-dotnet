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
    internal static async Task RunAsync(bool automatic = false, bool date = false, bool time = false, bool timestamp = false, bool interval = false, bool batch = false)
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
            var names = new[] { "bool", "int2", "int4", "int8", "text", "bytea", "date", "time", "timetz", "timestamp", "timestamptz", "interval" };
            var sizes = new[] { "1", "2", "4", "8", "-1", "-1", "4", "8", "12", "8", "8", "16" };
            var rows = names.Select((name, i) => Row((12000 + i).ToString(CultureInfo.InvariantCulture), "90", "pg_catalog", name,
                sizes[i], "b", "U", "t", "0", "0", "0", "-1")).ToArray();
            await stream.WriteAsync(Join(Description(12), Join(rows), Complete(), Ready()), token);
            if (batch)
            {
                var batchPrepare = await ReadFrames(stream, "PS", token);
                Require(Encoding.UTF8.GetString(batchPrepare[0]).Contains("SELECT $1", StringComparison.Ordinal));
                await stream.WriteAsync(Join(Frame('1'), Ready()), token);
                foreach (var value in new[] { "41", "42" })
                {
                    var execute = await ReadFrames(stream, "BDES", token);
                    Require(Encoding.UTF8.GetString(execute[0]).Contains(value, StringComparison.Ordinal));
                    await stream.WriteAsync(Join(Frame('2'), Description(1, 12002, 4), Row(value), Complete(), Ready()), token);
                }
                await ReadFrames(stream, "PBDES", token);
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('n'), Frame('C', Encoding.UTF8.GetBytes("UPDATE 1\0")), Ready()), token);
                await ReadFrames(stream, "PBDES", token);
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('E', Encoding.UTF8.GetBytes("SERROR\0C22012\0Msynthetic-failure\0\0")), Ready()), token);
                await ReadFrames(stream, "PBDES", token);
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 123456, -1), Row("{1}"), Frame('C', Encoding.UTF8.GetBytes("UPDATE 1\0")), Ready()), token);
                await ReadFrames(stream, "PBDES", token);
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 12006, 4), Row("infinity"), Complete(), Ready()), token);
                var recovery = await ReadFrames(stream, "PBDES", token);
                Require(Encoding.UTF8.GetString(recovery[0]).Contains("SELECT 42", StringComparison.Ordinal));
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 12002, 4), Row("42"), Complete(), Ready()), token);
                Require(await stream.ReadAsync(new byte[1], token) == 0); return;
            }
            if (interval)
            {
                foreach (var (style, value) in IntervalValues)
                {
                    var execute = await ReadFrames(stream, "PBDES", token);
                    Require(execute[0].AsSpan(execute[0].Length - 4).SequenceEqual(Int32(12011)));
                    Require(Encoding.UTF8.GetString(execute[1]).Contains(value.StartsWith("PT", StringComparison.Ordinal) ? "PT-0.000001S" : "P14M-3DT-0.000001S", StringComparison.Ordinal));
                    await stream.WriteAsync(Join(Frame('1'), Frame('2'), Status("IntervalStyle", style), Description(1, 12011, 16),
                        Row(value), Complete(), Status("IntervalStyle", "unsupported"), Ready()), token);
                }
                await ReadFrames(stream, "PBDES", token);
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 12011, 16), Row((string?)null), Complete(), Ready()), token);
                Require(await stream.ReadAsync(new byte[1], token) == 0); return;
            }
            if (timestamp)
            {
                foreach (var zoned in new[] { false, true })
                foreach (var value in TimestampValues(zoned))
                {
                    var oid = zoned ? 12010 : 12009;
                    var execute = await ReadFrames(stream, "PBDES", token);
                    Require(execute[0].AsSpan(execute[0].Length - 4).SequenceEqual(Int32(oid)));
                    Require(Encoding.UTF8.GetString(execute[1]).Contains(value, StringComparison.Ordinal));
                    await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, oid, 8), Row(value), Complete(), Ready()), token);
                }
                Require(await stream.ReadAsync(new byte[1], token) == 0); return;
            }
            if (time)
            {
                foreach (var zoned in new[] { false, true })
                {
                    var oid = zoned ? 12008 : 12007;
                    foreach (var value in TimeValues(zoned))
                    {
                        var execute = await ReadFrames(stream, "PBDES", token);
                        Require(execute[0].AsSpan(execute[0].Length - 4).SequenceEqual(Int32(oid)));
                        Require(Encoding.UTF8.GetString(execute[1]).Contains(value, StringComparison.Ordinal));
                        await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, oid, zoned ? 12 : 8), Row(value), Complete(), Ready()), token);
                    }
                }
                Require(await stream.ReadAsync(new byte[1], token) == 0); return;
            }
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
        if (batch)
        {
            using var valueBatch = connection.CreateBatch();
            foreach (var value in new[] { 41, 42 })
            {
                var item = valueBatch.CreateBatchCommand(); item.CommandText = "SELECT @p";
                item.Parameters.AddWithValue("p", value); valueBatch.BatchCommands.Add(item);
            }
            await valueBatch.PrepareAsync(token);
            using (var reader = await valueBatch.ExecuteReaderAsync(token))
            {
                Require(reader.Read() && reader.GetInt32(0) == 41);
                Require(reader.NextResult() && reader.Read() && reader.GetInt32(0) == 42);
                Require(!reader.NextResult());
            }
            foreach (var item in valueBatch.BatchCommands) Require(item.ExecutionStatus == KingbaseBatchCommandStatus.Succeeded);
            using var failureBatch = KingbaseFactory.Instance.CreateBatch(); failureBatch.Connection = connection;
            foreach (var sql in new[] { "UPDATE t SET n=1", "SELECT 1/0", "SELECT 99" }) failureBatch.BatchCommands.Add(new(sql));
            try { await failureBatch.ExecuteNonQueryAsync(token); throw new InvalidOperationException("Batch ignored an error."); }
            catch (KingbaseException error)
            {
                Require(error.SqlState == "22012" && ReferenceEquals(error.BatchCommand, failureBatch.BatchCommands[1]));
                Require(failureBatch.FailedCommandIndex == 1 && failureBatch.BatchCommands[0].RecordsAffected == 1);
                Require(failureBatch.BatchCommands[0].ExecutionStatus == KingbaseBatchCommandStatus.Succeeded);
                Require(failureBatch.BatchCommands[1].ExecutionStatus == KingbaseBatchCommandStatus.Failed);
                Require(failureBatch.BatchCommands[2].ExecutionStatus == KingbaseBatchCommandStatus.NotExecuted);
            }
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await failureBatch.ExecuteNonQueryAsync(canceled.Token); throw new InvalidOperationException("Batch ignored cancellation."); }
            catch (OperationCanceledException) { }
            Require(failureBatch.FailedCommandIndex is null);
            foreach (var item in failureBatch.BatchCommands) Require(item.ExecutionStatus == KingbaseBatchCommandStatus.NotExecuted && item.RecordsAffected == -1);
            using var validationBatch = connection.CreateBatch();
            var returning = KingbaseFactory.Instance.CreateBatchCommand(); returning.CommandText = "UPDATE t SET n=1 RETURNING ARRAY[n]";
            validationBatch.BatchCommands.Add(returning); validationBatch.BatchCommands.Add(new("UPDATE t SET n=2"));
            try { await validationBatch.ExecuteNonQueryAsync(token); throw new InvalidOperationException("Batch accepted unsupported output."); }
            catch (NotSupportedException) { }
            Require(returning.ExecutionStatus == KingbaseBatchCommandStatus.Succeeded && returning.RecordsAffected == 1);
            Require(validationBatch.FailedCommandIndex == 0 && validationBatch.BatchCommands[1].ExecutionStatus == KingbaseBatchCommandStatus.NotExecuted);
            returning.CommandText = "SELECT 'infinity'::date";
            try { await validationBatch.ExecuteScalarAsync(token); throw new InvalidOperationException("Batch accepted an unrepresentable scalar."); }
            catch (OverflowException) { }
            Require(validationBatch.FailedCommandIndex == 0 && returning.ExecutionStatus == KingbaseBatchCommandStatus.Succeeded);
            Require(validationBatch.BatchCommands[1].ExecutionStatus == KingbaseBatchCommandStatus.NotExecuted);
            command.CommandText = "SELECT 42"; Require((int)(await command.ExecuteScalarAsync(token))! == 42);
            connection.Close(); await peer.WaitAsync(token); return;
        }
        if (interval)
        {
            command.Parameters.AddWithValue("p", new KingbaseInterval(14, -3, -1));
            foreach (var (_, value) in IntervalValues)
            {
                var duration = value.StartsWith("PT", StringComparison.Ordinal);
                command.Parameters[0].Value = duration ? TimeSpan.FromTicks(-10) : new KingbaseInterval(14, -3, -1);
                using var reader = await command.ExecuteReaderAsync(token); Require(reader.Read());
                var expected = duration ? new KingbaseInterval(0, 0, -1) : new KingbaseInterval(14, -3, -1);
                Require(reader.GetFieldType(0) == typeof(KingbaseInterval)); Require(reader.GetProviderSpecificFieldType(0) == typeof(KingbaseInterval));
                Require(reader.GetFieldValue<KingbaseInterval>(0) == expected); Require(Equals(reader.GetValue(0), reader.GetProviderSpecificValue(0)));
                if (duration)
                {
                    Require(reader.GetFieldValue<TimeSpan>(0).Ticks == -10);
                    try { reader.GetFieldValue<TimeOnly>(0); throw new InvalidOperationException("INTERVAL was silently treated as a clock."); }
                    catch (InvalidCastException) { }
                }
                else
                {
                    try { reader.GetFieldValue<TimeSpan>(0); throw new InvalidOperationException("Calendar months were silently converted."); }
                    catch (InvalidCastException) { }
                }
            }
            command.Parameters[0].KingbaseDbType = KingbaseDbType.Interval; command.Parameters[0].Value = DBNull.Value;
            using (var reader = await command.ExecuteReaderAsync(token)) { Require(reader.Read()); Require(reader.IsDBNull(0)); Require(reader.GetValue(0) is DBNull); }
            connection.Close(); await peer.WaitAsync(token); return;
        }
        if (timestamp)
        {
            foreach (var zoned in new[] { false, true })
            {
                command.Parameters.Clear();
                command.Parameters.Add(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = zoned ? KingbaseDbType.TimestampTz : KingbaseDbType.Timestamp });
                foreach (var value in TimestampValues(zoned))
                {
                    command.Parameters[0].Value = zoned ? KingbaseTimestampTz.Parse(value) : KingbaseTimestamp.Parse(value);
                    using var reader = await command.ExecuteReaderAsync(token); Require(reader.Read());
                    Require(reader.GetFieldType(0) == typeof(DateTime));
                    Require(reader.GetProviderSpecificFieldType(0) == (zoned ? typeof(KingbaseTimestampTz) : typeof(KingbaseTimestamp)));
                    Require(reader.GetProviderSpecificValue(0).ToString() == value);
                    if (value.StartsWith("2024", StringComparison.Ordinal))
                    {
                        Require(reader.GetDateTime(0).Ticks == new DateTime(2024, 2, 29, 12, 34, 56).AddTicks(1234560).Ticks);
                        Require(reader.GetDateTime(0).Kind == (zoned ? DateTimeKind.Utc : DateTimeKind.Unspecified));
                        if (zoned) Require(reader.GetFieldValue<DateTimeOffset>(0).Offset == TimeSpan.Zero);
                    }
                    else
                    {
                        try { reader.GetValue(0); throw new InvalidOperationException("A non-CLR timestamp was silently converted."); }
                        catch (OverflowException) { }
                    }
                }
            }
            connection.Close(); await peer.WaitAsync(token); return;
        }
        if (time)
        {
            foreach (var zoned in new[] { false, true })
            {
                command.Parameters.Clear();
                command.Parameters.Add(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = zoned ? KingbaseDbType.TimeTz : KingbaseDbType.Time });
                foreach (var value in TimeValues(zoned))
                {
                    var expectedTime = value.StartsWith("24", StringComparison.Ordinal) ? TimeSpan.FromDays(1) : TimeSpan.FromTicks(10);
                    command.Parameters[0].Value = zoned ? KingbaseTimeTz.Parse(value) : expectedTime;
                    using var reader = await command.ExecuteReaderAsync(token); Require(reader.Read());
                    Require(reader.GetFieldType(0) == (zoned ? typeof(KingbaseTimeTz) : typeof(TimeSpan)));
                    if (zoned) Require(reader.GetFieldValue<KingbaseTimeTz>(0).ToString() == value);
                    else Require(reader.GetFieldValue<TimeSpan>(0) == expectedTime);
                    Require(Equals(reader.GetValue(0), reader.GetProviderSpecificValue(0)));
                }
            }
            connection.Close(); await peer.WaitAsync(token); return;
        }
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
    static readonly (string Style, string Text)[] IntervalValues =
    [
        ("kingbase", "1 year 2 mons -3 days -00:00:00.000001"),
        ("kingbase_verbose", "@ 1 year 2 mons -3 days -0.000001 secs"),
        ("sql_standard", "+1-2 -3 -0:00:00.000001"),
        ("iso_8601", "P1Y2M-3DT-0.000001S"),
        ("iso_8601", "PT-0.000001S")
    ];
    static string[] TimeValues(bool zoned) => zoned ? ["00:00:00.000001-00:00:01", "24:00:00+15:59:59"] : ["00:00:00.000001", "24:00:00"];
    static string[] TimestampValues(bool zoned) => ["2024-02-29 12:34:56.123456" + (zoned ? "+00" : ""),
        "0001-02-29 12:34:56.123456" + (zoned ? "+00" : "") + " BC", "infinity", "-infinity"];

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
    static byte[] Row(params string?[] values) => Frame('D', Join(Count(values.Length), Join(values.Select(value =>
        value is null ? Int32(-1) : Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray())));
    static byte[] Description(int count, int oid = 25, int size = -1) => Frame('T', Join(Count(count), Join(Enumerable.Range(0, count)
        .Select(i => Join(Encoding.UTF8.GetBytes("c" + i + "\0"), new byte[6], Int32(oid), Count(size), Int32(-1), Count(0))).ToArray())));
}
