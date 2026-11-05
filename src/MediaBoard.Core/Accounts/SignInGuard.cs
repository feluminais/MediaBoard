namespace MediaBoard.Core.Accounts;

/// <summary>
/// Locks a name after 5 failures for 15 minutes and an address after 20 for an hour. Counters live in memory, so a
/// restart clears them. Failures older than the lock period are forgotten. Any thread may call it.
/// </summary>
public sealed class SignInGuard
{
    private readonly Lock _gate = new();
    private readonly Tally _names;
    private readonly Tally _addresses;

    public SignInGuard(TimeProvider time)
    {
        _names = new Tally(5, TimeSpan.FromMinutes(15), time);
        _addresses = new Tally(20, TimeSpan.FromHours(1), time);
    }

    public bool IsLocked(string name, string address)
    {
        lock (_gate) return _names.IsLocked(name) || _addresses.IsLocked(address);
    }

    public void RecordFailure(string name, string address)
    {
        lock (_gate)
        {
            _names.Fail(name);
            _addresses.Fail(address);
        }
    }

    /// <summary>Forgets the name's failures. The address keeps its own, so one good account can't wash out a guessing run.</summary>
    public void RecordSuccess(string name)
    {
        lock (_gate) _names.Clear(name);
    }

    public IReadOnlyList<string> LockedNames()
    {
        lock (_gate) return _names.Locked();
    }

    private sealed class Tally(int limit, TimeSpan period, TimeProvider time)
    {
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public bool IsLocked(string key) => _entries.TryGetValue(key, out var entry) && entry.LockedUntil > time.GetUtcNow();

        public void Fail(string key)
        {
            var now = time.GetUtcNow();
            Prune(now);
            if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new Entry(key);
            if (entry.LockedUntil > now) return;

            entry.Failures.Add(now);
            if (entry.Failures.Count < limit) return;

            entry.Failures.Clear();
            entry.LockedUntil = now + period;
        }

        public void Clear(string key) => _entries.Remove(key);

        public List<string> Locked()
        {
            var now = time.GetUtcNow();
            Prune(now);
            return _entries.Values.Where(e => e.LockedUntil > now).Select(e => e.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void Prune(DateTimeOffset now)
        {
            foreach (var entry in _entries.Values.ToList())
            {
                entry.Failures.RemoveAll(at => now - at >= period);
                if (entry.Failures.Count == 0 && entry.LockedUntil <= now) _entries.Remove(entry.Key);
            }
        }
    }

    private sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public List<DateTimeOffset> Failures { get; } = [];
        public DateTimeOffset LockedUntil { get; set; }
    }
}
