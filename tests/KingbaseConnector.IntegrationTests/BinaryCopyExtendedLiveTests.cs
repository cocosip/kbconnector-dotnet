using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class BinaryCopyExtendedLiveTests
{
    static readonly KingbaseDbType[] Types = [KingbaseDbType.Numeric, KingbaseDbType.Date, KingbaseDbType.Time,
        KingbaseDbType.TimeTz, KingbaseDbType.Timestamp, KingbaseDbType.TimestampTz, KingbaseDbType.Interval];
    const string Definition = "n pg_catalog.numeric,d pg_catalog.date,t pg_catalog.time,z pg_catalog.timetz,s pg_catalog.timestamp,u pg_catalog.timestamptz,v pg_catalog.interval";
    static object[] Finite() => [KingbaseNumeric.Parse("12345.678900"), new KingbaseDate(-1, 12, 31), TimeSpan.FromDays(1),
        new KingbaseTimeTz(TimeSpan.FromTicks(10), TimeSpan.FromSeconds(19801)), KingbaseTimestamp.Parse("1999-12-31 23:59:59.999999"),
        KingbaseTimestampTz.Parse("2000-01-01 00:00:00+05:30"), new KingbaseInterval(-2, 3, -1)];

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ServerGeneratedValuesEstablishFormatsIndependentlyOfImporter(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Timezone = "America/New_York";
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var status = new KingbaseCommand("SHOW integer_datetimes", connection)) Assert.Equal("on", await status.ExecuteScalarAsync(token));
        using var output = await connection.BeginBinaryExportAsync("COPY (SELECT '12345.678900'::pg_catalog.numeric,'0001-12-31 BC'::pg_catalog.date,'24:00:00'::pg_catalog.time,'00:00:00.000001+05:30:01'::pg_catalog.timetz,'1999-12-31 23:59:59.999999'::pg_catalog.timestamp,'2000-01-01 00:00:00+05:30'::pg_catalog.timestamptz,'P-2M3DT-0.000001S'::pg_catalog.interval) TO STDOUT (FORMAT BINARY)", Types, token);
        AssertRow(Finite(), Assert.IsType<object[]>(await output.ReadRowAsync(token))); Assert.Null(output.ReadRow()); Assert.Equal(1UL, output.RowsCopied);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task NumericClientPrecisionAndBinaryScaleLimitsStayExact(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_numeric_limits (n pg_catalog.numeric)", connection)) await create.ExecuteNonQueryAsync(token);
        var values = new[] { new KingbaseNumeric(System.Numerics.BigInteger.Pow(10, KingbaseNumeric.MaxDigits) - 1, 16383),
            new KingbaseNumeric(1, 16383), KingbaseNumeric.FromDecimal(decimal.MinValue), KingbaseNumeric.FromDecimal(1.2300m) };
        using (var input = connection.BeginBinaryImport("COPY kb_copy_numeric_limits FROM STDIN (FORMAT BINARY)", KingbaseDbType.Numeric))
        { foreach (var value in values) input.WriteRow(value); Assert.Equal((ulong)values.Length, await input.CompleteAsync(token)); }
        using (var select = new KingbaseCommand("SELECT n FROM kb_copy_numeric_limits", connection))
        using (var reader = await select.ExecuteReaderAsync(token))
        { foreach (var value in values) { Assert.True(reader.Read()); AssertValue(value, reader.GetProviderSpecificValue(0)); } Assert.False(reader.Read()); }
        using var output = connection.BeginBinaryExport("COPY kb_copy_numeric_limits TO STDOUT (FORMAT BINARY)", KingbaseDbType.Numeric);
        foreach (var value in values) AssertRow([value], Assert.IsType<object[]>(output.ReadRow()));
        Assert.Null(await output.ReadRowAsync(token)); Assert.Equal((ulong)values.Length, output.RowsCopied);
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ServerSendFunctionsMatchIndependentNetworkLiterals(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        (string Name, string Value, string Hex)[] cases = [
            ("numeric", "12345.6789", "0003000100000004000109291a85"), ("numeric", "-0.00001", "0001fffe4000000503e8"),
            ("numeric", "NaN", "00000000c0000000"),
            ("date", "0001-01-01", "fff4dbf9"), ("date", "0001-12-31 BC", "fff4dbf8"),
            ("date", "infinity", "7fffffff"), ("date", "-infinity", "80000000"),
            ("date", "4714-11-24 BC", "ffda97a7"), ("date", "5874897-12-31", "7fda970c"),
            ("time", "24:00:00", "000000141dd76000"), ("timetz", "00:00:00.000001+05:30:01", "0000000000000001ffffb2a7"),
            ("timestamp", "1999-12-31 23:59:59.999999", "ffffffffffffffff"), ("timestamp", "infinity", "7fffffffffffffff"),
            ("timestamp", "4714-11-24 00:00:00 BC", "fd0f7cc1411fa000"), ("timestamp", "294276-12-31 23:59:59.999999", "7fffff5bb3b29fff"),
            ("timestamptz", "2000-01-01 00:00:00+00", "0000000000000000"), ("timestamptz", "-infinity", "8000000000000000"),
            ("interval", "P-2M3DT-0.000001S", "ffffffffffffffff00000003fffffffe") ];
        foreach (var item in cases)
        {
            using var command = new KingbaseCommand($"SELECT pg_catalog.encode(pg_catalog.{item.Name}_send('{item.Value}'::pg_catalog.{item.Name}),'hex')", connection);
            if (mode == KingbaseCompatibilityMode.SqlServer && item.Name == "numeric" && item.Value == "NaN")
            { Assert.Equal("22P02", (await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token))).SqlState); continue; }
            Assert.Equal(item.Hex, await command.ExecuteScalarAsync(token));
        }
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ExactImportsMatchIndependentProviderTextReaderAndExport(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Timezone = "Asia/Tokyo";
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_extended (" + Definition + ")", connection)) await create.ExecuteNonQueryAsync(token);
        var rows = new object[][] { Finite(),
            [KingbaseNumeric.NaN, KingbaseDate.PositiveInfinity, TimeSpan.Zero, new KingbaseTimeTz(TimeSpan.FromDays(1), TimeSpan.FromSeconds(-57599)), KingbaseTimestamp.NegativeInfinity, KingbaseTimestampTz.PositiveInfinity, new KingbaseInterval(int.MinValue, int.MaxValue, -1)],
            [KingbaseNumeric.Parse("-0.00001"), KingbaseDate.NegativeInfinity, TimeSpan.FromTicks(10), new KingbaseTimeTz(TimeSpan.Zero, TimeSpan.Zero), KingbaseTimestamp.PositiveInfinity, KingbaseTimestampTz.NegativeInfinity, new KingbaseInterval(0, 0, -86400000001)],
            [KingbaseNumeric.Parse("0.0000"), new KingbaseDate(5874897, 12, 31), TimeSpan.Zero, default(KingbaseTimeTz), KingbaseTimestamp.Parse("294276-12-31 23:59:59.999999"), KingbaseTimestampTz.Parse("0001-01-01 00:00:00+00 BC"), default(KingbaseInterval)],
            [KingbaseNumeric.Parse("10000.0000"), new KingbaseDate(-4714, 11, 24), TimeSpan.Zero, default(KingbaseTimeTz), KingbaseTimestamp.Parse("4714-11-24 00:00:00 BC"), KingbaseTimestampTz.Parse("4714-11-24 00:00:00+00 BC"), default(KingbaseInterval)],
            Enumerable.Repeat<object>(DBNull.Value, Types.Length).ToArray() };
        using (var input = await connection.BeginBinaryImportAsync("COPY kb_copy_extended FROM STDIN (FORMAT BINARY)", Types, token))
        {
            foreach (var row in rows) await input.WriteRowAsync(row, token);
            Assert.Equal((ulong)rows.Length, await input.CompleteAsync(token));
        }
        // Explicit UTC projection also works when a compatibility mode omits the offset from timestamptz text.
        using (var select = new KingbaseCommand("SELECT n,d,t,z,s,u AT TIME ZONE 'UTC',v FROM kb_copy_extended", connection))
        using (var reader = await select.ExecuteReaderAsync(token))
        {
            foreach (var row in rows)
            {
                Assert.True(await reader.ReadAsync(token));
                for (var i = 0; i < row.Length; i++)
                {
                    var expected = i == 5 && row[i] is KingbaseTimestampTz instant ? instant.UtcValue : row[i];
                    if (mode == KingbaseCompatibilityMode.SqlServer && expected is KingbaseNumeric number && number.IsNaN) expected = default(KingbaseNumeric);
                    AssertValue(expected, reader.GetProviderSpecificValue(i));
                }
            }
            Assert.False(reader.Read());
        }
        using var output = await connection.BeginBinaryExportAsync("COPY kb_copy_extended TO STDOUT (FORMAT BINARY)", Types, token);
        foreach (var row in rows) AssertRow(row, Assert.IsType<object[]>(await output.ReadRowAsync(token)));
        Assert.Null(await output.ReadRowAsync(token)); Assert.Equal((ulong)rows.Length, output.RowsCopied);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task BinaryIntervalPreservesValuesBeyondTheServerTextFormatter(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_interval_extreme (v pg_catalog.interval)", connection)) await create.ExecuteNonQueryAsync(token);
        var value = new KingbaseInterval(0, 0, long.MinValue + 1);
        using (var input = connection.BeginBinaryImport("COPY kb_copy_interval_extreme FROM STDIN (FORMAT BINARY)", KingbaseDbType.Interval))
        { input.WriteRow(value); Assert.Equal(1UL, await input.CompleteAsync(token)); }
        using (var text = new KingbaseCommand("SELECT v FROM kb_copy_interval_extreme", connection))
            Assert.Equal("22008", (await Assert.ThrowsAsync<KingbaseException>(() => text.ExecuteScalarAsync(token))).SqlState);
        using var output = connection.BeginBinaryExport("COPY kb_copy_interval_extreme TO STDOUT (FORMAT BINARY)", KingbaseDbType.Interval);
        Assert.Equal(new object[] {value}, await output.ReadRowAsync(token)); Assert.Null(output.ReadRow()); Assert.Equal(1UL, output.RowsCopied);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ServerRejectedNumericInfinitiesDrainWithoutInventingClientSupport(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_numeric_special (n pg_catalog.numeric)", connection)) await create.ExecuteNonQueryAsync(token);
        foreach (var value in new[] {KingbaseNumeric.PositiveInfinity, KingbaseNumeric.NegativeInfinity})
        {
            using (var text = new KingbaseCommand("SELECT '" + value + "'::pg_catalog.numeric", connection))
                Assert.Equal("22P02", (await Assert.ThrowsAsync<KingbaseException>(() => text.ExecuteScalarAsync(token))).SqlState);
            using (var input = await connection.BeginBinaryImportAsync("COPY kb_copy_numeric_special FROM STDIN (FORMAT BINARY)", new[] {KingbaseDbType.Numeric}, token))
            {
                await input.WriteRowAsync([value], token);
                Assert.Equal("22P03", (await Assert.ThrowsAsync<KingbaseException>(() => input.CompleteAsync(token))).SqlState); Assert.Null(input.RowsCopied);
            }
            Assert.Equal(ConnectionState.Open, connection.State);
            using var count = new KingbaseCommand("SELECT count(*)::pg_catalog.int4 FROM kb_copy_numeric_special", connection); Assert.Equal(0, await count.ExecuteScalarAsync(token));
        }
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ExactTemporalCopyPreservesTransactionAndPooledBorrowerOwnership(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build(); int? pid = null; KingbaseBinaryExporter? old = null;
        for (var lease = 0; lease < 2; lease++)
        {
            using var connection = await source.OpenConnectionAsync(token);
            using (var identity = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", connection))
            { var actual = Assert.IsType<int>(await identity.ExecuteScalarAsync(token)); if (pid.HasValue) Assert.Equal(pid, actual); pid = actual; }
            using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_extended_pool (" + Definition + ")", connection)) await create.ExecuteNonQueryAsync(token);
            using (var transaction = await connection.BeginTransactionAsync(token))
            {
                using (var input = await connection.BeginBinaryImportAsync("COPY kb_copy_extended_pool FROM STDIN (FORMAT BINARY)", Types, token))
                {
                    old?.Dispose();
                    await Assert.ThrowsAsync<ArgumentException>(() => input.WriteRowAsync([1.0m, new DateTime(2024, 1, 1), TimeSpan.FromTicks(1), default(KingbaseTimeTz), default(KingbaseTimestamp), default(KingbaseTimestampTz), default(KingbaseInterval)], token));
                    await input.WriteRowAsync(Finite(), token); Assert.Equal(1UL, await input.CompleteAsync(token));
                }
                old = await connection.BeginBinaryExportAsync("COPY kb_copy_extended_pool TO STDOUT (FORMAT BINARY)", Types, token);
                AssertRow(Finite(), Assert.IsType<object[]>(await old.ReadRowAsync(token))); Assert.Null(old.ReadRow()); Assert.Equal(1UL, old.RowsCopied);
                Assert.Same(connection, transaction.Connection); await transaction.RollbackAsync(token);
            }
            using var count = new KingbaseCommand("SELECT count(*)::pg_catalog.int4 FROM kb_copy_extended_pool", connection); Assert.Equal(0, await count.ExecuteScalarAsync(token));
            Assert.Equal(ConnectionState.Open, connection.State);
        }
        old!.Dispose();
    });

    static void AssertRow(object[] expected, object[] actual)
    { Assert.Equal(expected.Length, actual.Length); for (var i = 0; i < expected.Length; i++) AssertValue(expected[i], actual[i]); }
    static void AssertValue(object expected, object actual)
    {
        Assert.Equal(expected, actual);
        if (expected is KingbaseNumeric {IsFinite: true} numeric)
        { var exact = Assert.IsType<KingbaseNumeric>(actual); Assert.Equal(numeric.Scale, exact.Scale); Assert.Equal(numeric.Coefficient, exact.Coefficient); }
    }
}
