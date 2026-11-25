using System.Collections;
using System.Data.Common;

namespace KingbaseConnector;

/// <summary>Owns distinct commands in execution order. A batch accepts at most 256 commands.</summary>
public sealed class KingbaseBatchCommandCollection
#if NET6_0_OR_GREATER
    : DbBatchCommandCollection
#else
    : IList<KingbaseBatchCommand>
#endif
{
    readonly List<KingbaseBatchCommand> _items = [];
    internal KingbaseBatch Batch { get; }
    internal KingbaseBatchCommandCollection(KingbaseBatch batch) => Batch = batch;
    internal KingbaseBatchCommand[] Snapshot() => _items.ToArray();
    public
#if NET6_0_OR_GREATER
        new
#endif
        KingbaseBatchCommand this[int index] { get { lock (Batch.SyncRoot) return _items[index]; } set => Change(() => { var old = _items[index]; if (ReferenceEquals(old, value)) return; Validate(value); _items[index] = value; Detach(old); value.Owner = this; }); }
    public
#if NET6_0_OR_GREATER
        override
#endif
        int Count { get { lock (Batch.SyncRoot) return _items.Count; } }
    public
#if NET6_0_OR_GREATER
        override
#endif
        bool IsReadOnly => false;
    public void Add(KingbaseBatchCommand item) => Insert(Count, item);
    public void Insert(int index, KingbaseBatchCommand item) => Change(() =>
    {
        if (_items.Count >= 256) throw new NotSupportedException("A Kingbase batch accepts at most 256 commands.");
        Validate(item); _items.Insert(index, item); item.Owner = this;
    });
    public bool Contains(KingbaseBatchCommand item) => IndexOf(item) >= 0;
    public int IndexOf(KingbaseBatchCommand item) { lock (Batch.SyncRoot) return _items.IndexOf(item); }
    public bool Remove(KingbaseBatchCommand item)
    {
        lock (Batch.SyncRoot) { Batch.EnsureEditable(); var index = _items.IndexOf(item); if (index < 0) return false; RemoveAt(index); return true; }
    }
    public void CopyTo(KingbaseBatchCommand[] array, int arrayIndex) { lock (Batch.SyncRoot) _items.CopyTo(array, arrayIndex); }
    public
#if NET6_0_OR_GREATER
        override
#endif
        void Clear() => Change(() => { foreach (var item in _items) Detach(item); _items.Clear(); });
    public
#if NET6_0_OR_GREATER
        override
#endif
        void RemoveAt(int index) => Change(() => { var item = _items[index]; _items.RemoveAt(index); Detach(item); });
#if NET6_0_OR_GREATER
    public override void Add(DbBatchCommand item) => Add(Require(item));
    public override void Insert(int index, DbBatchCommand item) => Insert(index, Require(item));
    public override bool Contains(DbBatchCommand item) => item is KingbaseBatchCommand command && Contains(command);
    public override int IndexOf(DbBatchCommand item) => item is KingbaseBatchCommand command ? IndexOf(command) : -1;
    public override bool Remove(DbBatchCommand item) => item is KingbaseBatchCommand command && Remove(command);
    public override void CopyTo(DbBatchCommand[] array, int arrayIndex) { lock (Batch.SyncRoot) ((ICollection)_items).CopyTo(array, arrayIndex); }
    public override IEnumerator<KingbaseBatchCommand> GetEnumerator() { lock (Batch.SyncRoot) return ((IEnumerable<KingbaseBatchCommand>)_items.ToArray()).GetEnumerator(); }
    protected override DbBatchCommand GetBatchCommand(int index) => this[index];
    protected override void SetBatchCommand(int index, DbBatchCommand batchCommand) => this[index] = Require(batchCommand);
    static KingbaseBatchCommand Require(DbBatchCommand? value) => value as KingbaseBatchCommand ?? throw InvalidItem();
#else
    public IEnumerator<KingbaseBatchCommand> GetEnumerator() { lock (Batch.SyncRoot) return ((IEnumerable<KingbaseBatchCommand>)_items.ToArray()).GetEnumerator(); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
#endif
    void Change(Action action) { lock (Batch.SyncRoot) { Batch.EnsureEditable(); action(); } }
    static void Detach(KingbaseBatchCommand item) { item.Prepared = null; item.Owner = null; }
    static void Validate(KingbaseBatchCommand? item) { if (item is null || item.Owner is not null) throw InvalidItem(); }
    static ArgumentException InvalidItem() => new("Invalid or already owned Kingbase batch command.");
}
