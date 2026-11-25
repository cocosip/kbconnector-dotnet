using System.Buffers.Binary;
using System.Collections;
using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using KingbaseConnector;
using static KingbaseConnector.Tests.PreparedAssetProbe;

namespace KingbaseConnector.Tests;

// Public package APIs only: selected unchanged for every archived TFM asset.
internal static class NativeScalarAssetProbe
{
    sealed record Spec(string Schema, string Name, int Size, string Category, string Input = "", string Output = "", string Kind = "b", string? Base = null);
    sealed record Value(KingbaseDbType Provider, object Input, string Key, object Expected, string? Text, object? ProviderExpected = null, Type? NullType = null);
    internal static async Task RunAsync(KingbaseCompatibilityMode mode)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var token = guard.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var wireMode = mode switch { KingbaseCompatibilityMode.Oracle => "oracle", KingbaseCompatibilityMode.MySql => "mysql", KingbaseCompatibilityMode.SqlServer => "sqlserver", _ => "pg" };
        var definitions = Definitions(mode); var values = Values(mode);
        Check(KingbaseMySqlDate.Parse("0000-00-00").IsZero && KingbaseMySqlDateTime.Parse("2024-00-15 13:14:15.123456").ToString() == "2024-00-15 13:14:15.123456");
        try { KingbaseMySqlDate.Parse("10000-01-01").ToDateTime(); throw new InvalidOperationException("A non-CLR date was narrowed."); } catch (OverflowException) { }
        string[] baseline = ["db", "user", "user", wireMode, "UTF8", "on", "native_probe", "pg_catalog, public", "UTC",
            "read committed", "off", "off", "ISO, MDY", "postgres", "read committed", "off", ""];
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(token); using var stream = socket.GetStream();
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token); var length = BinaryPrimitives.ReadInt32BigEndian(header);
            Check(length is >= 8 and <= 4096); await stream.ReadExactlyAsync(new byte[length - 4], token);
            await stream.WriteAsync(Join(Frame('R', Int32(0)), Status("client_encoding", "UTF8"), Status("server_version", "14"),
                Status("database_mode", wireMode), Status("standard_conforming_strings", "on"), Status("DateStyle", "ISO, MDY"), Frame('K', Join(Int32(1), Int32(2))), Ready()), token);
            await Identity(stream); await Catalog(stream, 0); await State(stream);
            for (var borrower = 0; borrower < 2; borrower++)
            {
                if (borrower != 0) { await Query(stream, "SELECT 1"); await stream.WriteAsync(Join(Description(1), Row("1"), Complete(), Ready()), token); }
                foreach (var value in values)
                {
                    var spec = definitions.Single(type => type.Schema + "." + type.Name == value.Key); var oid = Oid(spec, borrower);
                    var frames = await ReadFrames(stream, "PBDES", token);
                    Check(frames[0].SequenceEqual(Frame('P', Join([0], Encoding.UTF8.GetBytes("SELECT $1\0"), Count(1), Int32(oid)))));
                    var text = value.Text is null ? [] : Encoding.UTF8.GetBytes(value.Text);
                    Check(frames[1].SequenceEqual(Frame('B', Join([0, 0], Count(0), Count(1), Int32(value.Text is null ? -1 : text.Length), text, Count(0)))));
                    await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, oid, spec.Size), Row(value.Text), Complete(), Ready()), token);
                }
                await Query(stream, "DISCARD ALL"); await stream.WriteAsync(Join(Frame('C', Encoding.ASCII.GetBytes("DISCARD ALL\0")), Ready()), token);
                await Catalog(stream, borrower + 1);
                var restore = await ReadFrames(stream, "PBDES", token); var textOid = Oid(definitions.Single(type => type.Name == "text"), borrower + 1);
                Check(restore[0].SequenceEqual(Frame('P', Join([0], Encoding.UTF8.GetBytes(PoolAssetProbe.RestoreSql + "\0"), Count(10), Join(Enumerable.Repeat(Int32(textOid), 10).ToArray())))));
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(10), Row(baseline[4..14]), Complete(), Ready()), token);
                await State(stream); await Identity(stream);
            }
            Check(await stream.ReadAsync(new byte[1], token) == 0);
        }, token);
        var settings = new KingbaseConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "user", Database = "db", SslMode = KingbaseSslMode.Disable,
            Pooling = true, MaxPoolSize = 1, CompatibilityMode = mode, ApplicationName = baseline[6], SearchPath = baseline[7], Timezone = baseline[8]
        };
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build(); using var connection = source.CreateConnection();
        using var command = new KingbaseCommand("SELECT :p", connection);
        for (var borrower = 0; borrower < 2; borrower++)
        {
            await connection.OpenAsync(token);
            foreach (var value in values)
            {
                Check(Enum.IsDefined(typeof(KingbaseDbType), value.Provider)); command.Parameters.Clear();
                var parameter = new KingbaseParameter("p", value.Input);
                if (borrower == 0 || value.Input is DBNull || value.Provider is not (KingbaseDbType.Int128 or KingbaseDbType.SignedTinyint or KingbaseDbType.UnsignedTinyint or KingbaseDbType.UnsignedBigint or KingbaseDbType.Varbit or KingbaseDbType.MySqlDate or KingbaseDbType.MySqlDateTime))
                    parameter.KingbaseDbType = value.Provider;
                if (value.Provider == KingbaseDbType.UnsignedInteger) parameter.DbType = DbType.UInt32;
                if (value.Provider == KingbaseDbType.UnsignedBigint) parameter.DbType = DbType.UInt64;
                if (borrower != 0 && value.Provider == KingbaseDbType.SignedTinyint) parameter.DbType = DbType.SByte;
                if (borrower != 0 && value.Provider == KingbaseDbType.UnsignedTinyint) parameter.DbType = DbType.Byte;
                command.Parameters.Add(parameter);
                using var reader = await command.ExecuteReaderAsync(token); Check(reader.Read());
                var expectedType = value.Input is DBNull ? value.NullType ?? typeof(BitArray) : value.Expected.GetType();
                Check(reader.GetFieldType(0) == expectedType && SameValue(value.Expected, reader.GetValue(0)) && SameValue(value.ProviderExpected ?? value.Expected, reader.GetProviderSpecificValue(0)));
                if (value.Expected is BitArray expectedBits) Check(expectedBits.Cast<bool>().SequenceEqual(reader.GetFieldValue<BitArray>(0).Cast<bool>()));
                if (value.Expected is BigInteger) Check(reader.GetFieldValue<BigInteger>(0) == (BigInteger)value.Expected);
                if (value.ProviderExpected is { } providerValue) Check(reader.GetProviderSpecificFieldType(0) == providerValue.GetType());
                if (value.ProviderExpected is KingbaseMySqlDate mySqlDate) Check(reader.GetFieldValue<KingbaseMySqlDate>(0) == mySqlDate);
                if (value.ProviderExpected is KingbaseMySqlDateTime mySqlCalendar) Check(reader.GetFieldValue<KingbaseMySqlDateTime>(0) == mySqlCalendar);
                if (value.Input is uint unsignedInteger) Check(reader.GetFieldValue<uint>(0) == unsignedInteger);
                if (value.Input is ulong unsignedLong) Check(reader.GetFieldValue<ulong>(0) == unsignedLong);
                Check(reader.GetSchemaTable()!.Rows[0]["DataType"].Equals(expectedType));
            }
            command.Parameters.Clear(); command.Parameters.Add(new KingbaseParameter("p", BigInteger.One << 127) { KingbaseDbType = KingbaseDbType.Int128 });
            try { await command.ExecuteScalarAsync(token); throw new InvalidOperationException("Wide input overflow was not rejected."); } catch (OverflowException) { }
            command.Parameters.Clear(); command.Parameters.Add(new KingbaseParameter("p", new BitArray(1024 * 1024 + 1)));
            try { await command.ExecuteScalarAsync(token); throw new InvalidOperationException("Oversized bits were not rejected."); } catch (ArgumentOutOfRangeException) { }
            command.Parameters.Clear(); command.Parameters.Add(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = KingbaseDbType.UnsignedTinyint, DbType = DbType.SByte });
            try { await command.ExecuteScalarAsync(token); throw new InvalidOperationException("Conflicting NULL selectors were not rejected."); } catch (ArgumentException) { }
            if (mode != KingbaseCompatibilityMode.PostgreSql)
            {
                var provider = mode == KingbaseCompatibilityMode.Oracle ? KingbaseDbType.OracleDate : mode == KingbaseCompatibilityMode.MySql ? KingbaseDbType.MySqlTime : KingbaseDbType.SqlServerDateTime;
                object input = mode == KingbaseCompatibilityMode.MySql ? TimeSpan.FromTicks(1) : new DateTime(2024, 2, 29).AddTicks(1);
                command.Parameters.Clear(); command.Parameters.Add(new KingbaseParameter("p", input) { KingbaseDbType = provider });
                try { await command.ExecuteScalarAsync(token); throw new InvalidOperationException("Lossy native temporal input was not rejected."); } catch (ArgumentException) { }
            }
            await connection.CloseAsync();
        }
        source.Dispose(); await peer.WaitAsync(token);

        int Oid(Spec spec, int generation) => 12000 + definitions.IndexOf(spec) + generation * 1000;
        async Task Query(Stream stream, string expected)
        { var frames = await ReadFrames(stream, "Q", token); Check(Encoding.UTF8.GetString(frames[0].AsSpan(5)) == expected + "\0"); }
        async Task Identity(Stream stream)
        { await Query(stream, "SELECT pg_catalog.version();"); await stream.WriteAsync(Join(Description(1), Row("KingbaseES V009R001C001B0001"), Complete(), Ready()), token); }
        async Task State(Stream stream)
        { await Query(stream, PoolAssetProbe.StateSql); await stream.WriteAsync(Join(Description(17), Row(baseline), Complete(), Ready()), token); }
        async Task Catalog(Stream stream, int generation)
        {
            foreach (var schema in mode == KingbaseCompatibilityMode.PostgreSql ? new[] { "pg_catalog" } : new[] { "pg_catalog", "sys" })
            {
                var query = await ReadFrames(stream, "Q", token); var sql = Encoding.UTF8.GetString(query[0]);
                Check(sql.Contains("FROM sys_catalog.sys_type", StringComparison.Ordinal) && sql.Contains("pg_catalog.nameeq", StringComparison.Ordinal));
                if (schema == "sys") Check(sql.Contains("pg_catalog.pg_roles", StringComparison.Ordinal));
                var selected = definitions.Where(type => type.Schema == schema).ToArray();
                var rows = selected.Select(spec => Row(new[] { Oid(spec, generation).ToString(CultureInfo.InvariantCulture), schema == "sys" ? "91" : "90", schema,
                    spec.Name, spec.Size.ToString(CultureInfo.InvariantCulture), spec.Kind, spec.Category, "t", spec.Base is null ? "0" : Oid(definitions.Single(type => type.Schema + "." + type.Name == spec.Base), generation).ToString(CultureInfo.InvariantCulture), "0", "0", "-1" }
                    .Concat(schema == "sys" ? new[] { spec.Input, spec.Output, "true", "true" } : []).ToArray())).ToArray();
                await stream.WriteAsync(Join(Description(schema == "sys" ? 16 : 12), Join(rows), Frame('C', Encoding.ASCII.GetBytes("SELECT " + rows.Length.ToString(CultureInfo.InvariantCulture) + "\0")), Ready()), token);
            }
        }
    }
    static List<Spec> Definitions(KingbaseCompatibilityMode mode)
    {
        var result = new List<Spec> { new("pg_catalog", "bool", 1, "B"), new("pg_catalog", "int2", 2, "N"), new("pg_catalog", "int4", 4, "N"),
            new("pg_catalog", "int8", 8, "N"), new("pg_catalog", "text", -1, "S"), new("pg_catalog", "bytea", -1, "U"), new("pg_catalog", "int16", 16, "N"),
            new("pg_catalog", "bit", -1, "V"), new("pg_catalog", "varbit", -1, "V"),
            new("pg_catalog", "date", 4, "D"), new("pg_catalog", "time", 8, "D"), new("pg_catalog", "timestamp", 8, "D") };
        if (mode == KingbaseCompatibilityMode.Oracle)
        {
            result.Add(new("pg_catalog", "tinyint", 1, "N"));
            result.Add(new("sys", "date", 8, "D", "pg_catalog.ora_date_in", "sys.ora_date_out", "d", "pg_catalog.timestamp"));
        }
        if (mode != KingbaseCompatibilityMode.PostgreSql)
        {
            result.Add(new("sys", "varcharbyte", -1, "S", "sys.varcharbytein", "sys.varcharbyteout"));
            result.Add(new("sys", "bpcharbyte", -1, "S", "sys.bpcharbytein", "sys.bpcharbyteout"));
        }
        if (mode == KingbaseCompatibilityMode.MySql)
        {
            result.Add(new("sys", "tinyint", 1, "N", "pg_catalog.tinyintin", "pg_catalog.tinyintout"));
            result.Add(new("sys", "uint4", 4, "N", "pg_catalog.uint4in", "pg_catalog.uint4out")); result.Add(new("sys", "uint8", 8, "N", "pg_catalog.uint8in", "pg_catalog.uint8out"));
            foreach (var name in new[] { "date", "datetime", "timestamp", "time" }) result.Add(new("sys", name, name == "date" ? 4 : 8, "D", "sys.mysql_" + name + "_in", "sys.mysql_" + name + "_out"));
            result.Add(new("sys", "year", 4, "N", "pg_catalog.domain_in", "pg_catalog.int4out", "d", "pg_catalog.int4"));
        }
        if (mode == KingbaseCompatibilityMode.SqlServer)
        {
            result.Add(new("sys", "tinyint", 1, "N", "sys.sqlserver_tinyint_in", "sys.sqlserver_tinyint_out"));
            result.Add(new("sys", "bit", 1, "B", "sys.sqlserver_bit_in", "sys.sqlserver_bit_out"));
            result.Add(new("sys", "nchar", -1, "S", "sys.nchar_in", "sys.nchar_out")); result.Add(new("sys", "nvarchar", -1, "S", "sys.nvarchar_in", "sys.nvarchar_out"));
            foreach (var name in new[] { "date", "datetime", "datetime2", "smalldatetime", "time" }) result.Add(new("sys", name, name == "date" ? 4 : 8, "D", "sys.sqlserver_" + name + "_in", "sys.sqlserver_" + name + "_out"));
        }
        return result;
    }
    static List<Value> Values(KingbaseCompatibilityMode mode)
    {
        var result = new List<Value>(); var min = -(BigInteger.One << 127); var high = (BigInteger.One << 96) + 7;
        result.Add(new(KingbaseDbType.Int128, min, "pg_catalog.int16", min, min.ToString(CultureInfo.InvariantCulture)));
        result.Add(new(KingbaseDbType.Int128, high, "pg_catalog.int16", high, high.ToString(CultureInfo.InvariantCulture)));
        foreach (var (provider, text) in new[] { (KingbaseDbType.Bit, "00101"), (KingbaseDbType.Varbit, ""), (KingbaseDbType.Varbit, "10000000100000001") })
        { var bits = new BitArray(text.Select(c => c == '1').ToArray()); result.Add(new(provider, bits, "pg_catalog." + provider.ToString().ToLowerInvariant(), bits, text)); }
        result.Add(new(KingbaseDbType.Varbit, DBNull.Value, "pg_catalog.varbit", DBNull.Value, null));
        result.Add(new(KingbaseDbType.SignedTinyint, sbyte.MinValue, mode == KingbaseCompatibilityMode.Oracle ? "pg_catalog.tinyint" : mode == KingbaseCompatibilityMode.MySql ? "sys.tinyint" : "pg_catalog.int2",
            mode is KingbaseCompatibilityMode.Oracle or KingbaseCompatibilityMode.MySql ? sbyte.MinValue : (object)(short)-128, "-128"));
        result.Add(new(KingbaseDbType.UnsignedTinyint, byte.MaxValue, mode == KingbaseCompatibilityMode.SqlServer ? "sys.tinyint" : mode == KingbaseCompatibilityMode.MySql ? "sys.uint4" : "pg_catalog.int2",
            mode == KingbaseCompatibilityMode.SqlServer ? byte.MaxValue : mode == KingbaseCompatibilityMode.MySql ? (object)255U : (short)255, "255"));
        result.Add(new(KingbaseDbType.UnsignedInteger, uint.MaxValue, mode == KingbaseCompatibilityMode.MySql ? "sys.uint4" : "pg_catalog.int8",
            mode == KingbaseCompatibilityMode.MySql ? uint.MaxValue : (object)4294967295L, "4294967295"));
        result.Add(new(KingbaseDbType.UnsignedBigint, ulong.MaxValue, mode == KingbaseCompatibilityMode.MySql ? "sys.uint8" : "pg_catalog.int16",
            mode == KingbaseCompatibilityMode.MySql ? ulong.MaxValue : (object)new BigInteger(ulong.MaxValue), "18446744073709551615"));
        if (mode != KingbaseCompatibilityMode.PostgreSql)
        { const string text = "汉😀 ' \\  "; result.Add(new(KingbaseDbType.VarcharByte, text, "sys.varcharbyte", text, text)); result.Add(new(KingbaseDbType.CharByte, text, "sys.bpcharbyte", text, text)); }
        if (mode == KingbaseCompatibilityMode.SqlServer)
        { result.Add(new(KingbaseDbType.SqlServerBit, true, "sys.bit", true, "1")); result.Add(new(KingbaseDbType.NChar, "nchar  ", "sys.nchar", "nchar  ", "nchar  ")); result.Add(new(KingbaseDbType.NVarchar, "nvarchar", "sys.nvarchar", "nvarchar", "nvarchar")); }
        var date = new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Unspecified); var clock = date.AddHours(13).AddMinutes(14).AddSeconds(15);
        if (mode == KingbaseCompatibilityMode.Oracle) result.Add(new(KingbaseDbType.OracleDate, clock, "sys.date", clock, "2024-02-29 13:14:15", KingbaseTimestamp.FromDateTime(clock)));
        if (mode == KingbaseCompatibilityMode.SqlServer)
        {
            result.Add(new(KingbaseDbType.SqlServerDate, date, "sys.date", date, "2024-02-29", new KingbaseDate(2024, 2, 29)));
            result.Add(new(KingbaseDbType.SqlServerDateTime, clock.AddMilliseconds(123), "sys.datetime", clock.AddMilliseconds(123), "2024-02-29 13:14:15.123"));
            result.Add(new(KingbaseDbType.SqlServerDateTime2, clock.AddTicks(1234567), "sys.datetime2", clock.AddTicks(1234567), "2024-02-29 13:14:15.1234567"));
            result.Add(new(KingbaseDbType.SqlServerSmallDateTime, date.AddHours(13), "sys.smalldatetime", date.AddHours(13), "2024-02-29 13:00:00"));
            result.Add(new(KingbaseDbType.SqlServerTime, TimeSpan.FromTicks(TimeSpan.TicksPerDay - 1), "sys.time", TimeSpan.FromTicks(TimeSpan.TicksPerDay - 1), "23:59:59.9999999"));
        }
        if (mode == KingbaseCompatibilityMode.MySql)
        {
            var day = new KingbaseMySqlDate(2024, 2, 29); var value = KingbaseMySqlDateTime.FromDateTime(clock.AddTicks(1234560));
            result.Add(new(KingbaseDbType.MySqlDate, day, "sys.date", date, "2024-02-29", day));
            result.Add(new(KingbaseDbType.MySqlDateTime, value, "sys.datetime", clock.AddTicks(1234560), "2024-02-29 13:14:15.123456", value));
            result.Add(new(KingbaseDbType.MySqlTimestamp, clock, "sys.timestamp", clock, "2024-02-29 13:14:15", KingbaseMySqlDateTime.FromDateTime(clock)));
            result.Add(new(KingbaseDbType.MySqlTime, -TimeSpan.FromHours(25) - TimeSpan.FromTicks(10), "sys.time", -TimeSpan.FromHours(25) - TimeSpan.FromTicks(10), "-25:00:00.000001"));
            result.Add(new(KingbaseDbType.MySqlYear, 1901, "sys.year", 1901, "1901"));
        }
        foreach (var native in result.Where(value => (int)value.Provider >= (int)KingbaseDbType.OracleDate).ToArray())
            result.Add(new(native.Provider, DBNull.Value, native.Key, DBNull.Value, null, NullType: native.Expected.GetType()));
        return result;
    }
    static bool SameValue(object expected, object actual) => expected is BitArray bits ? actual is BitArray other && bits.Cast<bool>().SequenceEqual(other.Cast<bool>()) : Equals(expected, actual);
    static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Exact-asset native scalar probe failed."); }
}
