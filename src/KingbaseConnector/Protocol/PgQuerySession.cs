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
    IReadOnlyList<IReadOnlyList<string?>> rows, string? commandTag, string? dateStyle = null, string? intervalStyle = null,
    IReadOnlyList<PgColumnMetadata?>? metadata = null, IReadOnlyDictionary<uint, string>? metadataVersions = null)
{
    internal IReadOnlyList<PgColumnDescription> Columns { get; } = columns;
    internal IReadOnlyList<IReadOnlyList<string?>> Rows { get; } = rows;
    internal string? CommandTag { get; } = commandTag;
    internal string? DateStyle { get; } = dateStyle;
    internal string? IntervalStyle { get; } = intervalStyle;
    internal IReadOnlyList<PgColumnMetadata?>? Metadata { get; } = metadata;
    internal IReadOnlyDictionary<uint, string> MetadataVersions { get; } = metadataVersions ?? new Dictionary<uint, string>();
}

internal sealed class PgQueryException(PgServerError error)
    : IOException($"PG query failed (SQLSTATE {error.SqlState}).")
{
    internal PgServerError ServerError { get; } = error;
}

internal enum PgQueryExchange { Simple, Extended, Prepare, PreparedExecution, Close, Describe }

/// <summary>Owns an authenticated wire and executes bounded PG exchanges with text results.</summary>
internal sealed partial class PgQuerySession : IDisposable
{
    const int MaxPayload = PgWire.MaxBufferedPayloadLength;
    const int MaxFrames = 4096;
    const int MaxResults = 256;
    const int MaxRows = 1024;
    const int MaxColumns = 1024;
    internal const string CanceledSqlState = "57014";
    internal static readonly TimeSpan DefaultDrainGrace = TimeSpan.FromSeconds(5);
    static readonly TimeSpan MaxDrainGrace = TimeSpan.FromMilliseconds(int.MaxValue - 1);
    readonly PgWire _wire;
    readonly Func<CancellationToken, Task>? _cancelSender;
    readonly TimeSpan _drainGrace;
    readonly Dictionary<string, string> _parameters;
    int _parameterBytes;
    int _busy;
    int _disposed;
    int _cancellationRequested;
    CancellationToken _callerCancellation;
    CancellationTokenSource? _drainDeadline;

    // cancelSender: sends the backend-key cancel on a fresh connection.
    // Without one, a caller cancellation keeps the legacy abort-and-retire behavior.
    internal PgQuerySession(PgWire wire, PgStartupResult startup, int maxAutoPrepare = 0, int autoPrepareMinUsages = 5,
        Func<CancellationToken, Task>? cancelSender = null, TimeSpan? drainGrace = null)
    {
        if (wire is null) throw new ArgumentNullException(nameof(wire));
        if (startup is null) throw new ArgumentNullException(nameof(startup));
        if (maxAutoPrepare is < 0 or > MaxPreparedStatements) throw new ArgumentOutOfRangeException(nameof(maxAutoPrepare));
        if (autoPrepareMinUsages < 1) throw new ArgumentOutOfRangeException(nameof(autoPrepareMinUsages));
        _cancelSender = cancelSender;
        var grace = drainGrace ?? DefaultDrainGrace;
        if (grace <= TimeSpan.Zero || grace > MaxDrainGrace) grace = DefaultDrainGrace;
        _drainGrace = grace;
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
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0 || _wire.IsDisposed;
    internal bool HasActiveExchange => Volatile.Read(ref _busy) != 0;
    internal Action<KingbaseNotificationEventArgs>? NotificationHandler { get; set; }
    internal Action<KingbaseNotice>? NoticeHandler { get; set; }
    internal long WriteAttempts => _wire.WriteAttempts;
    internal long ExecutionWriteAttempts { get; private set; }
    internal PgServerError? CommandRejection { get; private set; }
    internal PgServerError? UncertainError { get; private set; }
    internal PgTransactionStatus TransactionStatus { get; private set; } = PgTransactionStatus.Idle;
    internal void ResetCommandOutcome() { CommandRejection = null; UncertainError = null; }
    internal bool CancelRequested => Volatile.Read(ref _cancellationRequested) != 0;

    // One backend-key cancel per exchange. The bounded drain deadline retires a session the server never completes.
    void RequestCallerCancel()
    {
        if (IsDisposed || _cancelSender is null || Interlocked.Exchange(ref _cancellationRequested, 1) != 0) return;
        var deadline = new CancellationTokenSource(_drainGrace);
        if (Interlocked.Exchange(ref _drainDeadline, deadline) is { } previous)
        {
            try { previous.Dispose(); } catch { }
        }
        deadline.Token.Register(static state =>
        {
            var session = (PgQuerySession)state!;
            if (!session.CancelRequested) return;
            try { session.Dispose(); } catch { }
        }, this);
        _ = SendCancelAsync();
    }

    async Task SendCancelAsync()
    {
        // Delivery shares the drain budget: a channel that cannot deliver in time is covered by the drain grace.
        using var delivery = new CancellationTokenSource(_drainGrace);
        try { await _cancelSender!(delivery.Token).ConfigureAwait(false); }
        catch { /* The drain deadline retires the session when the server therefore never answers. */ }
    }

    void ResetCancellation()
    {
        Volatile.Write(ref _cancellationRequested, 0);
        if (Interlocked.Exchange(ref _drainDeadline, null) is { } deadline)
        {
            try { deadline.Dispose(); } catch { }
        }
    }

    // Streaming inputs are staged before any frame reaches the server, so a caller cancellation during
    // staging must abort that work directly instead of waiting for a server-side cancel to drain.
    internal CancellationTokenSource? LinkStaging(CancellationToken abort)
    {
        var caller = _callerCancellation;
        return caller.CanBeCanceled && abort.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(abort, caller) : null;
    }

    internal IReadOnlyList<PgTextResult> Execute(string sql) => ExecuteAsync(sql).GetAwaiter().GetResult();

    internal IReadOnlyList<PgTextResult> ExecuteExtended(string sql, PgTypeCatalog catalog,
        IReadOnlyList<PgQueryParameter> parameters) => ExecuteExtendedAsync(sql, catalog, parameters).GetAwaiter().GetResult();
    internal Task<IReadOnlyList<PgTextResult>> ExecuteExtendedAsync(string sql, PgTypeCatalog catalog,
        IReadOnlyList<PgQueryParameter> parameters, CancellationToken cancellationToken = default,
        CancellationToken callerToken = default) =>
        ExecuteCoreAsync(() => PgExtendedQuery.Encode(sql, catalog, parameters), PgQueryExchange.Extended, cancellationToken,
            MaxRows, callerToken);

    internal Task<IReadOnlyList<PgTextResult>> DescribeAsync(string sql, PgTypeCatalog catalog,
        IReadOnlyList<uint> typeOids, CancellationToken token = default, CancellationToken callerToken = default) => OwnAsync(async () =>
        {
            if (typeOids is null) throw new ArgumentNullException(nameof(typeOids));
            if (typeOids.Count > 1024) throw new ArgumentException("PG description exceeds the parameter limit.", nameof(typeOids));
            var snapshot = typeOids.Take(1025).ToArray();
            if (snapshot.Length != typeOids.Count || snapshot.Length > 1024)
                throw new ArgumentException("Invalid PG description parameter types.", nameof(typeOids));
            var codecs = new PgTextCodecs(catalog);
            foreach (var oid in snapshot) codecs.Encode(oid, null);
            return await ExchangeAsync(() => PgExtendedQuery.EncodeDescribe(sql, snapshot), PgQueryExchange.Describe, token, snapshot).ConfigureAwait(false);
        }, token, callerToken);

    // maxRows lets one bounded bootstrap read (the type catalog) request more than the ordinary exchange limit.
    internal Task<IReadOnlyList<PgTextResult>> ExecuteAsync(string sql,
        CancellationToken cancellationToken = default, int maxRows = MaxRows, CancellationToken callerToken = default) =>
        ExecuteCoreAsync(() =>
        {
            if (PgFrontendMessages.CStringLength(sql) > MaxPayload)
                throw new ArgumentException("Oversized PG bootstrap query.", nameof(sql));
            return [PgFrontendMessages.Query(sql)];
        }, PgQueryExchange.Simple, cancellationToken, maxRows, callerToken);

    Task<IReadOnlyList<PgTextResult>> ExecuteCoreAsync(Func<byte[][]> encode, PgQueryExchange exchange,
        CancellationToken cancellationToken, int maxRows = MaxRows, CancellationToken callerToken = default) =>
        OwnAsync(() => ExchangeAsync(encode, exchange, cancellationToken, null, maxRows), cancellationToken, callerToken);

    async Task<T> OwnAsync<T>(Func<Task<T>> operation, CancellationToken token, CancellationToken callerToken = default)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("A PG query exchange is already active.");
        _callerCancellation = callerToken;
        ResetCancellation();
        try { ThrowIfDisposed(); ResetCommandOutcome(); return await operation().ConfigureAwait(false); }
        finally
        {
            _callerCancellation = default;
            ResetCancellation();
            Volatile.Write(ref _busy, 0);
        }
    }

    Task<IReadOnlyList<PgTextResult>> ExchangeAsync(Func<byte[][]> encode, PgQueryExchange exchange,
        CancellationToken cancellationToken, IReadOnlyList<uint>? describedTypes = null, int maxRows = MaxRows) =>
        ExchangeRequestAsync(() => new PgExtendedRequest(encode()), exchange, cancellationToken, describedTypes, maxRows);

    async Task<IReadOnlyList<PgTextResult>> ExchangeRequestAsync(Func<PgExtendedRequest> encode, PgQueryExchange exchange,
        CancellationToken cancellationToken, IReadOnlyList<uint>? describedTypes = null, int maxRows = MaxRows)
    {
        var extended = exchange != PgQueryExchange.Simple;
        var control = exchange is PgQueryExchange.Prepare or PgQueryExchange.Close;
        var describe = exchange == PgQueryExchange.Describe;
        ResetCommandOutcome();
        // The abort token ends the read; the caller token only asks the server to cancel and then drains.
        // Sessions without a cancel channel keep the legacy abort-and-retire caller behavior.
        var caller = _callerCancellation;
        var draining = _cancelSender is not null && caller.CanBeCanceled;
        CancellationTokenSource? mergedCancellation = !draining && caller.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, caller) : null;
        var readToken = mergedCancellation is null ? cancellationToken : mergedCancellation.Token;
        using CancellationTokenRegistration callerRegistration = draining
            ? caller.Register(static state => ((PgQuerySession)state!).RequestCallerCancel(), this) : default;
        var started = false;
        var synchronized = false;
        PgExtendedRequest? outgoing = null;
        try
        {
            ThrowIfDisposed();
            outgoing = encode();
            cancellationToken.ThrowIfCancellationRequested();
            started = true;
            await outgoing.SendAsync(_wire, count => ExecutionWriteAttempts += count, cancellationToken).ConfigureAwait(false);

            var results = new List<PgTextResult>();
            IReadOnlyList<PgColumnDescription>? columns = null;
            List<IReadOnlyList<string?>>? rows = null;
            string? dateStyle = null;
            string? intervalStyle = null;
            PgServerError? serverError = null;
            var payloadBytes = 0;
            var totalRows = 0;
            var emptyQuery = false;
            var extendedPhase = exchange == PgQueryExchange.PreparedExecution ? 1 : 0;
            for (var frame = 0; frame < MaxFrames; frame++)
            {
                var message = await _wire.ReadMessageAsync(readToken, MaxPayload - payloadBytes).ConfigureAwait(false);
                payloadBytes += message.Payload.Length;
                if (serverError is not null && message.Code != (byte)'Z' && message.Code != (byte)'N' && message.Code != (byte)'S' && message.Code != (byte)'A')
                    throw InvalidExchange();
                switch (message.Code)
                {
                    case (byte)'A':
                        var notification = PgBackendMessages.ParseNotification(message.Payload);
                        NotificationHandler?.Invoke(notification);
                        break;
                    case (byte)'1':
                        if (exchange is not (PgQueryExchange.Extended or PgQueryExchange.Prepare or PgQueryExchange.Describe) || extendedPhase != 0 || message.Payload.Length != 0)
                            throw InvalidExchange();
                        extendedPhase = 1;
                        break;
                    case (byte)'t':
                        if (!describe || extendedPhase != 1 || describedTypes is null || message.Payload.Length != 2 + 4 * describedTypes.Count ||
                            ((message.Payload[0] << 8) | message.Payload[1]) != describedTypes.Count)
                            throw InvalidExchange();
                        for (var i = 0; i < describedTypes.Count; i++)
                            if (unchecked((uint)PgBinary.ReadInt32(message.Payload, 2 + 4 * i)) != describedTypes[i]) throw InvalidExchange();
                        extendedPhase = 2;
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
                        if (describe) results.Add(new PgTextResult(Array.Empty<PgColumnDescription>(), Array.Empty<IReadOnlyList<string?>>(), null));
                        extendedPhase = 3;
                        break;
                    case (byte)'T':
                        if (control || (extended && extendedPhase != 2)) throw InvalidExchange();
                        if (emptyQuery || columns is not null || results.Count == MaxResults) throw InvalidExchange();
                        columns = ParseDescription(message.Payload);
                        rows = new List<IReadOnlyList<string?>>();
                        _parameters.TryGetValue("DateStyle", out dateStyle);
                        _parameters.TryGetValue("IntervalStyle", out intervalStyle);
                        if (describe)
                        {
                            results.Add(new PgTextResult(columns, Array.Empty<IReadOnlyList<string?>>(), null, dateStyle, intervalStyle));
                            columns = null;
                            rows = null;
                        }
                        extendedPhase = 3;
                        break;
                    case (byte)'D':
                        if (describe || columns is null || totalRows == maxRows) throw InvalidExchange();
                        _parameters.TryGetValue("DateStyle", out var rowStyle);
                        if (rows!.Count == 0) dateStyle = rowStyle;
                        else if (rowStyle != dateStyle) dateStyle = null;
                        _parameters.TryGetValue("IntervalStyle", out var rowIntervalStyle);
                        if (rows.Count == 0) intervalStyle = rowIntervalStyle;
                        else if (rowIntervalStyle != intervalStyle) intervalStyle = null;
                        rows!.Add(ParseRow(message.Payload, columns.Count));
                        totalRows++;
                        break;
                    case (byte)'C':
                        if (control || describe || (extended && extendedPhase != 3)) throw InvalidExchange();
                        if (emptyQuery || results.Count == MaxResults) throw InvalidExchange();
                        var offset = 0;
                        var tag = PgBinary.ReadCString(message.Payload, ref offset);
                        if (tag.Length == 0 || offset != message.Payload.Length) throw InvalidExchange();
                        if (tag is "DISCARD ALL" or "DEALLOCATE ALL") InvalidatePreparedStatements();
                        else if (tag == "DEALLOCATE") InvalidatePreparedStatements(forget: false);
                        results.Add(new PgTextResult(columns ?? Array.Empty<PgColumnDescription>(),
                            (rows ?? new List<IReadOnlyList<string?>>()).AsReadOnly(), tag, dateStyle, intervalStyle));
                        columns = null;
                        rows = null;
                        extendedPhase = 4;
                        break;
                    case (byte)'I':
                        if (control || describe || (extended && extendedPhase != 3)) throw InvalidExchange();
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
                        var notice = PgBackendMessages.ParseError(message.Payload);
                        NoticeHandler?.Invoke(new KingbaseNotice(notice));
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
                        if (extended && serverError is null && extendedPhase != (control ? 1 : describe ? 3 : 4)) throw InvalidExchange();
                        if (columns is not null || (!control && serverError is null && results.Count == 0)) throw InvalidExchange();
                        TransactionStatus = PgBackendMessages.ParseReadyForQuery(message.Payload);
                        ThrowIfDisposed();
                        synchronized = true;
                        if (CancelRequested && (serverError is null || serverError.SqlState == CanceledSqlState))
                        {
                            // The caller asked to stop: report cancellation rather than the server's 57014 error,
                            // and keep the confirmed ReadyForQuery as the reusable synchronization point.
                            CommandRejection = null;
                            if (serverError is null)
                            {
                                // The statement finished before the cancel took effect, so delivery is ambiguous:
                                // never let a late cancel reach the next command or lease.
                                Dispose();
                                throw new OperationCanceledException(caller);
                            }
                            ResetCancellation();
                            throw new OperationCanceledException(caller);
                        }
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
            outgoing?.Dispose();
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
        if (IsDisposed) throw new ObjectDisposedException(nameof(PgQuerySession));
    }
    public void Dispose()
    {
        NotificationHandler = null;
        NoticeHandler = null;
        if (Interlocked.Exchange(ref _drainDeadline, null) is { } drain)
        {
            try { drain.Dispose(); } catch { }
        }
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            InvalidatePreparedStatements();
            _wire.Dispose();
        }
    }
}

