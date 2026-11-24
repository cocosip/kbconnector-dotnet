using System.Collections;
using System.Data.Common;

namespace KingbaseConnector;

/// <summary>Owns distinct parameters with unique normalized nonempty names.</summary>
public sealed class KingbaseParameterCollection : DbParameterCollection
{
    readonly List<KingbaseParameter> _items = [];
    readonly object _syncRoot = new();
    public new KingbaseParameter this[int index] { get => (KingbaseParameter)GetParameter(index); set => SetParameter(index, value); }
    public new KingbaseParameter this[string name] { get => (KingbaseParameter)GetParameter(name); set => SetParameter(name, value); }
    public KingbaseParameter AddWithValue(string? name, object? value) { var p = new KingbaseParameter(name, value); Add(p); return p; }
    public KingbaseParameter Add(string? name, KingbaseDbType type) { var p = new KingbaseParameter { ParameterName = name!, KingbaseDbType = type }; Add(p); return p; }
    public override int Count => _items.Count;
    public override object SyncRoot => _syncRoot;
    public override int Add(object value) { Insert(Count, value); return Count - 1; }
    public override void AddRange(Array values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        var staged = new List<KingbaseParameter>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var instances = new HashSet<KingbaseParameter>();
        foreach (var value in values)
        {
            var p = RequireParameter(value);
            ValidateNew(p);
            var name = NormalizeName(p.ParameterName);
            if (!instances.Add(p) || (name.Length != 0 && !names.Add(name))) throw InvalidParameter();
            staged.Add(p);
        }
        foreach (var p in staged) { _items.Add(p); p.Owner = this; }
    }
    public override void Clear() { foreach (var p in _items) p.Owner = null; _items.Clear(); }
    public override bool Contains(object value) => IndexOf(value) >= 0;
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => value is KingbaseParameter p ? _items.IndexOf(p) : -1;
    public override int IndexOf(string parameterName)
    {
        if (parameterName is null) return -1;
        var name = NormalizeName(parameterName);
        return _items.FindIndex(p => string.Equals(NormalizeName(p.ParameterName), name, StringComparison.OrdinalIgnoreCase));
    }
    public override void Insert(int index, object value)
    {
        if (index < 0 || index > Count) throw new ArgumentOutOfRangeException(nameof(index));
        var p = RequireParameter(value);
        ValidateNew(p);
        _items.Insert(index, p);
        p.Owner = this;
    }
    public override void Remove(object value)
    {
        var index = IndexOf(value);
        if (index < 0) throw InvalidParameter();
        RemoveAt(index);
    }
    public override void RemoveAt(int index)
    {
        var p = _items[index];
        _items.RemoveAt(index);
        p.Owner = null;
    }
    public override void RemoveAt(string parameterName) => RemoveAt(Find(parameterName));
    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName) => _items[Find(parameterName)];
    protected override void SetParameter(int index, DbParameter value)
    {
        var old = _items[index];
        if (ReferenceEquals(old, value)) return;
        var p = RequireParameter(value);
        if (p.Owner is not null) throw InvalidParameter();
        ValidateName(old, p.ParameterName);
        _items[index] = p;
        old.Owner = null;
        p.Owner = this;
    }
    protected override void SetParameter(string parameterName, DbParameter value) => SetParameter(Find(parameterName), value);

    internal void ValidateName(KingbaseParameter current, string name)
    {
        var normalized = NormalizeName(name);
        if (normalized.Length != 0 && _items.Any(p => !ReferenceEquals(p, current) &&
            string.Equals(NormalizeName(p.ParameterName), normalized, StringComparison.OrdinalIgnoreCase))) throw InvalidParameter();
    }
    void ValidateNew(KingbaseParameter p)
    {
        if (p.Owner is not null) throw InvalidParameter();
        ValidateName(p, p.ParameterName);
    }
    int Find(string name) { var index = IndexOf(name); return index >= 0 ? index : throw new IndexOutOfRangeException("Kingbase parameter was not found."); }
    static string NormalizeName(string name) => name.Length != 0 && name[0] is '@' or ':' ? name.Substring(1) : name;
    static KingbaseParameter RequireParameter(object? value) => value as KingbaseParameter ?? throw InvalidParameter();
    static ArgumentException InvalidParameter() => new("Invalid, duplicate or already owned Kingbase parameter.");
}
