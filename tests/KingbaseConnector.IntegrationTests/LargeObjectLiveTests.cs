using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class LargeObjectLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task PoolResetRollsBackObjectsAndOldHandlesCannotCloseANewBorrowersDescriptor(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1; using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        KingbaseLargeObjectStream stale; uint rolledBack; int previous;
        using (var connection = await source.OpenConnectionAsync(token))
        {
            using (var pid = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", connection)) previous = (int)(await pid.ExecuteScalarAsync(token))!;
            using var transaction = await connection.BeginTransactionAsync(token); var manager = new KingbaseLargeObjectManager(connection);
            rolledBack = await manager.CreateAsync(cancellationToken: token); stale = await manager.OpenReadWriteAsync(rolledBack, token); stale.WriteByte(11);
            await connection.CloseAsync(); Assert.False(stale.CanRead); Assert.Null(transaction.Connection);
        }
        using var next = await source.OpenConnectionAsync(token);
        using (var pid = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", next)) Assert.Equal(previous, await pid.ExecuteScalarAsync(token));
        using var currentTransaction = await next.BeginTransactionAsync(token);
        using (var exists = new KingbaseCommand("SELECT count(*)::pg_catalog.int4 FROM pg_catalog.pg_largeobject_metadata WHERE oid=:p0", next) { Transaction = currentTransaction })
        { exists.Parameters.AddWithValue("p0", rolledBack); Assert.Equal(0, await exists.ExecuteScalarAsync(token)); }
        var currentManager = new KingbaseLargeObjectManager(next); var oid = await currentManager.CreateAsync(cancellationToken: token); using var current = await currentManager.OpenReadWriteAsync(oid, token);
        stale.Dispose(); await stale.DisposeAsync(); current.WriteByte(22); current.Position = 0; Assert.Equal(22, current.ReadByte()); await currentTransaction.RollbackAsync(token);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task LargeObjectsRoundTripMultipleChunksAndKeep64BitCursorAndLength(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); var manager = new KingbaseLargeObjectManager(connection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateAsync(cancellationToken: token));
        using var transaction = await connection.BeginTransactionAsync(token); var oid = await manager.CreateAsync(cancellationToken: token);
        using (var stream = await manager.OpenReadWriteAsync(oid, token))
        {
            var data = Enumerable.Range(0, 2 * 1024 * 1024 + 17).Select(i => (byte)(i % 251)).ToArray(); await stream.WriteAsync(data, 0, data.Length, token);
            Assert.Equal(data.Length, stream.Position); Assert.Equal(data.Length, stream.Length); Assert.Equal(data.Length, stream.Position);
            Assert.Equal(2147483660L, await stream.SeekAsync(2147483660L, SeekOrigin.Begin, token)); Assert.Equal(2147483660L, stream.Position);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], 0, 1, token)); Assert.Equal(2147483660L, stream.Position);
            stream.Position = 0; using var received = new MemoryStream(); await stream.CopyToAsync(received, 7919, token); Assert.Equal(data, received.ToArray());
            await stream.SetLengthAsync(3, token); Assert.Equal(3, stream.Length); Assert.Equal(data.Length, stream.Position);
            Assert.Equal(2, await stream.SeekAsync(-1, SeekOrigin.End, token)); stream.WriteByte(250); stream.Position = 0;
            var shortValue = new byte[9]; Assert.Equal(3, await stream.ReadAsync(shortValue, 0, 9, token)); Assert.Equal(new byte[] { 0, 1, 250 }, shortValue.Take(3)); await stream.FlushAsync(token);
            await Assert.ThrowsAsync<IOException>(() => stream.SeekAsync(-4, SeekOrigin.Begin, token)); Assert.Equal(3, stream.Position);
        }
        await manager.UnlinkAsync(oid, token); await transaction.RollbackAsync(token);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task SavepointRollbackInvalidatesOnlyDescriptorsOpenedInItsRevertedScope(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); using var transaction = await connection.BeginTransactionAsync(token);
        var manager = new KingbaseLargeObjectManager(connection); var oid = manager.Create(); using var root = manager.OpenReadWrite(oid); root.WriteByte(11);
        await transaction.SaveAsync("lob_outer", token); var child = await manager.OpenReadWriteAsync(oid, token); await transaction.SaveAsync("lob_inner", token); var leaf = await manager.OpenReadWriteAsync(oid, token);
        await transaction.ReleaseAsync("lob_inner", token); await transaction.RollbackAsync("lob_outer", token); Assert.True(root.CanRead); Assert.False(child.CanRead); Assert.False(leaf.CanRead);
        using var replacement = await manager.OpenReadWriteAsync(oid, token); child.Dispose(); await leaf.DisposeAsync(); replacement.WriteByte(22); replacement.Position = 0; Assert.Equal(22, replacement.ReadByte());
        root.Position = 0; Assert.Equal(22, root.ReadByte()); using var command = new KingbaseCommand("SELECT 42", connection) { Transaction = transaction }; Assert.Equal(42, await command.ExecuteScalarAsync(token));
        await transaction.RollbackAsync(token); Assert.False(root.CanSeek); Assert.False(replacement.CanRead); await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.ReadAsync(new byte[1], 0, 1, token));
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task UnlinkInvalidatesItsHandlesAndOldStreamsCannotCloseAReusedDescriptor(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); using var transaction = await connection.BeginTransactionAsync(token);
        var manager = new KingbaseLargeObjectManager(connection); var oid = await manager.CreateAsync(cancellationToken: token); var anotherOid = await manager.CreateAsync(cancellationToken: token);
        var old = await manager.OpenReadWriteAsync(oid, token); using var unrelated = await manager.OpenReadWriteAsync(anotherOid, token); await manager.UnlinkAsync(oid, token);
        Assert.False(old.CanWrite); Assert.True(unrelated.CanWrite); Assert.Equal(oid, await manager.CreateAsync(oid, token)); using var replacement = await manager.OpenReadWriteAsync(oid, token);
        old.Dispose(); replacement.WriteByte(77); replacement.Position = 0; Assert.Equal(77, replacement.ReadByte()); unrelated.WriteByte(88); unrelated.Position = 0; Assert.Equal(88, unrelated.ReadByte());
        await transaction.RollbackAsync(token);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ReadOnlyHandlesAndRawRollbackKeepExplicitOwnershipBoundaries(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); using var transaction = await connection.BeginTransactionAsync(token);
        var manager = new KingbaseLargeObjectManager(connection); var oid = await manager.CreateAsync(cancellationToken: token); using var read = await manager.OpenReadAsync(oid, token);
        Assert.True(read.CanRead); Assert.False(read.CanWrite); Assert.Throws<NotSupportedException>(() => read.WriteByte(1)); Assert.Throws<NotSupportedException>(() => read.SetLength(1));
        using (var save = new KingbaseCommand("SAVEPOINT lob_raw", connection) { Transaction = transaction }) await save.ExecuteNonQueryAsync(token);
        using var child = await manager.OpenReadWriteAsync(oid, token);
        using (var rollback = new KingbaseCommand("ROLLBACK TO SAVEPOINT lob_raw", connection) { Transaction = transaction }) await rollback.ExecuteNonQueryAsync(token);
        Assert.False(read.CanRead); Assert.False(child.CanRead); using var reopened = await manager.OpenReadAsync(oid, token); Assert.Equal(0, reopened.Length); Assert.Equal(-1, reopened.ReadByte());
        await transaction.RollbackAsync(token);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task CommitInvalidatesTheOldHandleAndDataCanBeReadAndDeletedInANewTransaction(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); var manager = new KingbaseLargeObjectManager(connection); uint oid = 0;
        try
        {
            using (var transaction = await connection.BeginTransactionAsync(token))
            {
                oid = await manager.CreateAsync(cancellationToken: token); using var stream = await manager.OpenReadWriteAsync(oid, token); stream.WriteByte(123);
                await transaction.CommitAsync(token); Assert.False(stream.CanRead); await Assert.ThrowsAsync<InvalidOperationException>(() => stream.ReadAsync(new byte[1], 0, 1, token));
            }
            using (var next = await connection.BeginTransactionAsync(token))
            { using var stream = await manager.OpenReadAsync(oid, token); Assert.Equal(123, stream.ReadByte()); await next.RollbackAsync(token); }
        }
        finally
        {
            if (oid != 0)
            {
                using var cleanupToken = new CancellationTokenSource(TimeSpan.FromSeconds(15)); using var cleanup = new KingbaseConnection(settings.ConnectionString); await cleanup.OpenAsync(cleanupToken.Token);
                using var transaction = await cleanup.BeginTransactionAsync(cleanupToken.Token);
                try { await new KingbaseLargeObjectManager(cleanup).UnlinkAsync(oid, cleanupToken.Token); await transaction.CommitAsync(cleanupToken.Token); }
                catch (KingbaseException error) when (error.SqlState == "42704") { await transaction.RollbackAsync(cleanupToken.Token); }
            }
        }
    });
}
