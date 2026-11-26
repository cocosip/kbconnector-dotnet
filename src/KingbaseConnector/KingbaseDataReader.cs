using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Numerics;
using System.Globalization;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>Reads supported scalar values from a completed, bounded PG text exchange.</summary>
public sealed partial class KingbaseDataReader : DbDataReader, IDbColumnSchemaGenerator
{
    IReadOnlyList<PgTextResult>? _results;
    readonly PgTypeCatalog _catalog;
    readonly PgTextCodecs _codecs;
    readonly int _recordsAffected;
    readonly bool _singleRow;
    readonly bool _singleResult;
    Action<KingbaseDataReader>? _onClose;
    int _resultIndex;
    int _rowIndex = -1;
    Dictionary<int, object>? _partialValues;
    readonly KingbaseReaderExecution? _streaming;

    internal KingbaseDataReader(KingbaseReaderExecution execution, PgTypeCatalog catalog, CommandBehavior behavior, Action<KingbaseDataReader> onClose)
        : this(new[] { execution.Header! }, catalog, behavior, onClose) => _streaming = execution;

    internal KingbaseDataReader(IReadOnlyList<PgTextResult> results, PgTypeCatalog catalog,
        CommandBehavior behavior = CommandBehavior.Default, Action<KingbaseDataReader>? onClose = null)
    {
        if (results is null) throw new ArgumentNullException(nameof(results));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _codecs = new PgTextCodecs(catalog);
        _singleRow = (behavior & CommandBehavior.SingleRow) != 0;
        _singleResult = (behavior & (CommandBehavior.SingleRow | CommandBehavior.SingleResult)) != 0;
        _onClose = onClose;
        var affected = 0;
        var hasAffected = false;
        foreach (var result in results)
        {
            foreach (var column in result.Columns)
            {
                _codecs.GetFieldType(column.TypeOid);
                if (column.TypeSize != catalog.ByOid[column.TypeOid].Size)
                    throw new NotSupportedException("PG result type size does not match its catalog.");
            }
            if (AffectedCount(result.CommandTag) is { } count)
            {
                affected = checked(affected + count);
                hasAffected = true;
            }
        }
        _recordsAffected = hasAffected ? affected : -1;
        _results = results;
    }

    PgTextResult? Current
    {
        get { EnsureOpen(); return _streaming is not null ? _streaming.Header : _resultIndex < _results!.Count ? _results[_resultIndex] : null; }
    }
    public override int FieldCount => Current?.Columns.Count ?? 0;
    public override int Depth { get { EnsureOpen(); return 0; } }
    public override bool HasRows { get { EnsureOpen(); return _streaming is not null ? _streaming.HasRowsAsync().GetAwaiter().GetResult() : Current?.Rows.Count > 0; } }
    public override bool IsClosed => _results is null;
    internal bool IsStreaming => _streaming is not null;
    internal void CloseFromConnection()
    { try { _streaming!.InvalidateFromConnection(); } finally { CloseBuffered(); } }
    public override int RecordsAffected => _streaming?.RecordsAffected ?? _recordsAffected;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_streaming is not null) { EnsureOpen(); return _streaming.ReadAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        var current = Current;
        _partialValues = null;
        if (current is null) return false;
        var count = _singleRow ? Math.Min(1, current.Rows.Count) : current.Rows.Count;
        if (_rowIndex < count) _rowIndex++;
        return _rowIndex < count;
    }
    public override bool NextResult()
    {
        EnsureOpen();
        if (_streaming is not null) return _streaming.NextResultAsync(CancellationToken.None).GetAwaiter().GetResult();
        _partialValues = null;
        if (_resultIndex < _results!.Count) _resultIndex = _singleResult ? _results.Count : _resultIndex + 1;
        _rowIndex = -1;
        return _resultIndex < _results.Count;
    }

    public override string GetName(int ordinal) => Column(ordinal).Name;
    public override int GetOrdinal(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        var columns = Current?.Columns;
        if (columns is not null)
        {
            for (var i = 0; i < columns.Count; i++) if (columns[i].Name == name) return i;
            for (var i = 0; i < columns.Count; i++)
                if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        throw new IndexOutOfRangeException("Kingbase result column was not found.");
    }
    public override string GetDataTypeName(int ordinal) => _catalog.ByOid[Column(ordinal).TypeOid].Name;
    public override Type GetFieldType(int ordinal) => _codecs.GetFieldType(Column(ordinal).TypeOid);
    public override Type GetProviderSpecificFieldType(int ordinal) => _codecs.GetProviderSpecificFieldType(Column(ordinal).TypeOid);
    public override object GetValue(int ordinal) => _streaming is not null ? StreamingValueAsync(ordinal, false, CancellationToken.None).GetAwaiter().GetResult() : _codecs.Decode(Column(ordinal).TypeOid, RawValue(ordinal), Current!.DateStyle, Current.IntervalStyle);
    public override object GetProviderSpecificValue(int ordinal) => _streaming is not null ? StreamingValueAsync(ordinal, true, CancellationToken.None).GetAwaiter().GetResult() : _codecs.DecodeProviderSpecific(Column(ordinal).TypeOid, RawValue(ordinal), Current!.DateStyle, Current.IntervalStyle);
    Task<object> StreamingValueAsync(int ordinal, bool provider, CancellationToken token) { Column(ordinal); return _streaming!.ValueAsync(ordinal, _codecs, provider, token); }
    public override T GetFieldValue<T>(int ordinal)
    {
        var type = _catalog.ByOid[Column(ordinal).TypeOid];
        if (_streaming is not null) return _streaming.TypedValueAsync<T>(ordinal, _codecs, type, CancellationToken.None).GetAwaiter().GetResult();
        return ConvertFieldValue<T>(type, provider => provider ? GetProviderSpecificValue(ordinal) : GetValue(ordinal));
    }
    internal static T ConvertFieldValue<T>(PgTypeDescription type, Func<bool, object> value)
    {
        if (typeof(T) == typeof(uint) && type.CodecName == "int8") return (T)(object)checked((uint)(long)value(false));
        if (typeof(T) == typeof(ulong) && type.CodecName == "int16") return (T)(object)(ulong)(BigInteger)value(false);
        if (typeof(T) == typeof(DateTimeOffset)) return (T)(object)((KingbaseTimestampTz)value(true)).ToDateTimeOffset();
        if (typeof(T) == typeof(TimeSpan) && type.Name == "interval") return (T)(object)((KingbaseInterval)value(false)).ToTimeSpan();
#if NET6_0_OR_GREATER
        if (typeof(T) == typeof(DateOnly)) return (T)(object)(type.CodecName == "mysql_date"
            ? DateOnly.FromDateTime(((KingbaseMySqlDate)value(true)).ToDateTime()) : ((KingbaseDate)value(true)).ToDateOnly());
        if (typeof(T) == typeof(TimeOnly))
        {
            if (type.Name != "time")
                throw new InvalidCastException("TimeOnly access requires a TIME column.");
            var time = (TimeSpan)value(false);
            if (time.Ticks < 0 || time.Ticks >= TimeSpan.TicksPerDay) throw new OverflowException("This TIME duration cannot be represented by TimeOnly.");
            return (T)(object)TimeOnly.FromTimeSpan(time);
        }
#endif
        return (T)(typeof(T) == typeof(KingbaseNumeric) || typeof(T) == typeof(KingbaseDate) || typeof(T) == typeof(KingbaseMySqlDate) || typeof(T) == typeof(KingbaseMySqlDateTime) ||
            typeof(T) == typeof(KingbaseTimestamp) || typeof(T) == typeof(KingbaseTimestampTz) ? value(true) : value(false));
    }
    public override int GetValues(object[] values) => CopyValues(values, provider: false);
    public override int GetProviderSpecificValues(object[] values) => CopyValues(values, provider: true);
    int CopyValues(object[] values, bool provider)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        EnsureRow();
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++) values[i] = provider ? GetProviderSpecificValue(i) : GetValue(i);
        return count;
    }
    public override bool IsDBNull(int ordinal) => _streaming is not null ? IsDBNullAsync(ordinal, CancellationToken.None).GetAwaiter().GetResult() : RawValue(ordinal) is null;
    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);
    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);
    public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);
    public override short GetInt16(int ordinal) => GetFieldValue<short>(ordinal);
    public override int GetInt32(int ordinal) => GetFieldValue<int>(ordinal);
    public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);
    public override float GetFloat(int ordinal) => GetFieldValue<float>(ordinal);
    public override double GetDouble(int ordinal) => GetFieldValue<double>(ordinal);
    public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);
    public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);
    public override DateTime GetDateTime(int ordinal) => GetFieldValue<DateTime>(ordinal);
    public override Guid GetGuid(int ordinal) => GetFieldValue<Guid>(ordinal);

    /// <summary>Copies buffered bytes. A null buffer returns the full length.</summary>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        if (_streaming is not null) { Column(ordinal); ValidateSlice(dataOffset, buffer, bufferOffset, length); return _streaming.CopyBytesAsync(ordinal, dataOffset, buffer, bufferOffset, length, _codecs).GetAwaiter().GetResult(); }
        var source = PartialValue<byte[]>(ordinal);
        ValidateSlice(dataOffset, buffer, bufferOffset, length);
        if (buffer is null) return source.Length;
        var count = SliceCount(source.Length, dataOffset, length);
        if (count != 0) Array.Copy(source, (int)dataOffset, buffer, bufferOffset, count);
        return count;
    }
    /// <summary>Copies buffered UTF16 characters. A null buffer returns the full length.</summary>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        if (_streaming is not null) { Column(ordinal); ValidateSlice(dataOffset, buffer, bufferOffset, length); return _streaming.CopyCharsAsync(ordinal, dataOffset, buffer, bufferOffset, length, _codecs).GetAwaiter().GetResult(); }
        var source = PartialValue<string>(ordinal);
        ValidateSlice(dataOffset, buffer, bufferOffset, length);
        if (buffer is null) return source.Length;
        var count = SliceCount(source.Length, dataOffset, length);
        if (count != 0) source.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }
    public override Stream GetStream(int ordinal) { Column(ordinal); return _streaming is not null ? _streaming.StreamAsync(ordinal, _codecs).GetAwaiter().GetResult() : new MemoryStream(GetFieldValue<byte[]>(ordinal), writable: false); }
    public override TextReader GetTextReader(int ordinal) { Column(ordinal); return _streaming is not null ? _streaming.TextReaderAsync(ordinal, _codecs).GetAwaiter().GetResult() : new StringReader(GetString(ordinal)); }
    T PartialValue<T>(int ordinal) where T : class
    {
        EnsureRow();
        if (_partialValues is not null && _partialValues.TryGetValue(ordinal, out var value)) return (T)value;
        var decoded = GetFieldValue<T>(ordinal);
        (_partialValues ??= new Dictionary<int, object>()).Add(ordinal, decoded);
        return decoded;
    }
    static void ValidateSlice(long dataOffset, Array? buffer, int bufferOffset, int length)
    {
        if (dataOffset < 0) throw new ArgumentOutOfRangeException(nameof(dataOffset));
        if (bufferOffset < 0) throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (buffer is not null && (bufferOffset > buffer.Length || length > buffer.Length - bufferOffset))
            throw new ArgumentException("The destination buffer cannot hold the requested slice.");
    }
    static int SliceCount(int sourceLength, long dataOffset, int length) =>
        dataOffset >= sourceLength ? 0 : (int)Math.Min(sourceLength - dataOffset, length);

    public override IEnumerator GetEnumerator() { EnsureOpen(); return new DbEnumerator(this, closeReader: false); }
    public ReadOnlyCollection<DbColumn> GetColumnSchema()
    {
        var schema = new List<DbColumn>(FieldCount);
        for (var i = 0; i < FieldCount; i++)
            schema.Add(new ResultColumn(GetName(i), i, GetFieldType(i), QualifiedTypeName(i), Column(i).TypeModifier, Current!.Metadata?[i]));
        return schema.AsReadOnly();
    }
    // Parameter binding expects pg_catalog base names bare and every other catalog type qualified.
    string QualifiedTypeName(int ordinal)
    {
        var type = _catalog.ByOid[Column(ordinal).TypeOid];
        return type.Namespace == "pg_catalog" ? type.Name : type.Namespace + "." + type.Name;
    }
    sealed class ResultColumn : DbColumn
    {
        internal ResultColumn(string name, int ordinal, Type type, string typeName, int modifier, PgColumnMetadata? metadata)
        {
            ColumnName = name;
            ColumnOrdinal = ordinal;
            DataType = type;
            DataTypeName = typeName;
            if (metadata is not null)
            {
                BaseCatalogName = metadata.Catalog; BaseSchemaName = metadata.Schema; BaseTableName = metadata.Table; BaseColumnName = metadata.Column;
                AllowDBNull = metadata.AllowNull; IsKey = metadata.Key; IsUnique = metadata.Unique;
                IsAutoIncrement = metadata.AutoIncrement; IsIdentity = metadata.Identity; IsReadOnly = metadata.ReadOnly;
                IsExpression = metadata.Expression; IsHidden = false; IsAliased = metadata.Column is null ? null : name != metadata.Column;
            }
            if (typeName is "varchar" or "bpchar" && modifier > 4) ColumnSize = modifier - 4;
            if (type == typeof(System.Collections.BitArray) && typeName is "bit" or "varbit" && modifier > 0) ColumnSize = modifier;
            if (typeName == "numeric" && modifier >= 4)
            {
                var encoded = modifier - 4;
                var precision = encoded >> 16;
                var scale = ((encoded & 0x7ff) ^ 0x400) - 0x400;
                // Only expose the supported PG numeric encoding; unrecognized modifiers stay unknown.
                if ((encoded & 0xf800) == 0 && precision is >= 1 and <= 1000 && scale is >= -1000 and <= 1000)
                {
                    NumericPrecision = precision;
                    NumericScale = scale;
                }
            }
        }
    }
    public override DataTable GetSchemaTable()
    {
        var table = new DataTable("SchemaTable") { Locale = CultureInfo.InvariantCulture };
        foreach (var (name, type) in new (string, Type)[] {
            (SchemaTableColumn.ColumnName, typeof(string)), (SchemaTableColumn.ColumnOrdinal, typeof(int)),
            (SchemaTableColumn.ColumnSize, typeof(int)), (SchemaTableColumn.NumericPrecision, typeof(short)),
            (SchemaTableColumn.NumericScale, typeof(short)), (SchemaTableColumn.DataType, typeof(Type)),
            ("ProviderSpecificDataType", typeof(Type)), (SchemaTableColumn.AllowDBNull, typeof(bool)),
            (SchemaTableColumn.IsKey, typeof(bool)), (SchemaTableColumn.IsUnique, typeof(bool)),
            (SchemaTableColumn.BaseSchemaName, typeof(string)), (SchemaTableOptionalColumn.BaseCatalogName, typeof(string)),
            (SchemaTableOptionalColumn.IsAutoIncrement, typeof(bool)), (SchemaTableOptionalColumn.IsReadOnly, typeof(bool)),
            ("IsIdentity", typeof(bool)), (SchemaTableColumn.IsAliased, typeof(bool)), (SchemaTableColumn.IsExpression, typeof(bool)), (SchemaTableOptionalColumn.IsHidden, typeof(bool)),
            (SchemaTableColumn.BaseTableName, typeof(string)), (SchemaTableColumn.BaseColumnName, typeof(string)),
            ("DataTypeName", typeof(string)), ("DataTypeOID", typeof(uint)), ("BaseTableOID", typeof(uint)),
            ("ColumnAttributeNumber", typeof(short)), ("TypeModifier", typeof(int)) }) table.Columns.Add(name, type);
        var schema = GetColumnSchema();
        for (var i = 0; i < schema.Count; i++)
        {
            var column = Column(i);
            var row = table.NewRow();
            row[SchemaTableColumn.ColumnName] = column.Name;
            row[SchemaTableColumn.ColumnOrdinal] = i;
            row[SchemaTableColumn.DataType] = schema[i].DataType!;
            row["ProviderSpecificDataType"] = GetProviderSpecificFieldType(i);
            row["DataTypeName"] = schema[i].DataTypeName!;
            row["DataTypeOID"] = column.TypeOid;
            row["BaseTableOID"] = column.TableOid;
            row["ColumnAttributeNumber"] = column.AttributeNumber;
            row["TypeModifier"] = column.TypeModifier;
            if (schema[i].ColumnSize is { } size) row[SchemaTableColumn.ColumnSize] = size;
            if (schema[i].NumericPrecision is { } precision) row[SchemaTableColumn.NumericPrecision] = (short)precision;
            if (schema[i].NumericScale is { } scale) row[SchemaTableColumn.NumericScale] = (short)scale;
            var known = schema[i];
            row[SchemaTableOptionalColumn.BaseCatalogName] = (object?)known.BaseCatalogName ?? DBNull.Value;
            row[SchemaTableColumn.BaseSchemaName] = (object?)known.BaseSchemaName ?? DBNull.Value;
            row[SchemaTableColumn.BaseTableName] = (object?)known.BaseTableName ?? DBNull.Value;
            row[SchemaTableColumn.BaseColumnName] = (object?)known.BaseColumnName ?? DBNull.Value;
            row[SchemaTableColumn.AllowDBNull] = (object?)known.AllowDBNull ?? DBNull.Value;
            row[SchemaTableColumn.IsKey] = (object?)known.IsKey ?? DBNull.Value;
            row[SchemaTableColumn.IsUnique] = (object?)known.IsUnique ?? DBNull.Value;
            row[SchemaTableOptionalColumn.IsAutoIncrement] = (object?)known.IsAutoIncrement ?? DBNull.Value;
            row[SchemaTableOptionalColumn.IsReadOnly] = (object?)known.IsReadOnly ?? DBNull.Value;
            row["IsIdentity"] = (object?)known.IsIdentity ?? DBNull.Value;
            row[SchemaTableColumn.IsAliased] = (object?)known.IsAliased ?? DBNull.Value;
            row[SchemaTableColumn.IsExpression] = (object?)known.IsExpression ?? DBNull.Value;
            row[SchemaTableOptionalColumn.IsHidden] = (object?)known.IsHidden ?? DBNull.Value;
            table.Rows.Add(row);
        }
        return table;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) { if (_streaming is null) return BufferedAsync(Read, cancellationToken); EnsureOpen(); return _streaming.ReadAsync(cancellationToken); }
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) { if (_streaming is null) return BufferedAsync(NextResult, cancellationToken); EnsureOpen(); return _streaming.NextResultAsync(cancellationToken); }
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) { if (_streaming is null) return BufferedAsync(() => IsDBNull(ordinal), cancellationToken); Column(ordinal); return _streaming.IsDBNullAsync(ordinal, cancellationToken); }
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => _streaming is null ? BufferedAsync(() => GetFieldValue<T>(ordinal), cancellationToken) : StreamingFieldValueAsync<T>(ordinal, cancellationToken);
    Task<T> StreamingFieldValueAsync<T>(int ordinal, CancellationToken token) =>
        _streaming!.TypedValueAsync<T>(ordinal, _codecs, _catalog.ByOid[Column(ordinal).TypeOid], token);
    static Task<T> BufferedAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
        try { return Task.FromResult(operation()); }
        catch (Exception e) { return Task.FromException<T>(e); }
    }
    public override void Close()
    {
        if (_streaming is not null && !IsClosed)
        {
            _streaming.CheckAccess();
            _streaming.CloseAsync(CloseBuffered).GetAwaiter().GetResult();
            return;
        }
        CloseBuffered();
    }
    void CloseBuffered()
    {
        if (Interlocked.Exchange(ref _results, null) is null) return;
        _partialValues = null;
        _rowIndex = -1;
        Interlocked.Exchange(ref _onClose, null)?.Invoke(this);
    }
    protected override void Dispose(bool disposing) { if (disposing) Close(); base.Dispose(disposing); }
#if !NETSTANDARD2_0
    public override async Task CloseAsync()
    {
        if (_streaming is null || IsClosed) { Close(); return; }
        _streaming.CheckAccess();
        await _streaming.CloseAsync(CloseBuffered).ConfigureAwait(false);
    }
    public override async ValueTask DisposeAsync() { await CloseAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }
#endif

    PgColumnDescription Column(int ordinal)
    {
        var current = Current;
        if (ordinal < 0 || current is null || ordinal >= current.Columns.Count)
            throw new IndexOutOfRangeException("Invalid Kingbase result column ordinal.");
        return current.Columns[ordinal];
    }
    string? RawValue(int ordinal) { Column(ordinal); EnsureRow(); return Current!.Rows[_rowIndex][ordinal]; }
    void EnsureRow()
    {
        if (_streaming is not null) { EnsureOpen(); if (!_streaming.OnRow) throw new InvalidOperationException("The Kingbase reader is not positioned on a row."); return; }
        var current = Current;
        if (current is null || _rowIndex < 0 || _rowIndex >= current.Rows.Count || (_singleRow && _rowIndex >= 1))
            throw new InvalidOperationException("The Kingbase reader is not positioned on a row.");
    }
    void EnsureOpen() { _streaming?.CheckAccess(); if (IsClosed) throw new InvalidOperationException("The Kingbase reader is closed."); }
    internal static int? AffectedCount(string? tag)
    {
        if (tag is null) return null;
        var parts = tag.Split(' ');
        var insert = parts[0] == "INSERT";
        if (!insert && parts[0] != "UPDATE" && parts[0] != "DELETE" && parts[0] != "MERGE") return null;
        if (parts.Length != (insert ? 3 : 2) ||
            (insert && !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))) throw InvalidTag();
        var count = parts[parts.Length - 1];
        if (count.Length == 0 || count.Any(c => c < '0' || c > '9')) throw InvalidTag();
        if (!int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new OverflowException("PG affected row count exceeds Int32.");
        return value;
    }
    static FormatException InvalidTag() => new("Invalid PG affected row command tag.");
}
