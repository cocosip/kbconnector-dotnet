using System.Data;
using System.Numerics;
using System.Text;

namespace KingbaseConnector.IntegrationTests;

public sealed class NativeScalarLiveTests
{
    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task UnsignedLogicalInputsUseLosslessNativeOrWideBindings(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT :u32,:u64", connection);
        command.Parameters.Add(new KingbaseParameter("u32", uint.MaxValue) { KingbaseDbType = KingbaseDbType.UnsignedInteger, DbType = DbType.UInt32 });
        command.Parameters.Add(new KingbaseParameter("u64", ulong.MaxValue) { DbType = DbType.UInt64 });
        await command.PrepareAsync(token);
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.True(reader.Read()); Assert.Equal(uint.MaxValue, reader.GetFieldValue<uint>(0)); Assert.Equal(ulong.MaxValue, reader.GetFieldValue<ulong>(1));
            Assert.Equal(mode == KingbaseCompatibilityMode.MySql ? typeof(uint) : typeof(long), reader.GetFieldType(0));
            Assert.Equal(mode == KingbaseCompatibilityMode.MySql ? typeof(ulong) : typeof(BigInteger), reader.GetFieldType(1));
        }
        foreach (var parameter in command.Parameters.Cast<KingbaseParameter>()) parameter.Value = DBNull.Value;
        using var nulls = await command.ExecuteReaderAsync(token); Assert.True(nulls.Read()); Assert.True(nulls.IsDBNull(0)); Assert.True(nulls.IsDBNull(1));
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task Signed128PreservesHighBitsAndBounds(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT :v", connection);
        command.Parameters.Add(new KingbaseParameter("v", BigInteger.Zero) { DataTypeName = "pg_catalog.int16" });
        await command.PrepareAsync(token);
        foreach (var value in new[] { -(BigInteger.One << 127), -(BigInteger.One << 64) - 1, -BigInteger.One,
            BigInteger.Zero, (BigInteger.One << 64) + 1, (BigInteger.One << 127) - 1 })
        {
            command.Parameters[0].Value = value;
            using var reader = await command.ExecuteReaderAsync(token); Assert.True(reader.Read());
            Assert.Equal(typeof(BigInteger), reader.GetFieldType(0)); Assert.Equal("int16", reader.GetDataTypeName(0));
            Assert.Equal(value, reader.GetValue(0)); Assert.Equal(value, reader.GetFieldValue<BigInteger>(0));
            Assert.Equal(value, reader.GetProviderSpecificValue(0));
        }
        command.Parameters[0].Value = DBNull.Value;
        Assert.Same(DBNull.Value, await command.ExecuteScalarAsync(token));
        command.Parameters[0].Value = BigInteger.One << 127;
        await Assert.ThrowsAsync<OverflowException>(() => command.ExecuteScalarAsync(token));
        command.Parameters[0].Value = -(BigInteger.One << 127) - 1;
        await Assert.ThrowsAsync<OverflowException>(() => command.ExecuteScalarAsync(token));
        command.Parameters[0].Value = (BigInteger.One << 96) + 7;
        Assert.Equal(command.Parameters[0].Value, await command.ExecuteScalarAsync(token));
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.Oracle, "pg_catalog.tinyint", "signed")]
    [InlineData(KingbaseCompatibilityMode.MySql, "sys.tinyint", "signed")]
    [InlineData(KingbaseCompatibilityMode.MySql, "sys.uint4", "uint4")]
    [InlineData(KingbaseCompatibilityMode.MySql, "sys.uint8", "uint8")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "sys.tinyint", "unsigned")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "sys.bit", "boolean")]
    public Task NativeIntegerAndBooleanRoundTrip(KingbaseCompatibilityMode mode, string name, string kind) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT :v", connection);
        object[] values = kind switch
        {
            "signed" => new object[] { sbyte.MinValue, (sbyte)-1, (sbyte)0, sbyte.MaxValue },
            "unsigned" => new object[] { byte.MinValue, byte.MaxValue },
            "uint4" => new object[] { 0U, 2147483648U, uint.MaxValue },
            "uint8" => new object[] { 0UL, 9223372036854775808UL, ulong.MaxValue },
            _ => new object[] { false, true }
        };
        command.Parameters.Add(new KingbaseParameter("v", values[0]) { DataTypeName = name });
        await command.PrepareAsync(token);
        foreach (var value in values)
        {
            command.Parameters[0].Value = value;
            using var reader = await command.ExecuteReaderAsync(token); Assert.True(reader.Read());
            Assert.Equal(value.GetType(), reader.GetFieldType(0)); Assert.Equal(value, reader.GetValue(0));
            Assert.Equal(value, reader.GetProviderSpecificValue(0)); Assert.Equal(name.Split('.')[1], reader.GetDataTypeName(0));
        }
        command.Parameters[0].Value = DBNull.Value; Assert.Same(DBNull.Value, await command.ExecuteScalarAsync(token));
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.Oracle, "varcharbyte")]
    [InlineData(KingbaseCompatibilityMode.Oracle, "bpcharbyte")]
    [InlineData(KingbaseCompatibilityMode.MySql, "varcharbyte")]
    [InlineData(KingbaseCompatibilityMode.MySql, "bpcharbyte")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "varcharbyte")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "bpcharbyte")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "nchar")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "nvarchar")]
    public Task NativeCharacterColumnsPreserveUnicodeAndPadding(KingbaseCompatibilityMode mode, string name) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        using var connection = await source.OpenConnectionAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_live_native_scalar(value sys." + name + "(24))", connection))
            await create.ExecuteNonQueryAsync(token);
        var value = "新😀 ' \\ ";
        using var insert = new KingbaseCommand("INSERT INTO kb_live_native_scalar VALUES(:v)", connection);
        insert.Parameters.Add(new KingbaseParameter("v", value) { DataTypeName = "sys." + name });
        await insert.PrepareAsync(token); Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
        insert.Parameters[0].Value = DBNull.Value; Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
        using var select = new KingbaseCommand("SELECT value FROM kb_live_native_scalar ORDER BY value NULLS LAST", connection);
        using var reader = await select.ExecuteReaderAsync(token); Assert.True(reader.Read());
        var padding = name == "bpcharbyte" ? 24 - Encoding.UTF8.GetByteCount(value) : name == "nchar" ? 24 - (value.Length - value.Count(char.IsHighSurrogate)) : 0;
        var expected = value + new string(' ', padding);
        Assert.Equal(typeof(string), reader.GetFieldType(0)); Assert.Equal(name, reader.GetDataTypeName(0));
        Assert.Equal(expected, reader.GetString(0)); Assert.Equal(expected, reader.GetTextReader(0).ReadToEnd());
        var chars = new char[expected.Length]; Assert.Equal(expected.Length, reader.GetChars(0, 0, chars, 0, chars.Length)); Assert.Equal(expected, new string(chars));
        Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.Equal(typeof(string), reader.GetFieldType(0));
    });

    [LiveFact]
    public Task SqlServerStringLiteralsHaveNativeStringResults() => LiveProfile.Run(KingbaseCompatibilityMode.SqlServer, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT 'ok'", connection); Assert.Equal("ok", await command.ExecuteScalarAsync(token));
    });
}
