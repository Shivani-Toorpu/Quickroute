namespace QuickRoute.Cache;

public class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
    private readonly LinkedList<(TKey Key, TValue Value)> _list;
    private readonly object _lock = new();

    private int _hits = 0;
    private int _misses = 0;

    public int Hits => _hits;
    public int Misses => _misses;
    public double HitRate => (_hits + _misses) == 0 ? 0 : (double)_hits / (_hits + _misses) * 100;

    public LruCache(int capacity)
    {
        _capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>>(capacity);
        _list = new LinkedList<(TKey Key, TValue Value)>();
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                // Move to head — this is now the most recently used
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                Interlocked.Increment(ref _hits);
                return true;
            }
            value = default;
            Interlocked.Increment(ref _misses);
            return false;
        }
    }

    public void Put(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                // Already exists — update and move to head
                _list.Remove(existing);
                _map.Remove(key);
            }
            else if (_map.Count >= _capacity)
            {
                // Cache full — evict least recently used (tail)
                var lru = _list.Last!;
                _list.RemoveLast();
                _map.Remove(lru.Value.Key);
            }

            var node = new LinkedListNode<(TKey Key, TValue Value)>((key, value));
            _list.AddFirst(node);
            _map[key] = node;
        }
    }
}