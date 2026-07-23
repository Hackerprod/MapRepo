namespace MapRepo.NativeStore.Internal.Caching;

/// <summary>
/// Small lock-based LRU used only for decoded/materialized objects. The immutable mapped payload is
/// the source of truth, so eviction has no semantic effect.
/// </summary>
internal sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _lru = new();
    private readonly Func<TKey, TValue, long> _weigher;
    private readonly long _capacity;
    private long _weight;

    public BoundedLruCache(long capacity, Func<TKey, TValue, long>? weigher = null, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _weigher = weigher ?? ((_, _) => 1);
        _entries = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public long CurrentWeight
    {
        get { lock (_gate) return _weight; }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                value = default!;
                return false;
            }
            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (TryGet(key, out var existing)) return existing;
        var created = factory(key);
        Add(key, created);
        return TryGet(key, out existing) ? existing : created;
    }

    public void Add(TKey key, TValue value)
    {
        if (_capacity == 0) return;
        var itemWeight = Math.Max(1, _weigher(key, value));
        if (itemWeight > _capacity) return;
        lock (_gate)
        {
            if (_entries.Remove(key, out var old))
            {
                _lru.Remove(old);
                _weight -= old.Value.Weight;
            }
            var node = new LinkedListNode<Entry>(new Entry(key, value, itemWeight));
            _lru.AddFirst(node);
            _entries.Add(key, node);
            _weight += itemWeight;
            while (_weight > _capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _entries.Remove(last.Value.Key);
                _weight -= last.Value.Weight;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
            _weight = 0;
        }
    }

    private sealed record Entry(TKey Key, TValue Value, long Weight);
}
