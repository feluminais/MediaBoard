namespace MediaBoard.Core.Accounts;

/// <summary>
/// Locks a name after 5 failures for 15 minutes and an address after 20 for an hour. Counters live in memory, so a
/// restart clears them. Failures older than the lock period are forgotten. Each tally holds at most
/// <see cref="MaxEntries"/> keys, since names and addresses come from strangers. Any thread may call it.
/// </summary>
public sealed class SignInGuard
{
    public const int MaxEntries = 10_000;

    private readonly Lock _gate = new();
    private readonly Tally _names;
    private readonly Tally _addresses;

    public SignInGuard(TimeProvider time)
    {
        _names = new Tally(5, TimeSpan.FromMinutes(15), time);
        _addresses = new Tally(20, TimeSpan.FromHours(1), time);
    }

    /// <param name="name">Null when there is no name to go by.</param>
    /// <param name="address">Null when there is no address to go by.</param>
    public bool IsLocked(string? name, string? address)
    {
        lock (_gate) return (name is not null && _names.IsLocked(name)) || (address is not null && _addresses.IsLocked(address));
    }

    /// <param name="name">Null to count only against the address.</param>
    /// <param name="address">Null to count only against the name.</param>
    public void RecordFailure(string? name, string? address)
    {
        lock (_gate)
        {
            if (name is not null) _names.Fail(name);
            if (address is not null) _addresses.Fail(address);
        }
    }

    /// <summary>Forgets the name's failures but not an active lock. The address keeps its own failures.</summary>
    public void RecordSuccess(string name)
    {
        lock (_gate) _names.ClearFailures(name);
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
            if (!_entries.TryGetValue(key, out var entry))
            {
                if (_entries.Count >= MaxEntries) MakeRoom(now);
                // Everything held is locked: a new key goes untracked rather than evicting a lock.
                if (_entries.Count >= MaxEntries) return;
                _entries[key] = entry = new Entry(key);
            }

            entry.Touched = now;
            if (entry.LockedUntil > now) return;

            entry.Failures.RemoveAll(at => now - at >= period);
            entry.Failures.Add(now);
            if (entry.Failures.Count < limit) return;

            entry.Failures.Clear();
            entry.LockedUntil = now + period;
        }

        public void ClearFailures(string key)
        {
            if (!_entries.TryGetValue(key, out var entry)) return;
            entry.Failures.Clear();
            if (entry.LockedUntil <= time.GetUtcNow()) _entries.Remove(key);
        }

        public List<string> Locked()
        {
            var now = time.GetUtcNow();
            return _entries.Values.Where(e => e.LockedUntil > now).Select(e => e.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Runs only when the table is full: drops what has expired, then the oldest tenth of the unlocked rest, so
        // the cost is spread over many inserts. A lock is never dropped.
        private void MakeRoom(DateTimeOffset now)
        {
            var unlocked = _entries.Values.Where(e => e.LockedUntil <= now).ToList();
            var stale = unlocked.Where(e => now - e.Touched >= period).ToList();
            var victims = stale.Count > 0
                ? stale
                : unlocked.OrderBy(e => e.Touched).Take(Math.Max(1, MaxEntries / 10)).ToList();
            foreach (var entry in victims) _entries.Remove(entry.Key);
        }
    }

    private sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public List<DateTimeOffset> Failures { get; } = [];
        public DateTimeOffset LockedUntil { get; set; }
        public DateTimeOffset Touched { get; set; }
    }
}
