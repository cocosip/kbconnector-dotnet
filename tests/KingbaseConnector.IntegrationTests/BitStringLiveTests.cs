using System.Collections;
using System.Data.Common;

namespace KingbaseConnector.IntegrationTests;

public sealed class BitStringLiveTests
{
    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task PgBitsPreserveLengthLeadingZerosNullAndPreparation(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        foreach (var name in new[] { "bit", "varbit" })
        {
            using var command = new KingbaseCommand("SELECT :v", connection);
            command.Parameters.Add(new KingbaseParameter("v", Bits("00101")) { DataTypeName = "pg_catalog." + name });
            await command.PrepareAsync(token);
            foreach (var text in new[] { "", "0", "1", "00101", "10000000100000001", new string('0', 256) + "1" })
            {
                command.Parameters[0].Value = Bits(text);
                using var reader = await command.ExecuteReaderAsync(token); Assert.True(reader.Read());
                Assert.Equal(typeof(BitArray), reader.GetFieldType(0)); Assert.Equal(name, reader.GetDataTypeName(0));
                if (mode == KingbaseCompatibilityMode.Oracle && text.Length == 0) Assert.True(reader.IsDBNull(0));
                else { Assert.Equal(text, Text(reader.GetFieldValue<BitArray>(0))); Assert.Equal(text, Text(Assert.IsType<BitArray>(reader.GetProviderSpecificValue(0)))); }
            }
            command.Parameters[0].Value = DBNull.Value; Assert.Same(DBNull.Value, await command.ExecuteScalarAsync(token));
            command.Parameters[0].Value = new BitArray(1024 * 1024 + 1);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => command.ExecuteScalarAsync(token));
            command.Parameters[0].Value = Bits("001"); Assert.Equal("001", Text(Assert.IsType<BitArray>(await command.ExecuteScalarAsync(token))));
        }
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task PgBitColumnsEnforceDeclaredLengthsAndExposeBitCounts(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_live_bits(fixed_bits pg_catalog.bit(5),varying_bits pg_catalog.varbit(9))", connection)) await create.ExecuteNonQueryAsync(token);
        using var insert = new KingbaseCommand("INSERT INTO kb_live_bits VALUES(:f,:v)", connection);
        insert.Parameters.Add(new KingbaseParameter("f", Bits("00101")) { DataTypeName = "pg_catalog.bit" });
        insert.Parameters.Add(new KingbaseParameter("v", Bits("000000001")) { DataTypeName = "pg_catalog.varbit" });
        await insert.PrepareAsync(token); Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
        insert.Parameters[0].Value = Bits("001"); Assert.Equal("22026", (await Assert.ThrowsAsync<KingbaseException>(() => insert.ExecuteNonQueryAsync(token))).SqlState);
        insert.Parameters[0].Value = Bits("001011"); Assert.Equal("22026", (await Assert.ThrowsAsync<KingbaseException>(() => insert.ExecuteNonQueryAsync(token))).SqlState);
        insert.Parameters[0].Value = Bits("00101"); insert.Parameters[1].Value = Bits("0000000001"); Assert.Equal("22001", (await Assert.ThrowsAsync<KingbaseException>(() => insert.ExecuteNonQueryAsync(token))).SqlState);
        insert.Parameters[0].Value = DBNull.Value; insert.Parameters[1].Value = Bits(""); Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
        using var select = new KingbaseCommand("SELECT fixed_bits,varying_bits FROM kb_live_bits ORDER BY fixed_bits NULLS LAST", connection);
        using var reader = await select.ExecuteReaderAsync(token); Assert.Equal(5, reader.GetColumnSchema()[0].ColumnSize); Assert.Equal(9, reader.GetColumnSchema()[1].ColumnSize);
        Assert.True(reader.Read()); Assert.Equal("00101", Text(reader.GetFieldValue<BitArray>(0))); Assert.Equal("000000001", Text(reader.GetFieldValue<BitArray>(1)));
        Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0));
        if (mode == KingbaseCompatibilityMode.Oracle) Assert.True(reader.IsDBNull(1)); else Assert.Equal("", Text(reader.GetFieldValue<BitArray>(1)));
        Assert.False(reader.Read());
    });

    static BitArray Bits(string text) => new(text.Select(c => c == '1').ToArray());
    static string Text(BitArray bits) => new(bits.Cast<bool>().Select(value => value ? '1' : '0').ToArray());
}
