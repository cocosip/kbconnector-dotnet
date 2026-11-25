namespace KingbaseConnector;

/// <summary>Manages OID large objects inside the connection's explicit local transaction.</summary>
public sealed class KingbaseLargeObjectManager
{
    readonly KingbaseConnection _connection;
    public KingbaseLargeObjectManager(KingbaseConnection connection) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    public uint Create(uint oid = 0) => CreateAsync(oid).GetAwaiter().GetResult();
    public Task<uint> CreateAsync(uint oid = 0, CancellationToken cancellationToken = default) => _connection.LargeObjectOperationAsync<uint>(null, async operation =>
    {
        var created = await operation.ScalarAsync<uint>("SELECT pg_catalog.lo_create(:p0)::pg_catalog.oid", oid).ConfigureAwait(false);
        if (created == 0 || (oid != 0 && created != oid)) throw operation.InvalidResult();
        operation.AbandonObjectOnFailure(created); return created;
    }, cancellationToken);
    public KingbaseLargeObjectStream Open(uint oid, FileAccess access = FileAccess.ReadWrite) => OpenAsync(oid, access).GetAwaiter().GetResult();
    public Task<KingbaseLargeObjectStream> OpenAsync(uint oid, FileAccess access = FileAccess.ReadWrite, CancellationToken cancellationToken = default)
    {
        ValidateOid(oid);
        if (access is not (FileAccess.Read or FileAccess.Write or FileAccess.ReadWrite)) throw new ArgumentOutOfRangeException(nameof(access));
        return _connection.LargeObjectOperationAsync<KingbaseLargeObjectStream>(null, async operation =>
        {
            var flags = (access.HasFlag(FileAccess.Read) ? 0x40000 : 0) | (access.HasFlag(FileAccess.Write) ? 0x20000 : 0);
            var descriptor = await operation.ScalarAsync<int>("SELECT pg_catalog.lo_open(:p0,:p1)::pg_catalog.int4", oid, flags).ConfigureAwait(false);
            if (descriptor < 0) throw operation.InvalidResult();
            operation.AbandonDescriptorOnFailure(descriptor); return new KingbaseLargeObjectStream(_connection, operation.Register(oid, descriptor, access));
        }, cancellationToken);
    }
    public KingbaseLargeObjectStream OpenRead(uint oid) => Open(oid, FileAccess.Read);
    public Task<KingbaseLargeObjectStream> OpenReadAsync(uint oid, CancellationToken cancellationToken = default) => OpenAsync(oid, FileAccess.Read, cancellationToken);
    public KingbaseLargeObjectStream OpenReadWrite(uint oid) => Open(oid);
    public Task<KingbaseLargeObjectStream> OpenReadWriteAsync(uint oid, CancellationToken cancellationToken = default) => OpenAsync(oid, FileAccess.ReadWrite, cancellationToken);
    public void Unlink(uint oid) => UnlinkAsync(oid).GetAwaiter().GetResult();
    public Task UnlinkAsync(uint oid, CancellationToken cancellationToken = default)
    {
        ValidateOid(oid);
        return _connection.LargeObjectOperationAsync<int>(null, async operation =>
        {
            if (await operation.ScalarAsync<int>("SELECT pg_catalog.lo_unlink(:p0)::pg_catalog.int4", oid).ConfigureAwait(false) != 1) throw operation.InvalidResult();
            operation.Unlinked(oid);
            return 0;
        }, cancellationToken);
    }
    static void ValidateOid(uint oid) { if (oid == 0) throw new ArgumentOutOfRangeException(nameof(oid)); }
}
