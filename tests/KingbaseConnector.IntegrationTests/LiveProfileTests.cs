using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.IntegrationTests;

public sealed class LiveProfileTests
{
    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task BootstrapDiscoversModeAndActualBuiltinCatalog(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, token);
            Assert.Equal(mode, session.Identity.EffectiveMode);
            Assert.Equal(mode, session.Identity.ReportedMode);
            Assert.Equal(9, session.Identity.Version.Major);
            Assert.Equal("UTF8", session.Startup.Parameters["client_encoding"]);
            foreach (var name in new[] { "bool", "int2", "int4", "int8", "text", "bytea" })
            {
                var type = session.Catalog.ByName["pg_catalog." + name];
                Assert.Equal("pg_catalog", type.Namespace);
                Assert.NotEqual(0u, type.Oid);
            }
            Assert.Equal("1", Assert.Single(await session.Queries.ExecuteAsync("SELECT 1", token)).Rows[0][0]);
        });

    [LiveFact]
    public Task PgPublicParametersAndTypedNullRoundTrip() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT @i AS i,@t AS t,@n AS n,@b AS b,@f AS f,@d AS d", connection);
        var text = "Kingbase 中文 ' " + (char)92;
        command.Parameters.Add(new KingbaseParameter("i", 42));
        command.Parameters.Add(new KingbaseParameter("t", text));
        command.Parameters.Add(new KingbaseParameter("n", DBNull.Value) { DataTypeName = "pg_catalog.text" });
        command.Parameters.Add(new KingbaseParameter("b", new byte[] { 0, 1, 127, 255 }));
        command.Parameters.Add(new KingbaseParameter("f", true));
        command.Parameters.Add(new KingbaseParameter("d", 123.450m));
        using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(42, reader.GetInt32(0));
        Assert.Equal(text, reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(new byte[] { 0, 1, 127, 255 }, reader.GetFieldValue<byte[]>(3));
        Assert.True(reader.GetBoolean(4));
        Assert.Equal(123.450m, reader.GetDecimal(5));
        Assert.False(await reader.ReadAsync(token));
    });

    [LiveFact]
    public Task PgSynchronizedQueryErrorLeavesConnectionUsable() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT 1/0", connection);
        var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token));
        Assert.Equal("22012", error.SqlState);
        command.CommandText = "SELECT 42";
        Assert.Equal(42, await command.ExecuteScalarAsync(token));
    });

    [LiveFact]
    public Task PgInternalNamedStatementExecutesAndCloses() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, token);
        var oid = session.Catalog.ByName["pg_catalog.int4"].Oid;
        var statement = await session.Queries.PrepareAsync("SELECT $1", session.Catalog, [oid], token);
        foreach (var value in new[] { 42, 43 })
        {
            var results = await session.Queries.ExecutePreparedAsync(statement, session.Catalog, [new(oid, value)], token);
            Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(results).Rows[0][0]);
        }
        await session.Queries.UnprepareAsync(statement, token);
        Assert.False(statement.IsValid);
        Assert.Equal("1", Assert.Single(await session.Queries.ExecuteAsync("SELECT 1", token)).Rows[0][0]);
    });

    [LiveFact]
    public Task PgPublicPrepareValuesSharedUnprepareAndLogicalReopen() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        using var first = new KingbaseCommand("SELECT @p", connection);
        first.Parameters.AddWithValue("p", 42);
        using var second = new KingbaseCommand("SELECT @p", connection);
        second.Parameters.AddWithValue("p", 43);
        first.Prepare(); await second.PrepareAsync(token);
        Assert.Equal(42, first.ExecuteScalar());
        first.Parameters[0].Value = 44;
        Assert.Equal(44, await first.ExecuteScalarAsync(token));
        await first.UnprepareAsync(token);
        Assert.Equal(43, await second.ExecuteScalarAsync(token));
        await second.PrepareAsync(token);
        second.Unprepare(); second.Unprepare();
        first.Parameters[0].Value = DBNull.Value;
        first.Parameters[0].KingbaseDbType = KingbaseDbType.Integer;
        await first.PrepareAsync(token);
        Assert.Same(DBNull.Value, await first.ExecuteScalarAsync(token));
        connection.Close(); await connection.OpenAsync(token);
        first.Parameters[0].Value = 45;
        Assert.Equal(45, await first.ExecuteScalarAsync(token));
        await first.UnprepareAsync(token);
    });

    [LiveFact(writes: true)]
    public Task PgPublicPreparedSchemaErrorAndTransactionRecovery() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_live_prepared (id int)", token);
        using var command = new KingbaseCommand("SELECT id FROM kb_live_prepared", connection);
        await command.PrepareAsync(token);
        Assert.Null(await command.ExecuteScalarAsync(token));
        await Scalar(connection, "ALTER TABLE kb_live_prepared ALTER COLUMN id TYPE bigint", token);
        var schemaError = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token));
        Assert.Equal("0A000", schemaError.SqlState);
        // Recovery is an explicit caller action; the failed Execute is never replayed.
        await command.PrepareAsync(token);
        using (var reader = await command.ExecuteReaderAsync(token)) Assert.Equal(typeof(long), reader.GetFieldType(0));
        await command.UnprepareAsync(token);
        using var transaction = await connection.BeginTransactionAsync(token);
        command.CommandText = "SELECT 1/0"; command.Transaction = transaction;
        await command.PrepareAsync(token);
        var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token));
        Assert.Equal("22012", error.SqlState);
        Assert.Same(connection, transaction.Connection);
        await command.UnprepareAsync(token);
        await transaction.RollbackAsync(token);
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    [LiveFact]
    public Task PgAutomaticThresholdUnprepareAndPoolReset() => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1;
        settings.MaxAutoPrepare = 1; settings.AutoPrepareMinUsages = 3;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        using var connection = await source.OpenConnectionAsync(token);
        using var count = new KingbaseCommand("SELECT count(*) FROM pg_catalog.pg_prepared_statements WHERE statement = 'SELECT $1'", connection);
        await count.PrepareAsync(token); // Explicit ownership keeps this observer outside the automatic capacity.
        var pid = await Scalar(connection, "SELECT pg_catalog.pg_backend_pid()", token);
        using var command = new KingbaseCommand("SELECT @p", connection); command.Parameters.AddWithValue("p", 42);
        Assert.Equal(0L, await count.ExecuteScalarAsync(token));
        for (var i = 1; i <= 4; i++)
        {
            command.Parameters[0].Value = i;
            Assert.Equal(i, await command.ExecuteScalarAsync(token));
            Assert.Equal(i < 3 ? 0L : 1L, await count.ExecuteScalarAsync(token));
        }
        await command.UnprepareAsync(token); Assert.Equal(0L, await count.ExecuteScalarAsync(token));
        for (var i = 0; i < 2; i++) Assert.Equal(4, await command.ExecuteScalarAsync(token));
        Assert.Equal(0L, await count.ExecuteScalarAsync(token));
        await connection.CloseAsync(); await connection.OpenAsync(token);
        Assert.Equal(pid, await Scalar(connection, "SELECT pg_catalog.pg_backend_pid()", token));
        await count.PrepareAsync(token);
        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(4, await command.ExecuteScalarAsync(token));
            Assert.Equal(i < 3 ? 0L : 1L, await count.ExecuteScalarAsync(token));
        }
    });

    [LiveFact(writes: true)]
    public Task PgAutomaticSchemaInvalidationAndFailedTransactionRecovery() => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.MaxAutoPrepare = 1; settings.AutoPrepareMinUsages = 2;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_live_auto_prepared (id int)", token);
        using var command = new KingbaseCommand("SELECT id FROM kb_live_auto_prepared", connection);
        Assert.Null(await command.ExecuteScalarAsync(token));
        Assert.Null(await command.ExecuteScalarAsync(token));
        await Scalar(connection, "ALTER TABLE kb_live_auto_prepared ALTER COLUMN id TYPE bigint", token);
        Assert.Equal("0A000", (await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token))).SqlState);
        for (var i = 0; i < 2; i++)
        {
            using var reader = await command.ExecuteReaderAsync(token);
            Assert.Equal(typeof(long), reader.GetFieldType(0));
        }
        await command.UnprepareAsync(token);
        using var count = new KingbaseCommand("SELECT count(*) FROM pg_catalog.pg_prepared_statements WHERE statement = 'SELECT 10/$1'", connection);
        await count.PrepareAsync(token);
        command.CommandText = "SELECT 10/@p";
        command.Parameters.AddWithValue("p", 2);
        Assert.Equal(5, await command.ExecuteScalarAsync(token));
        command.Parameters[0].Value = 0;
        Assert.Equal("22012", (await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token))).SqlState);
        Assert.Equal(1L, await count.ExecuteScalarAsync(token));
        await command.UnprepareAsync(token);
        Assert.Equal(0L, await count.ExecuteScalarAsync(token));
        using var transaction = await connection.BeginTransactionAsync(token);
        command.Transaction = transaction; command.Parameters[0].Value = 2;
        Assert.Equal(5, await command.ExecuteScalarAsync(token));
        command.Parameters[0].Value = 0;
        Assert.Equal("22012", (await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token))).SqlState);
        await command.UnprepareAsync(token); Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync(token);
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    [LiveFact]
    public Task PgDateCalendarValuesPreparedBindingAndIsoStyleRecovery() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        await Scalar(connection, "SELECT pg_catalog.set_config('DateStyle','ISO, DMY',false)", token);
        using var command = new KingbaseCommand("SELECT @d, CAST(@d AS text)", connection);
        command.Parameters.Add(new KingbaseParameter("d", DBNull.Value) { KingbaseDbType = KingbaseDbType.Date });
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0));
            Assert.Equal(typeof(DateTime), reader.GetFieldType(0)); Assert.Equal(typeof(KingbaseDate), reader.GetProviderSpecificFieldType(0));
        }
        foreach (var text in new[] { "0001-01-01", "1582-10-10", "2000-02-29", "9999-12-31", "10000-01-01",
            "5874897-12-31", "0001-02-29 BC", "4714-11-24 BC", "infinity", "-infinity" })
        {
            command.Parameters[0].Value = KingbaseDate.Parse(text);
            using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(reader.Read()); Assert.Equal(text, reader.GetString(1));
            Assert.Equal(text, reader.GetFieldValue<KingbaseDate>(0).ToString());
            var date = Assert.IsType<KingbaseDate>(command.Parameters[0].Value);
            if (date.IsFinite && date.Year is >= 1 and <= 9999)
            {
                Assert.Equal(date.ToDateTime(), reader.GetDateTime(0)); Assert.Equal(DateTimeKind.Unspecified, reader.GetDateTime(0).Kind);
                Assert.Equal(date.ToDateOnly(), reader.GetFieldValue<DateOnly>(0));
            }
            else Assert.Throws<OverflowException>(() => reader.GetDateTime(0));
        }
        command.Parameters[0].Value = new DateOnly(2024, 2, 29);
        await command.PrepareAsync(token);
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.True(reader.Read()); Assert.Equal(new DateTime(2024, 2, 29), reader.GetDateTime(0));
        }
        await Scalar(connection, "SELECT pg_catalog.set_config('DateStyle','SQL, DMY',false)", token);
        command.Parameters[0].Value = new DateOnly(2024, 2, 3);
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            // ISO input remains unambiguous when display order differs; compare server text independently.
            Assert.True(reader.Read()); Assert.Equal("03/02/2024", reader.GetString(1));
            Assert.Throws<NotSupportedException>(() => reader.GetValue(0));
            Assert.Throws<NotSupportedException>(() => reader.GetProviderSpecificValue(0));
        }
        await Scalar(connection, "SELECT pg_catalog.set_config('DateStyle','ISO, MDY',false)", token);
        Assert.Equal(new DateTime(2024, 2, 3), await command.ExecuteScalarAsync(token));
        await command.UnprepareAsync(token);
    });

    [LiveFact]
    public Task PgDatePoolRestoresDisplayFormat() => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        using var connection = await source.OpenConnectionAsync(token);
        var baseline = await Scalar(connection, "SHOW DateStyle", token);
        var pid = await Scalar(connection, "SELECT pg_catalog.pg_backend_pid()", token);
        await Scalar(connection, "SELECT pg_catalog.set_config('DateStyle','German, DMY',false)", token);
        await connection.CloseAsync(); await connection.OpenAsync(token);
        Assert.Equal(pid, await Scalar(connection, "SELECT pg_catalog.pg_backend_pid()", token));
        Assert.Equal(baseline, await Scalar(connection, "SHOW DateStyle", token));
        await Scalar(connection, "SELECT pg_catalog.set_config('DateStyle','ISO, YMD',false)", token);
        Assert.Equal(new DateTime(2024, 2, 29), await Scalar(connection, "SELECT DATE '2024-02-29'", token));
    });

    [LiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgTimeAndTimeTzExactBindingAcrossSessionFormatsAndPreparation(bool automatic) => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.MaxAutoPrepare = automatic ? 2 : 0; settings.AutoPrepareMinUsages = 2;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT @t, CAST(@t AS text), @z, CAST(@z AS text)", connection);
        command.Parameters.Add(new KingbaseParameter("t", DBNull.Value) { DbType = DbType.Time });
        command.Parameters.Add(new KingbaseParameter("z", DBNull.Value) { KingbaseDbType = KingbaseDbType.TimeTz });
        if (!automatic) await command.PrepareAsync(token);
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.True(reader.IsDBNull(2));
            Assert.Equal(typeof(TimeSpan), reader.GetFieldType(0)); Assert.Equal(typeof(KingbaseTimeTz), reader.GetFieldType(2));
            Assert.Equal(typeof(KingbaseTimeTz), reader.GetProviderSpecificFieldType(2));
        }
        foreach (var scenario in new[] { ("ISO, MDY", "UTC"), ("SQL, DMY", "Asia/Shanghai"),
            ("ISO, YMD", "America/New_York"), ("German, DMY", "UTC") })
        {
            await Scalar(connection, "SELECT pg_catalog.set_config('DateStyle','" + scenario.Item1 + "',false)", token);
            await Scalar(connection, "SELECT pg_catalog.set_config('TimeZone','" + scenario.Item2 + "',false)", token);
            foreach (var row in new[] { (0L, "00:00:00", "00:00:00+00"),
                (10L, "00:00:00.000001", "00:00:00.000001-00:00:01"),
                (452961234560L, "12:34:56.123456", "12:34:56.123456+05:30:01"),
                (863999999990L, "23:59:59.999999", "23:59:59.999999-15:59:59"),
                (864000000000L, "24:00:00", "24:00:00+15:59:59") })
            {
                command.Parameters[0].Value = TimeSpan.FromTicks(row.Item1);
                command.Parameters[1].Value = KingbaseTimeTz.Parse(row.Item3);
                using var reader = await command.ExecuteReaderAsync(token);
                Assert.True(reader.Read()); Assert.Equal(row.Item2, reader.GetString(1)); Assert.Equal(row.Item3, reader.GetString(3));
                Assert.Equal(TimeSpan.FromTicks(row.Item1), reader.GetFieldValue<TimeSpan>(0));
                Assert.Equal(row.Item3, reader.GetFieldValue<KingbaseTimeTz>(2).ToString());
                Assert.Equal(command.Parameters[1].Value, reader.GetProviderSpecificValue(2));
                if (row.Item1 < TimeSpan.TicksPerDay) Assert.Equal(new TimeOnly(row.Item1), reader.GetFieldValue<TimeOnly>(0));
                else Assert.Throws<OverflowException>(() => reader.GetFieldValue<TimeOnly>(0));
                Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<TimeOnly>(2));
            }
        }
        command.Parameters[0].Value = new TimeOnly(10L); command.Parameters[1].Value = default(KingbaseTimeTz);
        Assert.Equal(TimeSpan.FromTicks(10), await command.ExecuteScalarAsync(token));
        command.Parameters[0].Value = new TimeOnly(1L);
        await Assert.ThrowsAsync<ArgumentException>(() => command.ExecuteScalarAsync(token));
        command.Parameters[0].Value = TimeSpan.Zero;
        Assert.Equal(TimeSpan.Zero, await command.ExecuteScalarAsync(token));
        await command.UnprepareAsync(token);
    });

    [LiveFact]
    public Task PgTimeTzServerRejectsOffsetsOutsideTheSupportedWholeSecondBound() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        foreach (var text in new[] { "12:00:00+16", "12:00:00-16" })
        {
            using var command = new KingbaseCommand("SELECT CAST(@p AS timetz)", connection);
            command.Parameters.AddWithValue("p", text);
            var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token));
            Assert.StartsWith("22", error.SqlState);
        }
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    [LiveFact]
    public Task PgPoolReusesPhysicalSessionAndRestoresSettings() => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.Pooling = true;
        settings.MinPoolSize = 0;
        settings.MaxPoolSize = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        int firstPid;
        object? baseline;
        using (var first = await source.OpenConnectionAsync(token))
        {
            firstPid = Assert.IsType<int>(await Scalar(first, "SELECT pg_catalog.pg_backend_pid()", token));
            baseline = await Scalar(first, "SHOW application_name", token);
            await Scalar(first, "SELECT pg_catalog.set_config('application_name','kb-live-dirty',false)", token);
            Assert.Equal("kb-live-dirty", await Scalar(first, "SHOW application_name", token));
        }
        using var second = await source.OpenConnectionAsync(token);
        Assert.Equal(firstPid, await Scalar(second, "SELECT pg_catalog.pg_backend_pid()", token));
        Assert.Equal(baseline, await Scalar(second, "SHOW application_name", token));
    });

    [LiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgTimestampCalendarAndInstantValuesPreparedAndInferred(bool automatic) => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.MaxAutoPrepare = automatic ? 4 : 0; settings.AutoPrepareMinUsages = 2;
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "SELECT pg_catalog.set_config('DateStyle','ISO, MDY',false)", token);
        await Scalar(connection, "SELECT pg_catalog.set_config('TimeZone','UTC',false)", token);
        using var command = new KingbaseCommand("SELECT @t, CAST(@t AS text), @z, CAST(@z AS text)", connection);
        command.Parameters.Add(new KingbaseParameter("t", DBNull.Value) { KingbaseDbType = KingbaseDbType.Timestamp });
        command.Parameters.Add(new KingbaseParameter("z", DBNull.Value) { KingbaseDbType = KingbaseDbType.TimestampTz });
        if (!automatic) await command.PrepareAsync(token);
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.True(reader.IsDBNull(2));
            Assert.Equal(typeof(DateTime), reader.GetFieldType(0)); Assert.Equal(typeof(DateTime), reader.GetFieldType(2));
            Assert.Equal(typeof(KingbaseTimestamp), reader.GetProviderSpecificFieldType(0)); Assert.Equal(typeof(KingbaseTimestampTz), reader.GetProviderSpecificFieldType(2));
        }
        foreach (var row in new[] { ("0001-01-01 00:00:00", "0001-01-01 00:00:00+00"),
            ("1582-10-10 12:34:56.123456", "1582-10-10 12:34:56.123456+00"),
            ("2000-02-29 00:00:00.000001", "2000-02-29 00:00:00.000001+00"),
            ("9999-12-31 23:59:59.999999", "9999-12-31 23:59:59.999999+00"),
            ("10000-01-01 00:00:00", "10000-01-01 00:00:00+00"),
            ("294276-12-31 23:59:59.999999", "294276-12-31 23:59:59.999999+00"),
            ("0001-02-29 12:34:56.123456 BC", "0001-02-29 12:34:56.123456+00 BC"),
            ("4714-11-24 00:00:00 BC", "4714-11-24 00:00:00+00 BC"), ("infinity", "infinity"), ("-infinity", "-infinity") })
        {
            var timestamp = KingbaseTimestamp.Parse(row.Item1); var instant = KingbaseTimestampTz.Parse(row.Item2);
            command.Parameters[0].Value = timestamp; command.Parameters[1].Value = instant;
            using var reader = await command.ExecuteReaderAsync(token); Assert.True(reader.Read());
            Assert.Equal(row.Item1, reader.GetString(1)); Assert.Equal(row.Item2, reader.GetString(3));
            Assert.Equal(timestamp, reader.GetFieldValue<KingbaseTimestamp>(0)); Assert.Equal(instant, reader.GetFieldValue<KingbaseTimestampTz>(2));
            if (timestamp.IsFinite && timestamp.Date.Year is >= 1 and <= 9999)
            {
                Assert.Equal(timestamp.ToDateTime(), reader.GetDateTime(0)); Assert.Equal(DateTimeKind.Unspecified, reader.GetDateTime(0).Kind);
                Assert.Equal(instant.ToDateTime(), reader.GetDateTime(2)); Assert.Equal(DateTimeKind.Utc, reader.GetDateTime(2).Kind);
                Assert.Equal(TimeSpan.Zero, reader.GetFieldValue<DateTimeOffset>(2).Offset);
            }
            else { Assert.Throws<OverflowException>(() => reader.GetValue(0)); Assert.Throws<OverflowException>(() => reader.GetValue(2)); }
        }
        await command.UnprepareAsync(token);
        using var inferred = new KingbaseCommand("SELECT @p, CAST(@p AS text)", connection);
        inferred.Parameters.AddWithValue("p", new DateTime(2024, 2, 29, 12, 34, 56).AddTicks(10));
        if (!automatic) await inferred.PrepareAsync(token);
        foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Utc })
        {
            inferred.Parameters[0].Value = DateTime.SpecifyKind(new DateTime(2024, 2, 29, 12, 34, 56).AddTicks(10), kind);
            using var reader = await inferred.ExecuteReaderAsync(token); Assert.True(reader.Read());
            Assert.Equal(kind == DateTimeKind.Utc ? "timestamptz" : "timestamp", reader.GetDataTypeName(0)); Assert.Equal(kind, reader.GetDateTime(0).Kind);
            Assert.Equal(kind == DateTimeKind.Utc ? "2024-02-29 12:34:56.000001+00" : "2024-02-29 12:34:56.000001", reader.GetString(1));
        }
        inferred.Parameters[0].Value = new DateTimeOffset(2024, 3, 1, 2, 0, 0, TimeSpan.FromHours(14));
        Assert.Equal(new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Utc), await inferred.ExecuteScalarAsync(token));
        inferred.Parameters[0].Value = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Local);
        await Assert.ThrowsAsync<NotSupportedException>(() => inferred.ExecuteScalarAsync(token));
        inferred.Parameters[0].Value = new DateTime(2024, 1, 1).AddTicks(1);
        await Assert.ThrowsAsync<ArgumentException>(() => inferred.ExecuteScalarAsync(token));
        inferred.Parameters[0].Value = new DateTime(2024, 1, 1);
        Assert.Equal(new DateTime(2024, 1, 1), await inferred.ExecuteScalarAsync(token));
    });

    [LiveFact]
    public Task PgTimestampTzNumericDisplayOffsetsNormalizeDstSecondsAndEras() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "SELECT pg_catalog.set_config('DateStyle','ISO, YMD',false)", token);
        foreach (var row in new[] { ("UTC", "2024-03-10 06:59:59.999999+00", "2024-03-10 06:59:59.999999+00"),
            ("Asia/Shanghai", "2024-03-10 06:59:59.999999+00", "2024-03-10 14:59:59.999999+08"),
            ("America/New_York", "2024-03-10 06:59:59.999999+00", "2024-03-10 01:59:59.999999-05"),
            ("America/New_York", "2024-03-10 07:00:00+00", "2024-03-10 03:00:00-04"),
            ("America/New_York", "2024-11-03 05:30:00+00", "2024-11-03 01:30:00-04"),
            ("America/New_York", "2024-11-03 06:30:00+00", "2024-11-03 01:30:00-05"),
            ("America/New_York", "2024-07-01 12:00:00+00", "2024-07-01 08:00:00-04"),
            ("America/New_York", "2024-01-01 12:00:00+00", "2024-01-01 07:00:00-05"),
            ("Africa/Monrovia", "1970-01-01 00:00:00+00", "1969-12-31 23:15:30-00:44:30"),
            ("ABC-01", "0001-12-31 23:00:00+00 BC", "0001-01-01 00:00:00+01"),
            ("ABC+01", "0001-01-01 00:00:00+00", "0001-12-31 23:00:00-01 BC") })
        {
            await Scalar(connection, "SELECT pg_catalog.set_config('TimeZone','" + row.Item1 + "',false)", token);
            using var command = new KingbaseCommand("SELECT TIMESTAMPTZ '" + row.Item2 + "', CAST(TIMESTAMPTZ '" + row.Item2 + "' AS text)", connection);
            using var reader = await command.ExecuteReaderAsync(token); Assert.True(reader.Read());
            Assert.Equal(row.Item3, reader.GetString(1)); Assert.Equal(row.Item2, reader.GetFieldValue<KingbaseTimestampTz>(0).ToString());
            var value = KingbaseTimestampTz.Parse(row.Item2);
            if (value.UtcValue.Date.Year > 0)
            {
                Assert.Equal(value.ToDateTime(), reader.GetDateTime(0)); Assert.Equal(DateTimeKind.Utc, reader.GetDateTime(0).Kind);
                Assert.Equal(value.ToDateTimeOffset(), reader.GetFieldValue<DateTimeOffset>(0));
            }
            else Assert.Throws<OverflowException>(() => reader.GetDateTime(0));
        }
    });

    [LiveFact]
    public Task PgTimestampFormatRejectionRecoveryAndPoolTimezoneReset() => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build(); using var connection = await source.OpenConnectionAsync(token);
        var baselineZone = await Scalar(connection, "SHOW TimeZone", token); var baselineStyle = await Scalar(connection, "SHOW DateStyle", token);
        var pid = await Scalar(connection, "SELECT pg_catalog.pg_backend_pid()", token);
        await Scalar(connection, "SELECT pg_catalog.set_config('TimeZone','America/New_York',false)", token);
        using var command = new KingbaseCommand("SELECT @t, @z, CAST(@t AS text)", connection);
        command.Parameters.AddWithValue("t", new DateTime(2024, 2, 3, 12, 34, 56));
        command.Parameters.AddWithValue("z", new DateTime(2024, 2, 3, 12, 34, 56, DateTimeKind.Utc));
        await command.PrepareAsync(token);
        foreach (var style in new[] { "SQL, DMY", "German, DMY" })
        {
            await Scalar(connection, "SELECT pg_catalog.set_config('DateStyle','" + style + "',false)", token);
            using var reader = await command.ExecuteReaderAsync(token); Assert.True(reader.Read());
            Assert.Equal(style == "SQL, DMY" ? "03/02/2024 12:34:56" : "03.02.2024 12:34:56", reader.GetString(2));
            Assert.Throws<NotSupportedException>(() => reader.GetDateTime(0)); Assert.Throws<NotSupportedException>(() => reader.GetFieldValue<KingbaseTimestampTz>(1));
        }
        await Scalar(connection, "SELECT pg_catalog.set_config('DateStyle','ISO, DMY',false)", token);
        Assert.Equal(new DateTime(2024, 2, 3, 12, 34, 56), await command.ExecuteScalarAsync(token));
        await connection.CloseAsync(); await connection.OpenAsync(token);
        Assert.Equal(pid, await Scalar(connection, "SELECT pg_catalog.pg_backend_pid()", token));
        Assert.Equal(baselineZone, await Scalar(connection, "SHOW TimeZone", token)); Assert.Equal(baselineStyle, await Scalar(connection, "SHOW DateStyle", token));
    });

    [LiveTheory(writes: true)]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public Task PgTransactionCommitRollbackAndSavepoint(IsolationLevel isolation) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_live_transaction (id int)", connection))
            await create.ExecuteNonQueryAsync(token);
        using (var transaction = await connection.BeginTransactionAsync(isolation, token))
        {
            using var insert = new KingbaseCommand("INSERT INTO kb_live_transaction VALUES (1)", connection) { Transaction = transaction };
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
            await transaction.SaveAsync("kb_live_save", token);
            insert.CommandText = "INSERT INTO kb_live_transaction VALUES (2)";
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
            await transaction.RollbackAsync("kb_live_save", token);
            await transaction.ReleaseAsync("kb_live_save", token);
            await transaction.CommitAsync(token);
        }
        Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM kb_live_transaction", token));
        using (var transaction = await connection.BeginTransactionAsync(isolation, token))
        {
            using var delete = new KingbaseCommand("DELETE FROM kb_live_transaction", connection) { Transaction = transaction };
            Assert.Equal(1, await delete.ExecuteNonQueryAsync(token));
            await transaction.RollbackAsync(token);
        }
        Assert.Equal(1, await Scalar(connection, "SELECT id FROM kb_live_transaction", token));
        // The unpooled physical connection owns the temporary table and removes it on disposal.
    });

    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    {
        using var command = new KingbaseCommand(sql, connection);
        return await command.ExecuteScalarAsync(token);
    }
}
