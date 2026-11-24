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

/// <summary>Owns an authenticated wire and executes bounded bootstrap text queries.</summary>
internal sealed class PgQuerySession : IDisposable
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

    internal PgQuerySession(PgWire wire, PgStartupResult startup)
    {
        if (wire is null) throw new ArgumentNullException(nameof(wire));
        if (startup is null) throw new ArgumentNullException(nameof(startup));
        if (!startup.Parameters.TryGetValue("client_encoding", out var encoding) || encoding != "UTF8")
            throw new ArgumentException("PG text queries require confirmed UTF8 startup.", nameof(startup));
        _parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in startup.Parameters) UpdateParameter(parameter);
        _wire = wire;
        Parameters = new ReadOnlyDictionary<string, string>(_parameters);
    }

    internal IReadOnlyDictionary<string, string> Parameters { get; }
    internal PgTransactionStatus TransactionStatus { get; private set; } = PgTransactionStatus.Idle;

    internal IReadOnlyList<PgTextResult> Execute(string sql) => ExecuteAsync(sql).GetAwaiter().GetResult();

    internal async Task<IReadOnlyList<PgTextResult>> ExecuteAsync(string sql,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("A PG query exchange is already active.");
        var started = false;
        var synchronized = false;
        try
        {
            ThrowIfDisposed();
            if (PgFrontendMessages.CStringLength(sql) > MaxPayload)
                throw new ArgumentException("Oversized PG bootstrap query.", nameof(sql));
            var query = PgFrontendMessages.Query(sql);
            cancellationToken.ThrowIfCancellationRequested();
            started = true;
            await _wire.WriteMessageAsync(query, cancellationToken).ConfigureAwait(false);

            var results = new List<PgTextResult>();
            IReadOnlyList<PgColumnDescription>? columns = null;
            List<IReadOnlyList<string?>>? rows = null;
            PgServerError? serverError = null;
            var payloadBytes = 0;
            var totalRows = 0;
            var emptyQuery = false;
            for (var frame = 0; frame < MaxFrames; frame++)
            {
                var message = await _wire.ReadMessageAsync(cancellationToken, MaxPayload - payloadBytes).ConfigureAwait(false);
                payloadBytes += message.Payload.Length;
                if (serverError is not null && message.Code != (byte)'Z' && message.Code != (byte)'N' && message.Code != (byte)'S')
                    throw InvalidExchange();
                switch (message.Code)
                {
                    case (byte)'T':
                        if (emptyQuery || columns is not null || results.Count == MaxResults) throw InvalidExchange();
                        columns = ParseDescription(message.Payload);
                        rows = new List<IReadOnlyList<string?>>();
                        break;
                    case (byte)'D':
                        if (columns is null || totalRows == MaxRows) throw InvalidExchange();
                        rows!.Add(ParseRow(message.Payload, columns.Count));
                        totalRows++;
                        break;
                    case (byte)'C':
                        if (emptyQuery || results.Count == MaxResults) throw InvalidExchange();
                        var offset = 0;
                        var tag = PgBinary.ReadCString(message.Payload, ref offset);
                        if (tag.Length == 0 || offset != message.Payload.Length) throw InvalidExchange();
                        results.Add(new PgTextResult(columns ?? Array.Empty<PgColumnDescription>(),
                            (rows ?? new List<IReadOnlyList<string?>>()).AsReadOnly(), tag));
                        columns = null;
                        rows = null;
                        break;
                    case (byte)'I':
                        if (columns is not null || message.Payload.Length != 0 || results.Count != 0)
                            throw InvalidExchange();
                        results.Add(new PgTextResult(Array.Empty<PgColumnDescription>(), Array.Empty<IReadOnlyList<string?>>(), null));
                        emptyQuery = true;
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
                        serverError = PgBackendMessages.ParseError(message.Payload);
                        results.Clear();
                        columns = null;
                        rows = null;
                        break;
                    case (byte)'Z':
                        if (columns is not null || (serverError is null && results.Count == 0)) throw InvalidExchange();
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
        finally { Volatile.Write(ref _busy, 0); }
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
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _wire.Dispose();
    }
}
