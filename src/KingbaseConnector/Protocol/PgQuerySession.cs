using System.Collections.ObjectModel;
using System.Text;

namespace KingbaseConnector.Protocol;

internal sealed class PgColumnDescription(string name, uint tableOid, short attributeNumber, uint typeOid,
    short typeSize, int typeModifier)
{
    internal string Name { get; } = name;
    internal uint TableOid { get; } = tableOid;
    internal short AttributeNumber { get; } = attributeNumber;
    internal uint TypeOid { get; } = typeOid;
    internal short TypeSize { get; } = typeSize;
    internal int TypeModifier { get; } = typeModifier;
}

internal sealed class PgTextResult(IReadOnlyList<PgColumnDescription> columns,
    IReadOnlyList<IReadOnlyList<string?>> rows, string? commandTag)
{
    internal IReadOnlyList<PgColumnDescription> Columns { get; } = columns;
    internal IReadOnlyList<IReadOnlyList<string?>> Rows { get; } = rows;
    internal string? CommandTag { get; } = commandTag;
}

internal sealed class PgQueryException(PgServerError error)
    : IOException($"PG query failed (SQLSTATE {error.SqlState}).")
{
    internal PgServerError ServerError { get; } = error;
}

internal enum PgQueryExchange { Simple, Extended, Prepare, PreparedExecution, Close }

/// <summary>Owns an authenticated wire and executes bounded PG exchanges with text results.</summary>
internal sealed partial class PgQuerySession : IDisposable
{
    const int MaxPayload = 1024 * 1024;
    const int MaxFrames = 4096;
    const int MaxResults = 256;
    const int MaxRows = 1024;
    const int MaxColumns = 1024;
    readonly PgWire _wire;
    readonly Dictionary<string, string> _parameters;
    int _parameterBytes;
    int _busy;
    int _disposed;

    internal PgQuerySession(PgWire wire, PgStartupResult startup, int maxAutoPrepare = 0, int autoPrepareMinUsages = 5)
    {
        if (wire is null) throw new ArgumentNullException(nameof(wire));
        if (startup is null) throw new ArgumentNullException(nameof(startup));
        if (maxAutoPrepare is < 0 or > MaxPreparedStatements) throw new ArgumentOutOfRangeException(nameof(maxAutoPrepare));
        if (autoPrepareMinUsages < 1) throw new ArgumentOutOfRangeException(nameof(autoPrepareMinUsages));
        _maxAutoPrepare = maxAutoPrepare;
        _autoPrepareMinUsages = autoPrepareMinUsages;
        if (!startup.Parameters.TryGetValue("client_encoding", out var encoding) || encoding != "UTF8")
            throw new ArgumentException("PG text queries require confirmed UTF8 startup.", nameof(startup));
        _parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in startup.Parameters) UpdateParameter(parameter);
        _wire = wire;
        Parameters = new ReadOnlyDictionary<string, string>(_parameters);
    }

    internal IReadOnlyDictionary<string, string> Parameters { get; }
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal long WriteAttempts => _wire.WriteAttempts;
    internal PgServerError? CommandRejection { get; private set; }
    internal PgServerError? UncertainError { get; private set; }
    internal PgTransactionStatus TransactionStatus { get; private set; } = PgTransactionStatus.Idle;

    internal IReadOnlyList<PgTextResult> Execute(string sql) => ExecuteAsync(sql).GetAwaiter().GetResult();

    internal IReadOnlyList<PgTextResult> ExecuteExtended(string sql, PgTypeCatalog catalog,
        IReadOnlyList<PgQueryParameter> parameters) => ExecuteExtendedAsync(sql, catalog, parameters).GetAwaiter().GetResult();
    internal Task<IReadOnlyList<PgTextResult>> ExecuteExtendedAsync(string sql, PgTypeCatalog catalog,
        IReadOnlyList<PgQueryParameter> parameters, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(() => PgExtendedQuery.Encode(sql, catalog, parameters), PgQueryExchange.Extended, cancellationToken);

    internal Task<IReadOnlyList<PgTextResult>> ExecuteAsync(string sql,
        CancellationToken cancellationToken = default) => ExecuteCoreAsync(() =>
        {
            if (PgFrontendMessages.CStringLength(sql) > MaxPayload)
                throw new ArgumentException("Oversized PG bootstrap query.", nameof(sql));
            return [PgFrontendMessages.Query(sql)];
        }, PgQueryExchange.Simple, cancellationToken);

    Task<IReadOnlyList<PgTextResult>> ExecuteCoreAsync(Func<byte[][]> encode, PgQueryExchange exchange,
        CancellationToken cancellationToken) => OwnAsync(() => ExchangeAsync(encode, exchange, cancellationToken), cancellationToken);

    async Task<T> OwnAsync<T>(Func<Task<T>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("A PG query exchange is already active.");
        try { ThrowIfDisposed(); return await operation().ConfigureAwait(false); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    async Task<IReadOnlyList<PgTextResult>> ExchangeAsync(Func<byte[][]> encode, PgQueryExchange exchange,
        CancellationToken cancellationToken)
    {
        var extended = exchange != PgQueryExchange.Simple;
        var control = exchange is PgQueryExchange.Prepare or PgQueryExchange.Close;
        CommandRejection = null;
        UncertainError = null;
        var started = false;
        var synchronized = false;
        byte[][]? outgoing = null;
        try
        {
            ThrowIfDisposed();
            outgoing = encode();
            cancellationToken.ThrowIfCancellationRequested();
            started = true;
            foreach (var message in outgoing)
            {
                try { await _wire.WriteMessageAsync(message, cancellationToken).ConfigureAwait(false); }
                finally { Array.Clear(message, 0, message.Length); }
            }

            var results = new List<PgTextResult>();
            IReadOnlyList<PgColumnDescription>? columns = null;
            List<IReadOnlyList<string?>>? rows = null;
            PgServerError? serverError = null;
            var payloadBytes = 0;
            var totalRows = 0;
            var emptyQuery = false;
            var extendedPhase = exchange == PgQueryExchange.PreparedExecution ? 1 : 0;
            for (var frame = 0; frame < MaxFrames; frame++)
            {
                var message = await _wire.ReadMessageAsync(cancellationToken, MaxPayload - payloadBytes).ConfigureAwait(false);
                payloadBytes += message.Payload.Length;
                if (serverError is not null && message.Code != (byte)'Z' && message.Code != (byte)'N' && message.Code != (byte)'S')
                    throw InvalidExchange();
                switch (message.Code)
                {
                    case (byte)'1':
                        if (exchange is not (PgQueryExchange.Extended or PgQueryExchange.Prepare) || extendedPhase != 0 || message.Payload.Length != 0)
                            throw InvalidExchange();
                        extendedPhase = 1;
                        break;
                    case (byte)'3':
                        if (exchange != PgQueryExchange.Close || extendedPhase != 0 || message.Payload.Length != 0) throw InvalidExchange();
                        extendedPhase = 1;
                        break;
                    case (byte)'2':
                        if (exchange is not (PgQueryExchange.Extended or PgQueryExchange.PreparedExecution) || extendedPhase != 1 || message.Payload.Length != 0)
                            throw InvalidExchange();
                        extendedPhase = 2;
                        break;
                    case (byte)'n':
                        if (!extended || extendedPhase != 2 || message.Payload.Length != 0) throw InvalidExchange();
                        extendedPhase = 3;
                        break;
                    case (byte)'T':
                        if (control || (extended && extendedPhase != 2)) throw InvalidExchange();
                        if (emptyQuery || columns is not null || results.Count == MaxResults) throw InvalidExchange();
                        columns = ParseDescription(message.Payload);
                        rows = new List<IReadOnlyList<string?>>();
                        extendedPhase = 3;
                        break;
                    case (byte)'D':
                        if (columns is null || totalRows == MaxRows) throw InvalidExchange();
                        rows!.Add(ParseRow(message.Payload, columns.Count));
                        totalRows++;
                        break;
                    case (byte)'C':
                        if (control || (extended && extendedPhase != 3)) throw InvalidExchange();
                        if (emptyQuery || results.Count == MaxResults) throw InvalidExchange();
                        var offset = 0;
                        var tag = PgBinary.ReadCString(message.Payload, ref offset);
                        if (tag.Length == 0 || offset != message.Payload.Length) throw InvalidExchange();
                        if (tag is "DISCARD ALL" or "DEALLOCATE ALL") InvalidatePreparedStatements();
                        else if (tag == "DEALLOCATE") InvalidatePreparedStatements(forget: false);
                        results.Add(new PgTextResult(columns ?? Array.Empty<PgColumnDescription>(),
                            (rows ?? new List<IReadOnlyList<string?>>()).AsReadOnly(), tag));
                        columns = null;
                        rows = null;
                        extendedPhase = 4;
                        break;
                    case (byte)'I':
                        if (control || (extended && extendedPhase != 3)) throw InvalidExchange();
                        if (columns is not null || message.Payload.Length != 0 || results.Count != 0)
                            throw InvalidExchange();
                        results.Add(new PgTextResult(Array.Empty<PgColumnDescription>(), Array.Empty<IReadOnlyList<string?>>(), null));
                        emptyQuery = true;
                        extendedPhase = 4;
                        break;
                    case (byte)'S':
                        var parameter = PgBackendMessages.ParseParameterStatus(message.Payload);
                        if (parameter.Key == "client_encoding" && parameter.Value != "UTF8") throw InvalidExchange();
                        UpdateParameter(parameter);
                        break;
                    case (byte)'N':
                        PgBackendMessages.ParseError(message.Payload);
                        break;
                    case (byte)'E':
                        if (control && extendedPhase != 0) throw InvalidExchange();
                        serverError = PgBackendMessages.ParseError(message.Payload);
                        var severity = serverError.Fields.TryGetValue((byte)'V', out var invariantSeverity)
                            ? invariantSeverity : serverError.Fields[(byte)'S'];
                        if (results.Count == 0 && severity == "ERROR" && serverError.SqlState != "40003" &&
                            !serverError.SqlState.StartsWith("08", StringComparison.Ordinal))
                            CommandRejection = serverError;
                        else UncertainError = serverError;
                        results.Clear();
                        columns = null;
                        rows = null;
                        break;
                    case (byte)'Z':
                        if (extended && serverError is null && extendedPhase != (control ? 1 : 4)) throw InvalidExchange();
                        if (columns is not null || (!control && serverError is null && results.Count == 0)) throw InvalidExchange();
                        TransactionStatus = PgBackendMessages.ParseReadyForQuery(message.Payload);
                        ThrowIfDisposed();
                        synchronized = true;
                        if (serverError is not null) throw new PgQueryException(serverError);
                        return results.AsReadOnly();
                    default:
                        throw InvalidExchange();
                }
            }
            throw new IOException("PG text query exceeded its message limit.");
        }
        catch
        {
            // Only a confirmed ReadyForQuery permits reuse after a server error.
            if (started && !synchronized) { try { Dispose(); } catch { } }
            throw;
        }
        finally
        {
            if (outgoing is not null)
                foreach (var message in outgoing) Array.Clear(message, 0, message.Length);
        }
    }

    static IReadOnlyList<PgColumnDescription> ParseDescription(byte[] payload)
    {
        if (payload.Length < 2) throw InvalidExchange();
        var count = ReadUInt16(payload, 0);
        // Each column requires at least a name terminator and eighteen metadata bytes.
        if (count > MaxColumns || count > (payload.Length - 2) / 19) throw InvalidExchange();
        var columns = new PgColumnDescription[count];
        var offset = 2;
        for (var i = 0; i < count; i++)
        {
            var name = PgBinary.ReadCString(payload, ref offset);
            if (payload.Length - offset < 18 || ReadUInt16(payload, offset + 16) != 0) throw InvalidExchange();
            columns[i] = new PgColumnDescription(name, unchecked((uint)PgBinary.ReadInt32(payload, offset)),
                unchecked((short)ReadUInt16(payload, offset + 4)), unchecked((uint)PgBinary.ReadInt32(payload, offset + 6)),
                unchecked((short)ReadUInt16(payload, offset + 10)), PgBinary.ReadInt32(payload, offset + 12));
            offset += 18;
        }
        if (offset != payload.Length) throw InvalidExchange();
        return Array.AsReadOnly(columns);
    }

    static IReadOnlyList<string?> ParseRow(byte[] payload, int columnCount)
    {
        if (payload.Length < 2 || ReadUInt16(payload, 0) != columnCount) throw InvalidExchange();
        var values = new string?[columnCount];
        var offset = 2;
        for (var i = 0; i < columnCount; i++)
        {
            if (payload.Length - offset < 4) throw InvalidExchange();
            var length = PgBinary.ReadInt32(payload, offset);
            offset += 4;
            if (length == -1) continue;
            if (length < 0 || length > payload.Length - offset) throw InvalidExchange();
            try { values[i] = PgBinary.Utf8.GetString(payload, offset, length); }
            catch (DecoderFallbackException) { throw new IOException("Invalid UTF8 in PG row."); }
            offset += length;
        }
        if (offset != payload.Length) throw InvalidExchange();
        return Array.AsReadOnly(values);
    }

    static int ReadUInt16(byte[] bytes, int offset) => (bytes[offset] << 8) | bytes[offset + 1];
    void UpdateParameter(KeyValuePair<string, string> parameter)
    {
        var replacing = _parameters.TryGetValue(parameter.Key, out var previous);
        if (!replacing && _parameters.Count == 256) throw InvalidExchange();
        var bytes = _parameterBytes + PgBinary.Utf8.GetByteCount(parameter.Value) +
            (replacing ? -PgBinary.Utf8.GetByteCount(previous!) : PgBinary.Utf8.GetByteCount(parameter.Key) + 2);
        if (bytes > MaxPayload) throw InvalidExchange();
        _parameters[parameter.Key] = parameter.Value;
        _parameterBytes = bytes;
    }
    static IOException InvalidExchange() => new("Invalid PG text-query exchange.");
    void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PgQuerySession));
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            InvalidatePreparedStatements();
            _wire.Dispose();
        }
    }
}
