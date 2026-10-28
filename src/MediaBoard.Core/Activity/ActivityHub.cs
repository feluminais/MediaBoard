namespace MediaBoard.Core.Activity;

/// <summary>
/// Shared state behind the status bar and the activity log. Scanner, identification and playback
/// call it from background threads, so state is guarded by a lock and events fire outside it.
/// </summary>
public sealed class ActivityHub
{
    public const int MaxEntries = 200;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Queue<ActivityEntry> _recent = new();
    private StatusSnapshot _current = new();

    public ActivityHub(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    public event Action<StatusSnapshot>? StatusChanged;

    /// <summary>Raised on the logging thread, outside the lock, after the entry was added to <see cref="Recent"/>.</summary>
    public event Action<ActivityEntry>? EntryLogged;

    public StatusSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>The last <see cref="MaxEntries"/> log entries, oldest first.</summary>
    public IReadOnlyList<ActivityEntry> Recent
    {
        get { lock (_gate) return _recent.ToArray(); }
    }

    public void SetScan(string? folderName, int done, int total) =>
        Update(s => s.ScanFolder == folderName && s.ScanDone == done && s.ScanTotal == total
            ? s
            : s with { ScanFolder = folderName, ScanDone = done, ScanTotal = total });

    public void SetPendingIdentification(int count) =>
        Update(s => s.PendingIdentification == count ? s : s with { PendingIdentification = count });

    public void SetOnline(bool online) =>
        Update(s => s.Online == online ? s : s with { Online = online });

    public void SetKeyState(KeyState state) =>
        Update(s => s.KeyState == state ? s : s with { KeyState = state });

    public void SetPlayer(string? name) =>
        Update(s => s.PlayerName == name ? s : s with { PlayerName = name });

    public void SetUnavailableFolders(IReadOnlyList<string> names)
    {
        var copy = names.ToArray();
        Update(s => s.UnavailableFolders.SequenceEqual(copy) ? s : s with { UnavailableFolders = copy });
    }

    public void Log(ActivityLevel level, string message, string? hint = null)
    {
        var entry = new ActivityEntry(_time.GetLocalNow().DateTime, level, message, hint);
        lock (_gate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > MaxEntries) _recent.Dequeue();
        }

        EntryLogged?.Invoke(entry);
    }

    private void Update(Func<StatusSnapshot, StatusSnapshot> change)
    {
        StatusSnapshot next;
        lock (_gate)
        {
            next = change(_current);
            if (ReferenceEquals(next, _current)) return;
            _current = next;
        }

        StatusChanged?.Invoke(next);
    }
}
