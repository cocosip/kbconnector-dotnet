namespace KingbaseConnector.IntegrationTests;

public sealed class NativeTemporalLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task NativeCalendarColumnsPreserveDeclaredPrecisionAndServerRounding(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        var definition = mode switch
        {
            KingbaseCompatibilityMode.Oracle => "a sys.date",
            KingbaseCompatibilityMode.MySql => "a sys.date,b sys.datetime(6),c sys.timestamp(6),d sys.time(6),e sys.year",
            _ => "a sys.date,b sys.datetime,c sys.datetime2(7),d sys.smalldatetime,e sys.time(7)"
        };
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_live_native_calendar(" + definition + ")", connection)) await create.ExecuteNonQueryAsync(token);
        var date = new DateTime(2024, 2, 29); var clock = date.AddHours(13).AddMinutes(14).AddSeconds(30);
        (KingbaseDbType Type, object Value)[] inputs = mode switch
        {
            KingbaseCompatibilityMode.Oracle => new (KingbaseDbType Type, object Value)[] { (KingbaseDbType.OracleDate, clock) },
            KingbaseCompatibilityMode.MySql => new (KingbaseDbType, object)[] { (KingbaseDbType.MySqlDate, date), (KingbaseDbType.MySqlDateTime, clock.AddTicks(1234560)), (KingbaseDbType.MySqlTimestamp, clock.AddTicks(1234560)), (KingbaseDbType.MySqlTime, -TimeSpan.FromHours(25) - TimeSpan.FromTicks(1234560)), (KingbaseDbType.MySqlYear, 2155) },
            _ => new (KingbaseDbType, object)[] { (KingbaseDbType.SqlServerDate, date), (KingbaseDbType.SqlServerDateTime, clock.AddMilliseconds(123)), (KingbaseDbType.SqlServerDateTime2, clock.AddTicks(1234567)), (KingbaseDbType.SqlServerSmallDateTime, clock), (KingbaseDbType.SqlServerTime, TimeSpan.FromTicks(TimeSpan.TicksPerDay - 1)) }
        };
        using var insert = new KingbaseCommand("INSERT INTO kb_live_native_calendar VALUES(" + string.Join(",", Enumerable.Range(0, inputs.Length).Select(index => ":p" + index)) + ")", connection);
        for (var i = 0; i < inputs.Length; i++) insert.Parameters.Add(new KingbaseParameter("p" + i, inputs[i].Value) { KingbaseDbType = inputs[i].Type });
        await insert.PrepareAsync(token); Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
        foreach (var parameter in insert.Parameters.Cast<KingbaseParameter>()) parameter.Value = DBNull.Value;
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
        using var select = new KingbaseCommand("SELECT * FROM kb_live_native_calendar ORDER BY a NULLS LAST", connection);
        using var reader = await select.ExecuteReaderAsync(token); Assert.True(reader.Read());
        for (var i = 0; i < inputs.Length; i++) Assert.Equal(mode == KingbaseCompatibilityMode.SqlServer && i == 3 ? date.AddHours(13).AddMinutes(15) : inputs[i].Value, reader.GetValue(i));
        Assert.True(reader.Read()); for (var i = 0; i < inputs.Length; i++) Assert.True(reader.IsDBNull(i)); Assert.False(reader.Read());
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "UTC")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "America/New_York")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "Asia/Tokyo")]
    [InlineData(KingbaseCompatibilityMode.Oracle, "America/New_York")]
    public Task NativeWallClocksDoNotApplyMachineTimezoneOrFixedOffset(KingbaseCompatibilityMode mode, string zone) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Timezone = zone; using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        var value = new DateTime(2024, 11, 3, 1, 30, 0, DateTimeKind.Unspecified);
        using var command = new KingbaseCommand("SELECT :p", connection);
        command.Parameters.Add(new KingbaseParameter("p", value) { KingbaseDbType = mode == KingbaseCompatibilityMode.Oracle ? KingbaseDbType.OracleDate : KingbaseDbType.SqlServerDateTime2 });
        Assert.Equal(value, await command.ExecuteScalarAsync(token));
        command.Parameters[0].Value = DateTime.SpecifyKind(value, DateTimeKind.Utc); await Assert.ThrowsAsync<InvalidCastException>(() => command.ExecuteScalarAsync(token));
        command.Parameters[0].Value = value; Assert.Equal(value, await command.ExecuteScalarAsync(token));
    });

    [LiveFact]
    public Task MySqlDurationBoundsAndServerRejectedZeroValuesStayExplicit() => LiveProfile.Run(KingbaseCompatibilityMode.MySql, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT :p", connection);
        command.Parameters.Add(new KingbaseParameter("p", TimeSpan.Zero) { KingbaseDbType = KingbaseDbType.MySqlTime });
        var maximum = TimeSpan.FromHours(838) + TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59);
        foreach (var value in new[] { -maximum, -TimeSpan.FromTicks(10), TimeSpan.Zero, maximum - TimeSpan.FromTicks(10), maximum })
        { command.Parameters[0].Value = value; Assert.Equal(value, await command.ExecuteScalarAsync(token)); }
        command.Parameters[0].Value = maximum + TimeSpan.FromTicks(10); await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => command.ExecuteScalarAsync(token));
        command.Parameters[0].KingbaseDbType = KingbaseDbType.MySqlDate;
        foreach (var value in new[] { default(KingbaseMySqlDate), new KingbaseMySqlDate(2024, 0, 15), new KingbaseMySqlDate(2024, 2, 31) })
        { command.Parameters[0].Value = value; Assert.Equal("22008", (await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token))).SqlState); }
        command.Parameters[0].KingbaseDbType = KingbaseDbType.MySqlYear; command.Parameters[0].Value = 0;
        Assert.Equal("23514", (await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token))).SqlState);
        command.Parameters[0].Value = 1901; Assert.Equal(1901, await command.ExecuteScalarAsync(token));
    });

    [LiveTheory]
    [InlineData("date", KingbaseDbType.MySqlDate)]
    [InlineData("datetime", KingbaseDbType.MySqlDateTime)]
    [InlineData("timestamp", KingbaseDbType.MySqlTimestamp)]
    public Task MySqlNonClrCalendarsHaveLosslessProviderAccess(string name, KingbaseDbType provider) => LiveProfile.Run(KingbaseCompatibilityMode.MySql, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        object value = name == "date" ? new KingbaseMySqlDate(10000, 1, 1) : new KingbaseMySqlDateTime(new KingbaseMySqlDate(10000, 1, 1), TimeSpan.FromHours(13));
        using var command = new KingbaseCommand("SELECT :p", connection); command.Parameters.Add(new KingbaseParameter("p", value) { KingbaseDbType = provider });
        using var reader = await command.ExecuteReaderAsync(token); Assert.True(reader.Read()); Assert.Equal(typeof(DateTime), reader.GetFieldType(0));
        Assert.Throws<OverflowException>(() => reader.GetDateTime(0)); Assert.Equal(value, reader.GetProviderSpecificValue(0));
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.Oracle, "date", "OracleDate")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "date", "SqlServerDate")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "datetime", "SqlServerDateTime")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "datetime2", "SqlServerDateTime2")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "smalldatetime", "SqlServerSmallDateTime")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "time", "SqlServerTime")]
    [InlineData(KingbaseCompatibilityMode.MySql, "date", "MySqlDate")]
    [InlineData(KingbaseCompatibilityMode.MySql, "datetime", "MySqlDateTime")]
    [InlineData(KingbaseCompatibilityMode.MySql, "timestamp", "MySqlTimestamp")]
    [InlineData(KingbaseCompatibilityMode.MySql, "time", "MySqlTime")]
    [InlineData(KingbaseCompatibilityMode.MySql, "year", "MySqlYear")]
    public Task NativeTemporalPreparedValuesPreserveClockAndNull(KingbaseCompatibilityMode mode, string name, string selector) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        var date = new DateTime(2024, 2, 29, 13, 14, 15, DateTimeKind.Unspecified);
        object value = name switch
        {
            "date" => mode == KingbaseCompatibilityMode.Oracle ? date : date.Date,
            "datetime2" => date.AddTicks(1234567),
            "datetime" when mode == KingbaseCompatibilityMode.SqlServer => date.AddMilliseconds(123),
            "smalldatetime" => date.Date.AddHours(13).AddMinutes(14),
            "time" => mode == KingbaseCompatibilityMode.SqlServer ? TimeSpan.FromTicks(TimeSpan.TicksPerDay - 1) : -TimeSpan.FromHours(25) - TimeSpan.FromTicks(1234560),
            "year" => 1901,
            _ => date.AddTicks(1234560)
        };
        Assert.True(Enum.TryParse<KingbaseDbType>(selector, out var provider));
        using var command = new KingbaseCommand("SELECT :p", connection); command.Parameters.Add(new KingbaseParameter("p", value) { KingbaseDbType = provider });
        await command.PrepareAsync(token);
        using (var reader = await command.ExecuteReaderAsync(token))
        { Assert.True(reader.Read()); Assert.Equal(value.GetType(), reader.GetFieldType(0)); Assert.Equal(value, reader.GetValue(0)); if (value is DateTime) Assert.Equal(DateTimeKind.Unspecified, reader.GetDateTime(0).Kind); }
        command.Parameters[0].Value = DBNull.Value; Assert.Same(DBNull.Value, await command.ExecuteScalarAsync(token));
    });
}
