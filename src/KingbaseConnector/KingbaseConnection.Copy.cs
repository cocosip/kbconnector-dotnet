using KingbaseConnector.Protocol;

namespace KingbaseConnector;

public sealed partial class KingbaseConnection
{
    public KingbaseBinaryImporter BeginBinaryImport(string copyCommand, params KingbaseDbType[] columnTypes) =>
        BeginBinaryImportAsync(copyCommand, columnTypes).GetAwaiter().GetResult();
    public async Task<KingbaseBinaryImporter> BeginBinaryImportAsync(string copyCommand, IReadOnlyList<KingbaseDbType> columnTypes, CancellationToken cancellationToken = default) =>
        new(await BeginTypedCopyAsync(copyCommand, columnTypes, true, cancellationToken).ConfigureAwait(false));
    public KingbaseBinaryExporter BeginBinaryExport(string copyCommand, params KingbaseDbType[] columnTypes) =>
        BeginBinaryExportAsync(copyCommand, columnTypes).GetAwaiter().GetResult();
    public async Task<KingbaseBinaryExporter> BeginBinaryExportAsync(string copyCommand, IReadOnlyList<KingbaseDbType> columnTypes, CancellationToken cancellationToken = default) =>
        new(await BeginTypedCopyAsync(copyCommand, columnTypes, false, cancellationToken).ConfigureAwait(false));

    async Task<PgBinaryCopyRows> BeginTypedCopyAsync(string sql, IReadOnlyList<KingbaseDbType> columnTypes, bool import, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (columnTypes is null) throw new ArgumentNullException(nameof(columnTypes));
        if (columnTypes.Count > 1024) throw new ArgumentException("Binary COPY supports at most 1024 columns.", nameof(columnTypes));
        var snapshot = columnTypes.ToArray(); string[]? codecs = null;
        var copy = await BeginCopyCoreAsync(sql, true, import, token, session =>
        {
            codecs = PgBinaryCopyCodec.Resolve(session.Catalog, snapshot);
            if (codecs.Any(name => name is "time" or "timetz" or "timestamp" or "timestamptz" or "interval") &&
                (!session.Queries.Parameters.TryGetValue("integer_datetimes", out var status) || status != "on"))
                throw new NotSupportedException("Binary temporal COPY requires server-reported integer_datetimes=on.");
        }).ConfigureAwait(false);
        var rows = new PgBinaryCopyRows(copy, codecs!);
        await rows.StartAsync(token).ConfigureAwait(false); return rows;
    }

    public KingbaseCopyStream BeginRawBinaryCopy(string copyCommand) => BeginRawBinaryCopyAsync(copyCommand).GetAwaiter().GetResult();
    public Task<KingbaseCopyStream> BeginRawBinaryCopyAsync(string copyCommand, CancellationToken cancellationToken = default) =>
        BeginCopyCoreAsync(copyCommand, binary: true, import: null, cancellationToken);
    public KingbaseTextCopyWriter BeginTextImport(string copyCommand) => BeginTextImportAsync(copyCommand).GetAwaiter().GetResult();
    public async Task<KingbaseTextCopyWriter> BeginTextImportAsync(string copyCommand, CancellationToken cancellationToken = default) =>
        new(await BeginCopyCoreAsync(copyCommand, binary: false, import: true, cancellationToken).ConfigureAwait(false));
    public TextReader BeginTextExport(string copyCommand) => BeginTextExportAsync(copyCommand).GetAwaiter().GetResult();
    public async Task<TextReader> BeginTextExportAsync(string copyCommand, CancellationToken cancellationToken = default) =>
        new KingbaseTextCopyReader(await BeginCopyCoreAsync(copyCommand, binary: false, import: false, cancellationToken).ConfigureAwait(false));

    async Task<KingbaseCopyStream> BeginCopyCoreAsync(string sql, bool binary, bool? import, CancellationToken token, Action<PgPhysicalSession>? preflight = null)
    {
        token.ThrowIfCancellationRequested();
        var marker = new KingbaseCommand(); var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        KingbaseCopyStream copy;
        try
        {
            lock (_sync)
            {
                var physical = OpenSession();
                if (_command is not null || _reader is not null) throw ActiveOperation();
                physical.Queries.Parameters.TryGetValue("standard_conforming_strings", out var status);
                var plan = PgParameterPlan.Create(sql, Array.Empty<string?>(), physical.Identity.EffectiveMode,
                    status switch { "on" => true, "off" => false, _ => null }, clientCopy: true);
                if (plan.FirstKeyword != "COPY") throw new ArgumentException("A single COPY statement is required.", nameof(sql));
                if (import.HasValue && plan.CopyIsImport != import) throw new ArgumentException("The COPY endpoint does not match the requested direction.", nameof(sql));
                preflight?.Invoke(physical);
                var (session, generation) = BeginExecution(marker, cancel);
                copy = new KingbaseCopyStream(this, session, generation, marker, cancel, token, _settings.CommandTimeout, _settings.CleanupTimeout);
            }
        }
        catch { marker.Dispose(); cancel.Dispose(); throw; }
        await copy.StartAsync(sql, binary, import).ConfigureAwait(false);
        return copy;
    }
}
