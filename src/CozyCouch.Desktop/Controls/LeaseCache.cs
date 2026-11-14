namespace CozyCouch.Desktop.Controls;

/// <summary>
/// A least-recently-used cache whose values can be on screen. A value that falls out of the cache while something
/// still holds it is disposed when the last holder lets go, never before. Not thread-safe: use it from the UI thread.
/// </summary>
public sealed class LeaseCache<T>(int capacity)
    where T : class, IDisposable
{
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly LinkedList<Entry> _order = []; // newest first

    /// <summary>A cached value and who holds it. <see cref="Value"/> is null for a picture that could not be decoded.</summary>
    public sealed class Entry
    {
        internal Entry(string key, T? value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; }

        public T? Value { get; }

        internal int Holders { get; set; }

        internal bool Evicted { get; set; }

        internal LinkedListNode<Entry>? Node { get; set; }
    }

    public int Capacity => capacity;

    public int Count => _entries.Count;

    /// <summary>The entry for the key, held by the caller until <see cref="Release"/>; null when it is not cached.</summary>
    public Entry? Acquire(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;

        _order.Remove(entry.Node!);
        _order.AddFirst(entry.Node!);
        entry.Holders++;
        return entry;
    }

    /// <summary>
    /// Stores a value, held by the caller until <see cref="Release"/>. When the key is already cached, the existing
    /// entry is returned and <paramref name="value"/> is disposed.
    /// </summary>
    public Entry Add(string key, T? value)
    {
        if (Acquire(key) is { } existing)
        {
            if (!ReferenceEquals(existing.Value, value)) value?.Dispose();
            return existing;
        }

        var entry = new Entry(key, value) { Holders = 1 };
        entry.Node = _order.AddFirst(entry);
        _entries[key] = entry;
        while (_entries.Count > capacity) Evict(_order.Last!.Value);
        return entry;
    }

    public void Release(Entry entry)
    {
        entry.Holders--;
        if (entry.Evicted && entry.Holders == 0) entry.Value?.Dispose();
    }

    private void Evict(Entry entry)
    {
        _order.Remove(entry.Node!);
        _entries.Remove(entry.Key);
        entry.Evicted = true;
        if (entry.Holders == 0) entry.Value?.Dispose();
    }
}
