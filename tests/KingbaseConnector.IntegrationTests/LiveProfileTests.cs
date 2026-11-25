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

    [LiveTheory]
    [InlineData("postgres", false)] [InlineData("postgres", true)]
    [InlineData("postgres_verbose", false)] [InlineData("postgres_verbose", true)]
    [InlineData("sql_standard", false)] [InlineData("sql_standard", true)]
    [InlineData("iso_8601", false)] [InlineData("iso_8601", true)]
    public Task PgIntervalAllStylesPreserveCalendarAndClockThroughPreparation(string style, bool automatic) => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.MaxAutoPrepare = automatic ? 1 : 0; settings.AutoPrepareMinUsages = 2;
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "SET IntervalStyle TO '" + style + "'", token);
        using var command = new KingbaseCommand("SELECT @p,@n,CAST(EXTRACT(year FROM @p) AS int4),CAST(EXTRACT(month FROM @p) AS int4)," +
            "CAST(EXTRACT(day FROM @p) AS int4),CAST(EXTRACT(hour FROM @p) AS int8),CAST(EXTRACT(minute FROM @p) AS int4)," +
            "CAST(EXTRACT(second FROM @p)*1000000 AS int8)", connection);
        command.Parameters.AddWithValue("p", default(KingbaseInterval));
        command.Parameters.Add(new KingbaseParameter("n", DBNull.Value) { KingbaseDbType = KingbaseDbType.Interval });
        if (!automatic) await command.PrepareAsync(token);
        foreach (var value in new[]
        {
            default(KingbaseInterval), new(14, 3, 14706123456), new(-14, -3, -14706123456),
            new(14, -3, -1), new(-14, 3, 1), new(0, 0, 86400000000), new(0, 1, 0),
            new(0, -3, -14706123456), new(0, 0, -1),
            new(int.MaxValue, int.MaxValue, 0), new(int.MinValue, 0, 0), new(0, int.MinValue + 1, 0),
            new(0, 0, 7730941129200000001), new(0, 0, -7730941129200000001)
        })
        {
            command.Parameters[0].Value = value;
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var reader = await command.ExecuteReaderAsync(token); Assert.True(await reader.ReadAsync(token));
                Assert.Equal(typeof(KingbaseInterval), reader.GetFieldType(0)); Assert.Equal(typeof(KingbaseInterval), reader.GetProviderSpecificFieldType(0));
                Assert.Equal(value, reader.GetValue(0)); Assert.Equal(value, reader.GetFieldValue<KingbaseInterval>(0));
                Assert.Equal(value, reader.GetProviderSpecificValue(0)); Assert.True(reader.IsDBNull(1)); Assert.Same(DBNull.Value, reader.GetValue(1));
                Assert.Equal(value.Months / 12, reader.GetInt32(2)); Assert.Equal(value.Months % 12, reader.GetInt32(3));
                Assert.Equal(value.Days, reader.GetInt32(4)); Assert.Equal(value.Microseconds / 3600000000L, reader.GetInt64(5));
                Assert.Equal((int)(value.Microseconds / 60000000L % 60), reader.GetInt32(6)); Assert.Equal(value.Microseconds % 60000000L, reader.GetInt64(7));
                if (value.Months != 0) Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<TimeSpan>(0));
                else if (Math.Abs(value.Days) > 10675199 || Math.Abs(value.Microseconds) > long.MaxValue / 10)
                    Assert.Throws<OverflowException>(() => reader.GetFieldValue<TimeSpan>(0));
                else Assert.Equal(value.ToTimeSpan(), reader.GetFieldValue<TimeSpan>(0));
            }
        }
        await command.UnprepareAsync(token);
        using var inferred = new KingbaseCommand("SELECT @p", connection); inferred.Parameters.AddWithValue("p", TimeSpan.FromTicks(-10));
        await inferred.PrepareAsync(token); Assert.Equal(new KingbaseInterval(0, 0, -1), await inferred.ExecuteScalarAsync(token));
        inferred.Parameters[0].Value = TimeSpan.FromTicks(1);
        await Assert.ThrowsAsync<ArgumentException>(() => inferred.ExecuteScalarAsync(token));
        inferred.Parameters[0].Value = TimeSpan.FromTicks(10); inferred.Parameters[0].DbType = DbType.Time;
        Assert.Equal(TimeSpan.FromTicks(10), await inferred.ExecuteScalarAsync(token));
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    [LiveFact]
    public Task PgIntervalPoolResetRestoresStyleAndDoesNotCollapseCalendarDays() => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        using var connection = source.CreateConnection(); await connection.OpenAsync(token);
        var pid = await Scalar(connection, "SELECT pg_catalog.pg_backend_pid()", token);
        var style = await Scalar(connection, "SHOW IntervalStyle", token);
        await Scalar(connection, "SET IntervalStyle TO 'postgres_verbose'", token);
        await Scalar(connection, "SET DateStyle TO 'SQL, DMY'", token);
        await Scalar(connection, "SET TimeZone TO 'America/New_York'", token);
        using (var command = new KingbaseCommand("SELECT '1 day'::interval,'24 hours'::interval,'-3 days -4 hours -5 minutes -6.123456 seconds'::interval", connection))
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.True(reader.Read()); Assert.Equal(new KingbaseInterval(0, 1, 0), reader.GetValue(0));
            Assert.Equal(new KingbaseInterval(0, 0, 86400000000), reader.GetValue(1));
            Assert.Equal(new KingbaseInterval(0, -3, -14706123456), reader.GetValue(2));
        }
        connection.Close(); await connection.OpenAsync(token);
        Assert.Equal(pid, await Scalar(connection, "SELECT pg_catalog.pg_backend_pid()", token));
        Assert.Equal(style, await Scalar(connection, "SHOW IntervalStyle", token));
        using var check = new KingbaseCommand("SELECT '1 mon -2 days -0.000001 seconds'::interval", connection);
        Assert.Equal(new KingbaseInterval(1, -2, -1), await check.ExecuteScalarAsync(token));
    });

    [LiveFact]
    public Task PgIntervalServerTextBoundaryFailuresRetainSessionAndProviderRecovery() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT @p", connection);
        command.Parameters.AddWithValue("p", new KingbaseInterval(0, 0, long.MaxValue));
        // This server's text input rejects clock hours beyond its integer field bound.
        Assert.Equal("22015", (await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token))).SqlState);
        command.Parameters[0].Value = new KingbaseInterval(0, 0, long.MinValue);
        Assert.Equal("22015", (await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token))).SqlState);
        command.Parameters[0].Value = new KingbaseInterval(0, int.MinValue, 0);
        await Scalar(connection, "SET IntervalStyle TO 'postgres_verbose'", token);
        Assert.Throws<FormatException>(() => command.ExecuteScalar());
        await Scalar(connection, "SET IntervalStyle TO 'iso_8601'", token);
        Assert.Equal(new KingbaseInterval(0, int.MinValue, 0), await command.ExecuteScalarAsync(token));
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    [LiveTheory(writes: true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task PgSequentialBatchPartialPersistenceAndExplicitRollback(bool transactional, bool prepare) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_live_batch (id int)", token);
        using var transaction = transactional ? await connection.BeginTransactionAsync(token) : null;
        using var batch = new KingbaseBatch(connection, transaction);
        batch.BatchCommands.Add(new("INSERT INTO kb_live_batch VALUES (1)"));
        batch.BatchCommands.Add(new("SELECT 1/0"));
        batch.BatchCommands.Add(new("INSERT INTO kb_live_batch VALUES (3)"));
        if (prepare) await batch.PrepareAsync(token);
        var error = await Assert.ThrowsAsync<KingbaseException>(() => batch.ExecuteNonQueryAsync(token));
        Assert.Equal("22012", error.SqlState); Assert.Same(batch.BatchCommands[1], error.BatchCommand);
        Assert.Equal(1, batch.FailedCommandIndex); Assert.Equal(1, batch.BatchCommands[0].RecordsAffected);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.Failed, batch.BatchCommands[1].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[2].ExecutionStatus);
        if (transaction is not null)
        {
            Assert.Same(connection, transaction.Connection);
            await transaction.RollbackAsync(token);
        }
        Assert.Equal(transactional ? 0 : 1, await Scalar(connection, "SELECT coalesce(sum(id),0)::int FROM kb_live_batch", token));
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    [LiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgSequentialBatchTypedNullFreshValuesAndPreparedResults(bool automatic) => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.MaxAutoPrepare = automatic ? 3 : 0; settings.AutoPrepareMinUsages = 1;
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var batch = connection.CreateBatch();
        var integer = new KingbaseBatchCommand("SELECT @p"); integer.Parameters.AddWithValue("p", 41); batch.BatchCommands.Add(integer);
        var nullable = new KingbaseBatchCommand("SELECT @p::text"); nullable.Parameters.Add(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = KingbaseDbType.Text }); batch.BatchCommands.Add(nullable);
        var interval = new KingbaseBatchCommand("SELECT @p"); interval.Parameters.AddWithValue("p", new KingbaseInterval(14, -3, -1)); batch.BatchCommands.Add(interval);
        if (!automatic) await batch.PrepareAsync(token);
        foreach (var value in new[] { 41, 42 })
        {
            integer.Parameters[0].Value = value;
            using var reader = await batch.ExecuteReaderAsync(token);
            Assert.True(reader.Read()); Assert.Equal(value, reader.GetInt32(0));
            Assert.True(reader.NextResult()); Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.True(reader.NextResult()); Assert.True(reader.Read()); Assert.Equal(new KingbaseInterval(14, -3, -1), reader.GetFieldValue<KingbaseInterval>(0));
            Assert.False(reader.NextResult()); Assert.Null(batch.FailedCommandIndex);
            Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), item => Assert.Equal(KingbaseBatchCommandStatus.Succeeded, item.ExecutionStatus));
        }
    });

    [LiveFact(writes: true)]
    public Task PgBatchAssignedTransactionCompletionRejectsLaterAutocommit() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_live_batch_tx (id int)", token);
        using var tx = await connection.BeginTransactionAsync(token);
        using var batch = new KingbaseBatch(connection, tx);
        batch.BatchCommands.Add(new("ROLLBACK")); batch.BatchCommands.Add(new("INSERT INTO kb_live_batch_tx VALUES (1)"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteNonQueryAsync(token));
        Assert.Equal(1, batch.FailedCommandIndex); Assert.Null(tx.Connection);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.Failed, batch.BatchCommands[1].ExecutionStatus);
        Assert.Equal(0, await Scalar(connection, "SELECT count(*)::int FROM kb_live_batch_tx", token));
    });

    [LiveFact(writes: true)]
    public Task PgBatchUnsupportedReturningRetainsAcknowledgedCount() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_live_batch_count (id int)", token);
        await Scalar(connection, "INSERT INTO kb_live_batch_count VALUES (0)", token);
        using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new("UPDATE kb_live_batch_count SET id=id+1 RETURNING ARRAY[id]"));
        batch.BatchCommands.Add(new("UPDATE kb_live_batch_count SET id=id+100"));
        await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteNonQueryAsync(token));
        Assert.Equal(0, batch.FailedCommandIndex); Assert.Equal(1, batch.BatchCommands[0].RecordsAffected);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus);
        Assert.Equal(1, await Scalar(connection, "SELECT id FROM kb_live_batch_count", token));
    });

    [LiveFact(writes: true)]
    public Task PgBatchScalarConversionStopsBeforeLaterWrite() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "SET DateStyle TO 'ISO, MDY'", token);
        await Scalar(connection, "CREATE TEMP TABLE kb_live_batch_scalar (id int)", token);
        using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new("SELECT 'infinity'::date")); batch.BatchCommands.Add(new("INSERT INTO kb_live_batch_scalar VALUES (1)"));
        await Assert.ThrowsAsync<OverflowException>(() => batch.ExecuteScalarAsync(token));
        Assert.Equal(0, batch.FailedCommandIndex);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, batch.BatchCommands[1].ExecutionStatus);
        Assert.Equal(0, await Scalar(connection, "SELECT count(*)::int FROM kb_live_batch_scalar", token));
    });

    [LiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgXmlDocumentsContentAndPreparedTextAccess(bool automatic) => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.MaxAutoPrepare = automatic ? 2 : 0; settings.AutoPrepareMinUsages = 1;
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "SET xmloption = 'content'", token);
        using var count = new KingbaseCommand("SELECT count(*) FROM pg_catalog.pg_prepared_statements WHERE statement = 'SELECT $1, pg_typeof($1)::text, XMLSERIALIZE(CONTENT $1 AS text)'", connection);
        await count.PrepareAsync(token); // Keep the observer outside automatic preparation capacity.
        Assert.Equal(0L, await count.ExecuteScalarAsync(token));
        using var command = new KingbaseCommand("SELECT @p, pg_typeof(@p)::text, XMLSERIALIZE(CONTENT @p AS text)", connection);
        var p = new KingbaseParameter("p", "<root a=\"&amp;\">汉😀</root>") { KingbaseDbType = KingbaseDbType.Xml }; command.Parameters.Add(p);
        if (!automatic) await command.PrepareAsync(token);
        foreach (var text in new[] { (string)p.Value!, "<first/>text<second/>", "", "<root>" + new string('界', 90000) + "</root>" })
        {
            p.Value = text; using var reader = await command.ExecuteReaderAsync(token);
            Assert.Equal(typeof(string), reader.GetFieldType(0)); Assert.Equal("xml", reader.GetDataTypeName(0));
            Assert.True(reader.Read()); Assert.Equal("xml", reader.GetString(1));
            Assert.Equal(text, reader.GetString(0)); Assert.Equal(text, reader.GetString(2));
            Assert.Equal(text.Length, reader.GetChars(0, 0, null, 0, 0));
            using var buffered = reader.GetTextReader(0); reader.Close(); Assert.Equal(text, await buffered.ReadToEndAsync());
            Assert.Equal(1L, await count.ExecuteScalarAsync(token));
        }
    });

    [LiveFact]
    public Task PgXmlEmbeddedDeclarationDoesNotChangeUtf8ProtocolText() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT @p, XMLSERIALIZE(CONTENT @p AS text) LIKE '%<root>汉😀</root>'", connection);
        command.Parameters.Add(new KingbaseParameter("p", "<?xml version=\"1.0\" encoding=\"UTF-16\"?><root>汉😀</root>") { DbType = DbType.Xml });
        using var reader = await command.ExecuteReaderAsync(token); Assert.True(reader.Read());
        Assert.Contains("<root>汉😀</root>", reader.GetString(0)); Assert.True(reader.GetBoolean(1));
        Assert.DoesNotContain("UTF-16", reader.GetString(0), StringComparison.OrdinalIgnoreCase);
    });

    [LiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgXmlTypedNullAndInvalidDocumentRecovery(bool prepare) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "SET xmloption = 'document'", token);
        using var command = new KingbaseCommand("SELECT @p", connection);
        var p = new KingbaseParameter("p", DBNull.Value) { DataTypeName = "pg_catalog.xml", DbType = DbType.Xml }; command.Parameters.Add(p);
        if (prepare) await command.PrepareAsync(token);
        using (var reader = await command.ExecuteReaderAsync(token))
        { Assert.Equal(typeof(string), reader.GetFieldType(0)); Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0)); }
        foreach (var invalid in new[] { "<root>", "<a/><b/>" })
        {
            p.Value = invalid;
            var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token));
            Assert.Contains(error.SqlState, new[] { "2200M", "2200N" }); Assert.Equal(ConnectionState.Open, connection.State);
            p.Value = "<recovered/>"; Assert.Equal("<recovered/>", await command.ExecuteScalarAsync(token));
        }
    });

    [LiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgStandardSchemaCollectionsMatchSessionCatalogAndKeywords(bool async) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        var expectedKeywords = new List<string>();
        using (var command = new KingbaseCommand("SELECT word::text FROM pg_catalog.pg_get_keywords() WHERE catcode = 'R' ORDER BY word", connection))
        using (var reader = await command.ExecuteReaderAsync(token))
            while (reader.Read()) expectedKeywords.Add(reader.GetString(0));
        Assert.Contains("select", expectedKeywords); Assert.Contains("where", expectedKeywords);
        using var collections = async ? await connection.GetSchemaAsync(token) : connection.GetSchema();
        Assert.Equal(18, collections.Rows.Count);
        using var source = async ? await connection.GetSchemaAsync("DataSourceInformation", token) : connection.GetSchema("DataSourceInformation");
        Assert.Equal(connection.ServerVersion, source.Rows[0]["DataSourceProductVersion"]);
        using var types = async ? await connection.GetSchemaAsync("DataTypes", token) : connection.GetSchema("DataTypes");
        var integers = types.Rows.Cast<DataRow>().Single(row => (string)row["TypeName"] == "int4");
        Assert.Equal(await Scalar(connection, "SELECT 'pg_catalog.int4'::regtype::oid", token), integers["TypeOid"]);
        using var words = async ? await connection.GetSchemaAsync("ReservedWords", token) : connection.GetSchema("ReservedWords");
        Assert.Equal(expectedKeywords, words.Rows.Cast<DataRow>().Select(row => (string)row[0]));
        using var restrictions = async ? await connection.GetSchemaAsync("Restrictions", token) : connection.GetSchema("Restrictions");
        Assert.Equal(4, restrictions.Columns.Count); Assert.Equal(51, restrictions.Rows.Count);
        using (var transaction = await connection.BeginTransactionAsync(token))
        {
            using var inTransaction = await connection.GetSchemaAsync("ReservedWords", token);
            Assert.Equal(expectedKeywords, inTransaction.Rows.Cast<DataRow>().Select(row => (string)row[0]));
            Assert.Same(connection, transaction.Connection); await transaction.RollbackAsync(token);
        }
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    [LiveFact]
    public Task PgSchemaPoolReopenReturnsFreshCurrentMetadata() => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        using var connection = await source.OpenConnectionAsync(token);
        var pid = await Scalar(connection, "SELECT pg_catalog.pg_backend_pid()", token);
        using var initial = connection.GetSchema("DataTypes");
        var integerOid = initial.Rows.Cast<DataRow>().Single(row => (string)row["TypeName"] == "int4")["TypeOid"];
        initial.Rows.Clear(); await connection.CloseAsync(); await connection.OpenAsync(token);
        Assert.Equal(pid, await Scalar(connection, "SELECT pg_catalog.pg_backend_pid()", token));
        using var fresh = await connection.GetSchemaAsync("DataTypes", token);
        Assert.Equal(integerOid, fresh.Rows.Cast<DataRow>().Single(row => (string)row["TypeName"] == "int4")["TypeOid"]);
        using var words = await connection.GetSchemaAsync("ReservedWords", token);
        Assert.Contains(words.Rows.Cast<DataRow>(), row => (string)row[0] == "select");
        using var command = new KingbaseCommand("SELECT 1", connection); using var reader = await command.ExecuteReaderAsync(token);
        Assert.Throws<InvalidOperationException>(() => connection.GetSchema()); reader.Close();
        using var collections = connection.GetSchema(); Assert.Equal(18, collections.Rows.Count);
    });

    [LiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgObjectSchemaCollectionsMatchCatalogAndExactRestrictions(bool async) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var schemas = await Schema("Schemas", [connection.Database, "information_schema"]);
        Assert.Single(schemas.Rows.Cast<DataRow>()); Assert.Equal("information_schema", schemas.Rows[0]["SCHEMA_NAME"]);
        using var tables = await Schema("Tables", [null, "information_schema", "columns", "VIEW"]);
        Assert.Single(tables.Rows.Cast<DataRow>()); Assert.Equal("VIEW", tables.Rows[0]["TABLE_TYPE"]);
        using var views = await Schema("Views", [null, "information_schema", "columns"]);
        Assert.Single(views.Rows.Cast<DataRow>()); Assert.Equal("NONE", views.Rows[0]["CHECK_OPTION"]);
        using var columns = await Schema("Columns", [null, "information_schema", "columns"]);
        Assert.Equal(await Scalar(connection, "SELECT count(*)::pg_catalog.int4 FROM information_schema.columns WHERE table_schema = 'information_schema' AND table_name = 'columns'", token), columns.Rows.Count);
        Assert.Equal(Enumerable.Range(1, columns.Rows.Count), columns.Rows.Cast<DataRow>().Select(row => (int)row["ORDINAL_POSITION"]));
        using var selected = await Schema("Columns", [null, "information_schema", "columns", "table_name"]);
        Assert.Single(selected.Rows.Cast<DataRow>()); Assert.Equal(3, selected.Rows[0]["ORDINAL_POSITION"]);
        foreach (var wrong in new[] { "COLUMNS", "col%", "columns' OR 1=1 --", "" })
        {
            using var absent = await Schema("Tables", [null, "information_schema", wrong]);
            Assert.Empty(absent.Rows.Cast<DataRow>());
        }
        using var noCatalog = await Schema("Schemas", [""]);
        Assert.Empty(noCatalog.Rows.Cast<DataRow>());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.GetSchemaAsync("Columns", canceled.Token));
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
        Task<DataTable> Schema(string name, string?[] restrictions) => async ? connection.GetSchemaAsync(name, restrictions, token) : Task.FromResult(connection.GetSchema(name, restrictions));
    });

    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgObjectSchemaReportsQuotedTemporaryColumnsAndTransactionOwnership(bool async) => LiveProfile.RunPg(async (settings, token) =>
    {
        const string tableName = "kb_meta_%_'汉\"Name";
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE \"kb_meta_%_'汉\"\"Name\" (\"MiXeD\" varchar(12) NOT NULL DEFAULT 'sample', amount numeric(12,3), stamp timestamp(4), flag int)", token);
        await Scalar(connection, "CREATE TEMP VIEW \"kb_meta_View汉%\" AS SELECT \"MiXeD\", amount FROM \"kb_meta_%_'汉\"\"Name\"", token);
        var schema = Assert.IsType<string>(await Scalar(connection, "SELECT nspname::text FROM pg_catalog.pg_namespace WHERE oid = pg_catalog.pg_my_temp_schema()", token));
        using var transaction = await connection.BeginTransactionAsync(token);
        using var tables = await Schema("Tables", [connection.Database, schema, tableName, "LOCAL TEMPORARY"]);
        Assert.Single(tables.Rows.Cast<DataRow>()); Assert.Equal(tableName, tables.Rows[0]["TABLE_NAME"]);
        using var columns = await Schema("Columns", [connection.Database, schema, tableName]);
        Assert.Equal(new[] { "MiXeD", "amount", "stamp", "flag" }, columns.Rows.Cast<DataRow>().Select(row => (string)row["COLUMN_NAME"]));
        using var octets = new KingbaseCommand("SELECT character_octet_length::pg_catalog.int8 FROM information_schema.columns WHERE " +
            "table_catalog::pg_catalog.text OPERATOR(pg_catalog.=) @catalog AND table_schema::pg_catalog.text OPERATOR(pg_catalog.=) @schema AND " +
            "table_name::pg_catalog.text OPERATOR(pg_catalog.=) @table AND column_name::pg_catalog.text OPERATOR(pg_catalog.=) @column", connection) { Transaction = transaction };
        octets.Parameters.Add(new KingbaseParameter("catalog", connection.Database) { KingbaseDbType = KingbaseDbType.Text });
        octets.Parameters.Add(new KingbaseParameter("schema", schema) { KingbaseDbType = KingbaseDbType.Text });
        octets.Parameters.Add(new KingbaseParameter("table", tableName) { KingbaseDbType = KingbaseDbType.Text });
        octets.Parameters.Add(new KingbaseParameter("column", "MiXeD") { KingbaseDbType = KingbaseDbType.Text });
        Assert.Equal(12L, columns.Rows[0]["CHARACTER_MAXIMUM_LENGTH"]);
        Assert.Equal(Assert.IsType<long>(await octets.ExecuteScalarAsync(token)), columns.Rows[0]["CHARACTER_OCTET_LENGTH"]);
        Assert.False((bool)columns.Rows[0]["IS_NULLABLE"]); Assert.Contains("sample", (string)columns.Rows[0]["COLUMN_DEFAULT"]);
        Assert.Equal("varchar", columns.Rows[0]["UDT_NAME"]);
        Assert.Equal(12, columns.Rows[1]["NUMERIC_PRECISION"]); Assert.Equal(3, columns.Rows[1]["NUMERIC_SCALE"]);
        Assert.Equal(4, columns.Rows[2]["DATETIME_PRECISION"]);
        Assert.True((bool)columns.Rows[3]["IS_NULLABLE"]); Assert.Equal(DBNull.Value, columns.Rows[3]["COLUMN_DEFAULT"]);
        using var exactColumn = await Schema("Columns", [null, schema, tableName, "MiXeD"]); Assert.Single(exactColumn.Rows.Cast<DataRow>());
        using var wrongCase = await Schema("Columns", [null, schema, tableName, "mixed"]); Assert.Empty(wrongCase.Rows.Cast<DataRow>());
        using var views = await Schema("Views", [null, schema, "kb_meta_View汉%"]);
        Assert.Single(views.Rows.Cast<DataRow>()); Assert.Contains("amount", (string)views.Rows[0]["VIEW_DEFINITION"]);
        Assert.True((bool)views.Rows[0]["IS_UPDATABLE"]);
        columns.Rows.Clear(); using var fresh = await Schema("Columns", [null, schema, tableName]); Assert.Equal(4, fresh.Rows.Count);
        Assert.Same(connection, transaction.Connection); await transaction.RollbackAsync(token);
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
        Task<DataTable> Schema(string name, string?[] restrictions) => async ? connection.GetSchemaAsync(name, restrictions, token) : Task.FromResult(connection.GetSchema(name, restrictions));
    });

    [LiveFact(writes: true)]
    public Task PgObjectSchemaRespectsLocalRoleVisibilityAndRollback() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_metadata_private (id int)", token);
        var schema = Assert.IsType<string>(await Scalar(connection, "SELECT nspname::text FROM pg_catalog.pg_namespace WHERE oid = pg_catalog.pg_my_temp_schema()", token));
        using (var visible = connection.GetSchema("Tables", [null, schema, "kb_metadata_private"])) Assert.Single(visible.Rows.Cast<DataRow>());
        using var transaction = await connection.BeginTransactionAsync(token);
        await Scalar(connection, "SET LOCAL ROLE pg_monitor", token);
        Assert.False(Assert.IsType<bool>(await Scalar(connection, "SELECT pg_catalog.has_table_privilege('pg_temp.kb_metadata_private', 'SELECT')", token)));
        using (var hidden = await connection.GetSchemaAsync("Tables", [null, schema, "kb_metadata_private"], token)) Assert.Empty(hidden.Rows.Cast<DataRow>());
        using (var hidden = await connection.GetSchemaAsync("Columns", [null, schema, "kb_metadata_private"], token)) Assert.Empty(hidden.Rows.Cast<DataRow>());
        var denied = await Assert.ThrowsAsync<KingbaseException>(() => Scalar(connection, "SELECT id FROM pg_temp.kb_metadata_private", token));
        Assert.Equal("42501", denied.SqlState); await transaction.RollbackAsync(token);
        using var recovered = await connection.GetSchemaAsync("Columns", [null, schema, "kb_metadata_private"], token);
        Assert.Single(recovered.Rows.Cast<DataRow>()); Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    {
        using var command = new KingbaseCommand(sql, connection);
        return await command.ExecuteScalarAsync(token);
    }
}
