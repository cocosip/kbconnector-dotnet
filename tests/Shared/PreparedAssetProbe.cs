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
    internal static async Task RunAsync(bool automatic = false, bool date = false, bool time = false, bool timestamp = false, bool interval = false, bool batch = false, bool xml = false, bool schema = false, bool readerSchema = false)
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
            var names = new[] { "bool", "int2", "int4", "int8", "text", "bytea", "date", "time", "timetz", "timestamp", "timestamptz", "interval", "xml", "numeric", "varchar", "bpchar" };
            var sizes = new[] { "1", "2", "4", "8", "-1", "-1", "4", "8", "12", "8", "8", "16", "-1", "-1", "-1", "-1" };
            var rows = names.Select((name, i) => Row((12000 + i).ToString(CultureInfo.InvariantCulture), "90", "pg_catalog", name,
                sizes[i], "b", "U", "t", "0", "0", "0", "-1")).ToArray();
            await stream.WriteAsync(Join(Description(12), Join(rows), Complete(), Ready()), token);
            if (readerSchema)
            {
                foreach (var (oid, modifier) in new[] { (12014, 21), (12015, 9), (12013, 786439), (12013, 329730), (12013, -1) })
                    for (var empty = 0; empty < 2; empty++)
                    {
                        await ReadFrames(stream, "PBDES", token);
                        var description = Frame('T', Join(Count(1), Encoding.UTF8.GetBytes("alias\0"), new byte[6], Int32(oid), Count(-1), Int32(modifier), Count(0)));
                        await stream.WriteAsync(Join(Frame('1'), Frame('2'), description, empty == 0 ? Row((string?)null) : [], Complete(), Ready()), token);
                    }
                Require(await stream.ReadAsync(new byte[1], token) == 0); return;
            }
            if (schema)
            {
                for (var i = 0; i < 2; i++)
                {
                    var query = await ReadFrames(stream, "PBDES", token);
                    Require(Encoding.UTF8.GetString(query[0]).Contains("FROM pg_catalog.pg_get_keywords() WHERE catcode = 'R'", StringComparison.Ordinal));
                    await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 12004, -1), Row("select"), Row("where"), Complete(), Ready()), token);
                }
                foreach (var name in new[] { "schemata", "tables", "views", "columns", "schemata" })
                {
                    var query = await ReadFrames(stream, "PBDES", token);
                    var sql = Encoding.UTF8.GetString(query[0]);
                    Require(sql.Contains("FROM information_schema." + name, StringComparison.Ordinal));
                    Require(sql.Contains("COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) $1", StringComparison.Ordinal));
                    Require(!sql.Contains("MiXeD汉%_'", StringComparison.Ordinal));
                    Require(Encoding.UTF8.GetString(query[1]).Contains("MiXeD汉%_'", StringComparison.Ordinal));
                    var fieldNames = name == "schemata" ? new[] { "CATALOG_NAME", "SCHEMA_NAME", "SCHEMA_OWNER" } :
                        name == "tables" ? ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE"] :
                        name == "views" ? ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "VIEW_DEFINITION", "CHECK_OPTION", "IS_UPDATABLE"] :
                        ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "ORDINAL_POSITION", "COLUMN_DEFAULT", "IS_NULLABLE", "DATA_TYPE",
                            "CHARACTER_MAXIMUM_LENGTH", "CHARACTER_OCTET_LENGTH", "NUMERIC_PRECISION", "NUMERIC_PRECISION_RADIX", "NUMERIC_SCALE",
                            "DATETIME_PRECISION", "INTERVAL_TYPE", "INTERVAL_PRECISION", "UDT_CATALOG", "UDT_SCHEMA", "UDT_NAME", "DOMAIN_CATALOG",
                            "DOMAIN_SCHEMA", "DOMAIN_NAME", "COLLATION_CATALOG", "COLLATION_SCHEMA", "COLLATION_NAME", "IS_IDENTITY", "IDENTITY_GENERATION",
                            "IDENTITY_START", "IDENTITY_INCREMENT", "IDENTITY_MAXIMUM", "IDENTITY_MINIMUM", "IDENTITY_CYCLE", "IS_GENERATED", "GENERATION_EXPRESSION"];
                    var width = fieldNames.Length;
                    var objectHeader = Frame('T', Join(Count(width), Join(Enumerable.Range(0, width).Select(i =>
                    {
                        var integer = name == "columns" && (i == 4 || i is >= 10 and <= 13 || i == 15);
                        var length = name == "columns" && (i is 8 or 9 or >= 27 and <= 30);
                        Require(sql.Contains(" AS \"" + fieldNames[i] + "\"", StringComparison.Ordinal));
                        return Join(Encoding.UTF8.GetBytes(fieldNames[i] + '\0'), new byte[6], Int32(integer ? 12002 : length ? 12003 : 12004),
                            Count(integer ? 4 : length ? 8 : -1), Int32(-1), Count(0));
                    }).ToArray())));
                    var values = name == "schemata" ? new string?[] { "db", "MiXeD汉%_'", "owner" } : name == "tables" ? ["db", "MiXeD汉%_'", "object", "BASE TABLE"] :
                        name == "views" ? ["db", "MiXeD汉%_'", "object", null, "NONE", "NO"] :
                        ["db", "MiXeD汉%_'", "object", "column", "1", null, "NO", "bigint", null, null, "64", "2", "0", null, null, null,
                            "db", "pg_catalog", "int8", null, null, null, null, null, null,
                            "YES", "ALWAYS", "-2147483649", "-2", "-1", "-9223372036854775808", "YES", "NEVER", null];
                    await stream.WriteAsync(Join(Frame('1'), Frame('2'), objectHeader, Row(values), Complete(), Ready()), token);
                }
                var sequenceQuery = await ReadFrames(stream, "PBDES", token);
                var sequenceSql = Encoding.UTF8.GetString(sequenceQuery[0]);
                Require(sequenceSql.Contains("FROM pg_catalog.pg_sequence", StringComparison.Ordinal));
                Require(sequenceSql.Contains("NOT pg_catalog.pg_is_other_temp_schema", StringComparison.Ordinal));
                Require(sequenceSql.Contains("COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) $1", StringComparison.Ordinal));
                Require(!sequenceSql.Contains("MiXeD汉%_'", StringComparison.Ordinal));
                Require(Encoding.UTF8.GetString(sequenceQuery[1]).Contains("MiXeD汉%_'", StringComparison.Ordinal));
                var sequenceHeader = Frame('T', Join(Count(SequenceFields.Length), Join(SequenceFields.Select(field =>
                    Join(Encoding.UTF8.GetBytes(field.Name + '\0'), new byte[6], Int32(field.Type == typeof(int) ? 12002 : field.Type == typeof(long) ? 12003 : 12004),
                        Count(field.Type == typeof(int) ? 4 : field.Type == typeof(long) ? 8 : -1), Int32(-1), Count(0))).ToArray())));
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), sequenceHeader, Row(SequenceValues), Complete(), Ready()), token);
                foreach (var name in RelationalNames)
                {
                    var query = await ReadFrames(stream, "PBDES", token);
                    var sql = Encoding.UTF8.GetString(query[0]);
                    Require(sql.Contains("pg_catalog.pg_class", StringComparison.Ordinal));
                    Require(sql.Contains("COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) $1", StringComparison.Ordinal));
                    Require(!sql.Contains("MiXeD汉%_'", StringComparison.Ordinal));
                    Require(Encoding.UTF8.GetString(query[1]).Contains("MiXeD汉%_'", StringComparison.Ordinal));
                    var fields = RelationalFields(name);
                    var relationalHeader = Frame('T', Join(Count(fields.Length), Join(fields.Select(field =>
                        Join(Encoding.UTF8.GetBytes(field.Name + '\0'), new byte[6], Int32(field.Integer ? 12002 : 12004),
                            Count(field.Integer ? 4 : -1), Int32(-1), Count(0))).ToArray())));
                    await stream.WriteAsync(Join(Frame('1'), Frame('2'), relationalHeader, Row(RelationalValues(name)), Complete(), Ready()), token);
                }
                foreach (var name in new[] { "Databases", "Users" })
                {
                    var query = await ReadFrames(stream, "PBDES", token); var sql = Encoding.UTF8.GetString(query[0]);
                    Require(sql.Contains(name == "Databases" ? "pg_catalog.pg_database" : "pg_catalog.pg_roles", StringComparison.Ordinal));
                    Require(sql.Contains("COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) $1", StringComparison.Ordinal));
                    Require(!sql.Contains("MiXeD汉%_'", StringComparison.Ordinal));
                    Require(Encoding.UTF8.GetString(query[1]).Contains("MiXeD汉%_'", StringComparison.Ordinal));
                    var fields = DirectoryFields(name);
                    var directoryHeader = Frame('T', Join(Count(fields.Length), Join(fields.Select(field =>
                        Join(Encoding.UTF8.GetBytes(field.Name + '\0'), new byte[6], Int32(field.Type == typeof(int) ? 12002 : 12004),
                            Count(field.Type == typeof(int) ? 4 : -1), Int32(-1), Count(0))).ToArray())));
                    await stream.WriteAsync(Join(Frame('1'), Frame('2'), directoryHeader, Row(DirectoryValues(name)), Complete(), Ready()), token);
                }
                foreach (var name in new[] { "Functions", "Procedures", "ProcedureParameters" })
                {
                    var query = await ReadFrames(stream, "PBDES", token); var sql = Encoding.UTF8.GetString(query[0]);
                    Require(sql.Contains("information_schema.routines", StringComparison.Ordinal));
                    Require(sql.Contains("NOT pg_catalog.pg_is_other_temp_schema", StringComparison.Ordinal));
                    Require(!sql.Contains("MiXeD汉%_'", StringComparison.Ordinal));
                    Require(Encoding.UTF8.GetString(query[1]).Contains("MiXeD汉%_'", StringComparison.Ordinal));
                    var fields = RoutineFields(name);
                    var routineHeader = Frame('T', Join(Count(fields.Length), Join(fields.Select(field =>
                        Join(Encoding.UTF8.GetBytes(field.Name + '\0'), new byte[6],
                            Int32(field.Type == typeof(int) ? 12002 : field.Type == typeof(long) ? 12003 : 12004),
                            Count(field.Type == typeof(int) ? 4 : field.Type == typeof(long) ? 8 : -1), Int32(-1), Count(0))).ToArray())));
                    await stream.WriteAsync(Join(Frame('1'), Frame('2'), routineHeader, Row(RoutineValues(name)), Complete(), Ready()), token);
                }
                foreach (var name in UdtSchemaFixtures.Names)
                {
                    var query = await ReadFrames(stream, "PBDES", token); var sql = Encoding.UTF8.GetString(query[0]);
                    Require(sql.Contains("pg_catalog.pg_type", StringComparison.Ordinal));
                    Require(sql.Contains("NOT pg_catalog.pg_is_other_temp_schema(t.typnamespace)", StringComparison.Ordinal));
                    Require(!sql.Contains("MiXeD汉%_'", StringComparison.Ordinal));
                    Require(Encoding.UTF8.GetString(query[1]).Contains("MiXeD汉%_'", StringComparison.Ordinal));
                    var fields = UdtSchemaFixtures.Fields(name);
                    var udtHeader = Frame('T', Join(Count(fields.Length), Join(fields.Select(field =>
                        Join(Encoding.UTF8.GetBytes(field.Name + '\0'), new byte[6], Int32(field.Type == typeof(int) ? 12002 : field.Type == typeof(long) ? 12003 : 12004),
                            Count(field.Type == typeof(int) ? 4 : field.Type == typeof(long) ? 8 : -1), Int32(-1), Count(0))).ToArray())));
                    await stream.WriteAsync(Join(Frame('1'), Frame('2'), udtHeader, Row(UdtSchemaFixtures.Values(name)), Complete(), Ready()), token);
                }
                Require(await stream.ReadAsync(new byte[1], token) == 0); return;
            }
            if (xml)
            {
                var preparation = await ReadFrames(stream, "PS", token);
                Require(preparation[0].AsSpan(preparation[0].Length - 4).SequenceEqual(Int32(12012)));
                await stream.WriteAsync(Join(Frame('1'), Ready()), token);
                foreach (var value in new string?[] { "<r>汉😀&amp;</r>", null, "<x/><y/>" })
                {
                    var execute = await ReadFrames(stream, "BDES", token);
                    Require(value is null ? BinaryPrimitives.ReadInt32BigEndian(execute[0].AsSpan(16, 4)) == -1 : Encoding.UTF8.GetString(execute[0]).Contains(value, StringComparison.Ordinal));
                    await stream.WriteAsync(Join(Frame('2'), Description(1, 12012, -1), Row(value), Complete(), Ready()), token);
                }
                await ReadFrames(stream, "BDES", token);
                await stream.WriteAsync(Join(Frame('2'), Frame('E', Encoding.UTF8.GetBytes("SERROR\0C2200N\0Minvalid XML\0\0")), Ready()), token);
                await ReadFrames(stream, "BDES", token);
                await stream.WriteAsync(Join(Frame('2'), Description(1, 12012, -1), Row("<recovered/>"), Complete(), Ready()), token);
                Require(await stream.ReadAsync(new byte[1], token) == 0); return;
            }
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
        if (readerSchema)
        {
            command.CommandText = "SELECT NULL";
            foreach (var (size, precision, scale) in new (int?, int?, int?)[] { (17, null, null), (5, null, null), (null, 12, 3), (null, 5, -2), (null, null, null) })
                for (var empty = 0; empty < 2; empty++)
                {
                    using DbDataReader reader = await command.ExecuteReaderAsync(token);
                    var column = ((IDbColumnSchemaGenerator)reader).GetColumnSchema().Single();
                    Require(column.ColumnSize == size && column.NumericPrecision == precision && column.NumericScale == scale);
                    using var table = reader.GetSchemaTable()!;
                    Require(Equals(table.Rows[0][System.Data.Common.SchemaTableColumn.ColumnSize], size is { } length ? length : DBNull.Value));
                    Require(Equals(table.Rows[0][System.Data.Common.SchemaTableColumn.NumericPrecision], precision is { } digits ? (short)digits : DBNull.Value));
                    Require(Equals(table.Rows[0][System.Data.Common.SchemaTableColumn.NumericScale], scale is { } fraction ? (short)fraction : DBNull.Value));
                    Require(column.AllowDBNull is null && column.IsKey is null && column.BaseTableName is null);
                    Require(reader.Read() == (empty == 0));
                    if (empty == 0) Require(reader.IsDBNull(0));
                }
            connection.Close(); await peer.WaitAsync(token); return;
        }
        if (schema)
        {
            using var collections = await connection.GetSchemaAsync(token); Require(collections.Rows.Count == 29);
            using var source = connection.GetSchema("DataSourceInformation");
            Require((string)source.Rows[0]["DataSourceProductVersion"] == connection.ServerVersion);
            using var restrictions = await connection.GetSchemaAsync("Restrictions", null, token);
            Require(restrictions.Columns.Count == 4 && restrictions.Rows.Count == 87);
            foreach (var alias in new[] { "Schemata", "PrimaryKey" })
            {
                var count = alias == "Schemata" ? 2 : 4;
                var entry = collections.Rows.Cast<System.Data.DataRow>().Single(row => (string)row[0] == alias);
                Require((int)entry[1] == count && (int)entry[2] == (count == 2 ? 2 : 5));
                var descriptors = restrictions.Rows.Cast<System.Data.DataRow>().Where(row => (string)row[0] == alias).ToArray();
                Require(descriptors.Select(row => (int)row[3]).SequenceEqual(Enumerable.Range(1, count)));
            }
            using var types = await ((DbConnection)connection).GetSchemaAsync("DataTypes", token);
            var integer = types.Rows.Cast<System.Data.DataRow>().Single(row => (string)row["TypeName"] == "int4");
            Require((uint)integer["TypeOid"] == 12002 && (int)integer["ProviderDbType"] == (int)KingbaseDbType.Integer);
            Require((string)integer["DataType"] == typeof(int).FullName);
            using var words = await connection.GetSchemaAsync("ReservedWords", [], token);
            Require(words.Rows.Count == 2 && (string)words.Rows[0][0] == "select");
            using var more = ((DbConnection)connection).GetSchema("ReservedWords"); Require(more.Rows.Count == 2);
            foreach (var name in new[] { "Schemas", "Tables", "Views", "Columns", "Schemata" })
            {
                using var objects = name is "Schemas" or "Views" ? connection.GetSchema(name, [null, "MiXeD汉%_'"]) :
                    await connection.GetSchemaAsync(name, [null, "MiXeD汉%_'"], token);
                Require(objects.Rows.Count == 1 && (string)objects.Rows[0][1] == "MiXeD汉%_'");
                Require(objects.TableName == (name == "Schemata" ? "Schemas" : name));
                if (name == "Columns")
                {
                    Require(objects.Columns.Count == 34 && (int)objects.Rows[0]["ORDINAL_POSITION"] == 1 && !(bool)objects.Rows[0]["IS_NULLABLE"] && objects.Rows[0]["COLUMN_DEFAULT"] is DBNull);
                    Require((bool)objects.Rows[0]["IS_IDENTITY"] && (string)objects.Rows[0]["IDENTITY_GENERATION"] == "ALWAYS");
                    Require((long)objects.Rows[0]["IDENTITY_START"] == -2147483649L && (long)objects.Rows[0]["IDENTITY_INCREMENT"] == -2);
                    Require((long)objects.Rows[0]["IDENTITY_MAXIMUM"] == -1 && (long)objects.Rows[0]["IDENTITY_MINIMUM"] == long.MinValue);
                    Require((bool)objects.Rows[0]["IDENTITY_CYCLE"] && (string)objects.Rows[0]["IS_GENERATED"] == "NEVER" && objects.Rows[0]["GENERATION_EXPRESSION"] is DBNull);
                }
                if (name == "Views") Require(objects.Rows[0]["VIEW_DEFINITION"] is DBNull && !(bool)objects.Rows[0]["IS_UPDATABLE"]);
            }
            using (var sequences = await connection.GetSchemaAsync("Sequences", [null, "MiXeD汉%_'"], token))
            {
                Require(sequences.Rows.Count == 1 && sequences.Columns.Count == SequenceFields.Length);
                for (var i = 0; i < SequenceFields.Length; i++)
                {
                    var field = SequenceFields[i]; var value = sequences.Rows[0][i];
                    Require(sequences.Columns[i].ColumnName == field.Name && sequences.Columns[i].DataType == field.Type);
                    Require(field.Type == typeof(int) ? (int)value == int.Parse(SequenceValues[i]!, CultureInfo.InvariantCulture) :
                        field.Type == typeof(long) ? (long)value == long.Parse(SequenceValues[i]!, CultureInfo.InvariantCulture) :
                        field.Type == typeof(bool) ? (bool)value == (SequenceValues[i] == "YES") : (string)value == SequenceValues[i]);
                }
            }
            foreach (var name in RelationalNames)
            {
                using var objects = name is "PrimaryKeys" or "ForeignKeys" or "Indexes" ? connection.GetSchema(name, [null, "MiXeD汉%_'"]) :
                    await connection.GetSchemaAsync(name, [null, "MiXeD汉%_'"], token);
                var fields = RelationalFields(name); var values = RelationalValues(name);
                Require(objects.Rows.Count == 1 && objects.Columns.Count == fields.Length);
                Require(objects.TableName == (name == "PrimaryKey" ? "PrimaryKeys" : name));
                for (var i = 0; i < fields.Length; i++)
                {
                    Require(objects.Columns[i].ColumnName == fields[i].Name);
                    Require(objects.Columns[i].DataType == (fields[i].Integer ? typeof(int) : fields[i].Flag ? typeof(bool) : typeof(string)));
                    Require(values[i] is null ? objects.Rows[0][i] is DBNull : fields[i].Integer ? (int)objects.Rows[0][i] == int.Parse(values[i]!, CultureInfo.InvariantCulture) :
                        fields[i].Flag ? (bool)objects.Rows[0][i] == (values[i] == "YES") : (string)objects.Rows[0][i] == values[i]);
                }
            }
            foreach (var name in new[] { "Databases", "Users" })
            {
                using var objects = name == "Databases" ? connection.GetSchema(name, ["MiXeD汉%_'"]) :
                    await connection.GetSchemaAsync(name, ["MiXeD汉%_'"], token);
                var fields = DirectoryFields(name); var values = DirectoryValues(name);
                Require(objects.TableName == name && objects.Rows.Count == 1 && objects.Columns.Count == fields.Length);
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i]; var value = objects.Rows[0][i];
                    Require(objects.Columns[i].ColumnName == field.Name && objects.Columns[i].DataType == field.Type);
                    Require(field.Type == typeof(int) ? (int)value == int.Parse(values[i]!, CultureInfo.InvariantCulture) :
                        field.Type == typeof(bool) ? (bool)value == (values[i] == "YES") : (string)value == values[i]);
                }
            }
            foreach (var name in new[] { "Functions", "Procedures", "ProcedureParameters" })
            {
                using var objects = name == "Functions" ? connection.GetSchema(name, [null, "MiXeD汉%_'"]) :
                    await connection.GetSchemaAsync(name, [null, "MiXeD汉%_'"], token);
                var fields = RoutineFields(name); var values = RoutineValues(name);
                Require(objects.TableName == name && objects.Rows.Count == 1 && objects.Columns.Count == fields.Length);
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i]; var value = objects.Rows[0][i];
                    Require(objects.Columns[i].ColumnName == field.Name && objects.Columns[i].DataType == field.Type);
                    Require(values[i] is null ? value is DBNull : field.Type == typeof(int) ? (int)value == int.Parse(values[i]!, CultureInfo.InvariantCulture) :
                        field.Type == typeof(long) ? (long)value == long.Parse(values[i]!, CultureInfo.InvariantCulture) :
                        field.Type == typeof(bool) ? (bool)value == (values[i] == "YES") : (string)value == values[i]);
                }
            }
            foreach (var name in UdtSchemaFixtures.Names)
            {
                using var objects = name == "EnumLabels" ? connection.GetSchema(name, [null, "MiXeD汉%_'", null, ""]) :
                    await connection.GetSchemaAsync(name, [null, "MiXeD汉%_'"], token);
                var fields = UdtSchemaFixtures.Fields(name); var values = UdtSchemaFixtures.Values(name);
                Require(objects.TableName == name && objects.Rows.Count == 1 && objects.Columns.Count == fields.Length);
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i]; var value = objects.Rows[0][i];
                    Require(objects.Columns[i].ColumnName == field.Name && objects.Columns[i].DataType == field.Type);
                    Require(values[i] is null ? value is DBNull : field.Type == typeof(int) ? (int)value == int.Parse(values[i]!, CultureInfo.InvariantCulture) :
                        field.Type == typeof(long) ? (long)value == long.Parse(values[i]!, CultureInfo.InvariantCulture) :
                        field.Type == typeof(bool) ? (bool)value == (values[i] == "YES") : (string)value == values[i]);
                }
            }
            try { connection.GetSchema("DataTypes", ["private-payload"]); throw new InvalidOperationException("Schema restrictions were ignored."); }
            catch (ArgumentException error) { Require(!error.Message.Contains("private-payload", StringComparison.Ordinal)); }
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await connection.GetSchemaAsync(canceled.Token); throw new InvalidOperationException("Schema cancellation was ignored."); }
            catch (OperationCanceledException) { }
            connection.Close(); await peer.WaitAsync(token); return;
        }
        if (xml)
        {
            var parameter = new KingbaseParameter("p", "<r>汉😀&amp;</r>") { DbType = System.Data.DbType.Xml };
            Require(parameter.KingbaseDbType == KingbaseDbType.Xml);
            command.Parameters.Add(parameter); await command.PrepareAsync(token);
            foreach (var value in new string?[] { "<r>汉😀&amp;</r>", null, "<x/><y/>" })
            {
                parameter.Value = value is null ? DBNull.Value : value;
                using var reader = await command.ExecuteReaderAsync(token);
                Require(reader.GetFieldType(0) == typeof(string) && reader.GetDataTypeName(0) == "xml");
                Require(reader.Read() && reader.IsDBNull(0) == (value is null));
                if (value is not null)
                {
                    Require(reader.GetString(0) == value && reader.GetChars(0, 0, null, 0, 0) == value.Length);
                    using var buffered = reader.GetTextReader(0); reader.Close();
                    Require(await buffered.ReadToEndAsync() == value);
                }
            }
            parameter.Value = "<invalid>";
            try { await command.ExecuteScalarAsync(token); throw new InvalidOperationException("XML validation error was ignored."); }
            catch (KingbaseException error) { Require(error.SqlState == "2200N"); }
            parameter.Value = "<recovered/>"; Require((string)(await command.ExecuteScalarAsync(token))! == "<recovered/>");
            connection.Close(); await peer.WaitAsync(token); return;
        }
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

    static (string Name, Type Type)[] RoutineFields(string name) => name == "ProcedureParameters" ?
        [("ROUTINE_CATALOG", typeof(string)), ("ROUTINE_SCHEMA", typeof(string)), ("ROUTINE_NAME", typeof(string)), ("SPECIFIC_NAME", typeof(string)),
            ("ORDINAL_POSITION", typeof(int)), ("PARAMETER_NAME", typeof(string)), ("PARAMETER_MODE", typeof(string)),
            ("IS_VARIADIC", typeof(bool)), ("IS_TABLE_COLUMN", typeof(bool)), ("DATA_TYPE", typeof(string)),
            ("CHARACTER_MAXIMUM_LENGTH", typeof(long)), ("CHARACTER_OCTET_LENGTH", typeof(long)), ("NUMERIC_PRECISION", typeof(int)),
            ("NUMERIC_PRECISION_RADIX", typeof(int)), ("NUMERIC_SCALE", typeof(int)), ("DATETIME_PRECISION", typeof(int)),
            ("INTERVAL_TYPE", typeof(string)), ("INTERVAL_PRECISION", typeof(int)), ("UDT_CATALOG", typeof(string)),
            ("UDT_SCHEMA", typeof(string)), ("UDT_NAME", typeof(string)), ("PARAMETER_DEFAULT", typeof(string))] :
        [("ROUTINE_CATALOG", typeof(string)), ("ROUTINE_SCHEMA", typeof(string)), ("ROUTINE_NAME", typeof(string)), ("SPECIFIC_NAME", typeof(string)),
            ("ROUTINE_TYPE", typeof(string)), ("DATA_TYPE", typeof(string)), ("CHARACTER_MAXIMUM_LENGTH", typeof(long)),
            ("CHARACTER_OCTET_LENGTH", typeof(long)), ("NUMERIC_PRECISION", typeof(int)), ("NUMERIC_PRECISION_RADIX", typeof(int)),
            ("NUMERIC_SCALE", typeof(int)), ("DATETIME_PRECISION", typeof(int)), ("INTERVAL_TYPE", typeof(string)), ("INTERVAL_PRECISION", typeof(int)),
            ("UDT_CATALOG", typeof(string)), ("UDT_SCHEMA", typeof(string)), ("UDT_NAME", typeof(string)), ("RETURNS_SET", typeof(bool)),
            ("INPUT_PARAMETER_COUNT", typeof(int)), ("DEFAULT_PARAMETER_COUNT", typeof(int)), ("EXTERNAL_LANGUAGE", typeof(string)),
            ("IS_DETERMINISTIC", typeof(bool)), ("SECURITY_TYPE", typeof(string)), ("IS_NULL_CALL", typeof(bool))];
    static string?[] RoutineValues(string name) => name == "ProcedureParameters" ?
        ["db", "MiXeD汉%_'", "routine", "routine_1", "1", null, "IN", "YES", "NO", "ARRAY", null, null, null, null, "-2", null, null, null,
            "db", "pg_catalog", "_int4", "ARRAY[1, 2]"] :
        ["db", "MiXeD汉%_'", "routine", "routine_1", name == "Functions" ? "FUNCTION" : "PROCEDURE", name == "Functions" ? "numeric" : null,
            null, null, null, null, null, null, null, null, name == "Functions" ? "db" : null,
            name == "Functions" ? "pg_catalog" : null, name == "Functions" ? "numeric" : null,
            "NO", "2", "1", "SQL", "NO", "INVOKER", name == "Functions" ? "YES" : null];

    static (string Name, Type Type)[] DirectoryFields(string name) => name == "Databases" ?
        [("DATABASE_NAME", typeof(string)), ("OWNER", typeof(string)), ("ENCODING", typeof(string)), ("COLLATION", typeof(string)), ("CTYPE", typeof(string)),
            ("IS_TEMPLATE", typeof(bool)), ("ALLOWS_CONNECTIONS", typeof(bool)), ("CONNECTION_LIMIT", typeof(int)), ("HAS_CONNECT_PRIVILEGE", typeof(bool))] :
        [("USER_NAME", typeof(string)), ("IS_SUPERUSER", typeof(bool)), ("INHERITS_PRIVILEGES", typeof(bool)), ("CAN_CREATE_ROLE", typeof(bool)),
            ("CAN_CREATE_DATABASE", typeof(bool)), ("CAN_REPLICATE", typeof(bool)), ("BYPASSES_ROW_SECURITY", typeof(bool)), ("CONNECTION_LIMIT", typeof(int))];
    static string?[] DirectoryValues(string name) => name == "Databases" ?
        ["MiXeD汉%_'", "owner", "UTF8", "C", "C", "YES", "NO", "-2", "YES"] : ["MiXeD汉%_'", "NO", "YES", "NO", "YES", "NO", "NO", "-1"];

    static readonly string[] RelationalNames = ["PrimaryKeys", "UniqueKeys", "ForeignKeys", "ForeignKeyColumns", "Indexes", "IndexColumns", "Constraints", "ConstraintColumns", "PrimaryKey"];
    static readonly (string Name, Type Type)[] SequenceFields =
        [("SEQUENCE_CATALOG", typeof(string)), ("SEQUENCE_SCHEMA", typeof(string)), ("SEQUENCE_NAME", typeof(string)), ("DATA_TYPE", typeof(string)),
            ("NUMERIC_PRECISION", typeof(int)), ("NUMERIC_PRECISION_RADIX", typeof(int)), ("NUMERIC_SCALE", typeof(int)),
            ("START_VALUE", typeof(long)), ("MINIMUM_VALUE", typeof(long)), ("MAXIMUM_VALUE", typeof(long)), ("INCREMENT", typeof(long)),
            ("CYCLE_OPTION", typeof(bool)), ("CACHE_SIZE", typeof(long))];
    static readonly string?[] SequenceValues = ["db", "MiXeD汉%_'", "sequence", "bigint", "64", "2", "0",
        "-2147483649", "-9223372036854775808", "-1", "-2", "YES", "17"];
    static (string Name, bool Integer, bool Flag)[] RelationalFields(string name) => (name switch
    {
        "PrimaryKeys" or "UniqueKeys" or "PrimaryKey" => "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME CONSTRAINT_NAME COLUMN_NAME ORDINAL_POSITION IS_DEFERRABLE INITIALLY_DEFERRED",
        "Constraints" => "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME CONSTRAINT_NAME CONSTRAINT_TYPE_CODE CONSTRAINT_TYPE IS_DEFERRABLE INITIALLY_DEFERRED IS_VALIDATED NO_INHERIT DEFINITION",
        "ConstraintColumns" => "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME CONSTRAINT_NAME ORDINAL_POSITION COLUMN_NAME CONSTRAINT_TYPE_CODE CONSTRAINT_TYPE",
        "ForeignKeys" => "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME CONSTRAINT_NAME REFERENCED_TABLE_CATALOG REFERENCED_TABLE_SCHEMA REFERENCED_TABLE_NAME REFERENCED_INDEX_NAME MATCH_OPTION UPDATE_RULE DELETE_RULE IS_DEFERRABLE INITIALLY_DEFERRED IS_VALIDATED",
        "ForeignKeyColumns" => "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME CONSTRAINT_NAME COLUMN_NAME ORDINAL_POSITION REFERENCED_TABLE_CATALOG REFERENCED_TABLE_SCHEMA REFERENCED_TABLE_NAME REFERENCED_COLUMN_NAME",
        "Indexes" => "INDEX_CATALOG INDEX_SCHEMA INDEX_NAME TABLE_CATALOG TABLE_SCHEMA TABLE_NAME INDEX_TYPE IS_UNIQUE IS_PRIMARY IS_VALID IS_READY IS_LIVE IS_CLUSTERED KEY_COLUMN_COUNT COLUMN_COUNT PREDICATE EXPRESSION DEFINITION",
        _ => "INDEX_CATALOG INDEX_SCHEMA INDEX_NAME ORDINAL_POSITION TABLE_CATALOG TABLE_SCHEMA TABLE_NAME COLUMN_NAME COLUMN_EXPRESSION IS_INCLUDED IS_DESCENDING NULLS_FIRST COLLATION_CATALOG COLLATION_SCHEMA COLLATION_NAME OPERATOR_CLASS_CATALOG OPERATOR_CLASS_SCHEMA OPERATOR_CLASS_NAME"
    }).Split(' ').Select(field => (field, field is "ORDINAL_POSITION" or "KEY_COLUMN_COUNT" or "COLUMN_COUNT",
        field.StartsWith("IS_", StringComparison.Ordinal) || field is "INITIALLY_DEFERRED" or "NULLS_FIRST" or "NO_INHERIT")).ToArray();
    static string?[] RelationalValues(string name) => name switch
    {
        "PrimaryKeys" or "UniqueKeys" or "PrimaryKey" => ["db", "MiXeD汉%_'", "table", "key", "column", "2", "YES", "NO"],
        "Constraints" => ["db", "MiXeD汉%_'", "table", "check", "c", "CHECK", "NO", "NO", "NO", "YES", "CHECK (a > 0)"],
        "ConstraintColumns" => ["db", "MiXeD汉%_'", "table", "check", "2", "b", "c", "CHECK"],
        "ForeignKeys" => ["db", "MiXeD汉%_'", "table", "fk", "db", "target", "parent", "unique_index", "SIMPLE", "CASCADE", "SET NULL", "YES", "NO", "YES"],
        "ForeignKeyColumns" => ["db", "MiXeD汉%_'", "table", "fk", "column", "2", "db", "target", "parent", "target_column"],
        "Indexes" => ["db", "MiXeD汉%_'", "index", "db", "MiXeD汉%_'", "table", "btree", "YES", "NO", "YES", "YES", "YES", "NO", "1", "2", "flag > 0", "lower(name)", "CREATE INDEX ..."],
        _ => ["db", "MiXeD汉%_'", "index", "2", "db", "MiXeD汉%_'", "table", null, "lower(name)", "NO", null, null, null, null, null, "db", "pg_catalog", "text_ops"]
    };
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
