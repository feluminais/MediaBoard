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
    private readonly Dictionary<int, ScanTrack> _scans = [];
    private StatusSnapshot _current = new();
    private long _scanOrder;

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

    /// <summary>
    /// Marks a folder as waiting to scan until the scanner starts it, so a folder that was just added, or is in line
    /// behind another folder, never looks empty. Dispose the result once the scan ended or will not run; until
    /// then the folder shows as waiting whenever it is not scanning.
    /// </summary>
    public IDisposable QueueScan(int folderId)
    {
        ChangeScan(folderId, track => track.Queued++);
        return new QueuedScan(this, folderId);
    }

    /// <summary>The scanner started on a folder. Until <see cref="ReportScan"/> the total is unknown (0).</summary>
    public void BeginScan(int folderId, string folderName) => ChangeScan(folderId, track =>
    {
        track.Running++;
        track.Name = folderName;
        track.Done = 0;
        track.Total = 0;
        track.Started = ++_scanOrder;
    });

    public void ReportScan(int folderId, int done, int total) => ChangeScan(folderId, track =>
    {
        if (track.Running == 0) return;
        track.Done = done;
        track.Total = total;
    });

    public void EndScan(int folderId) => ChangeScan(folderId, track => track.Running = Math.Max(0, track.Running - 1));

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

    public void SetLibraryError(string? hint) =>
        Update(s => s.LibraryError == hint ? s : s with { LibraryError = hint });

    public void SetFailedScans(IReadOnlyList<string> names)
    {
        var copy = names.ToArray();
        Update(s => s.FailedScans.SequenceEqual(copy) ? s : s with { FailedScans = copy });
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

    // Changes one folder's scan bookkeeping and rebuilds the snapshot's scan fields from all of it.
    private void ChangeScan(int folderId, Action<ScanTrack> change) => Update(s =>
    {
        if (!_scans.TryGetValue(folderId, out var track)) _scans[folderId] = track = new ScanTrack();
        change(track);
        if (track.Queued == 0 && track.Running == 0) _scans.Remove(folderId);

        var folders = _scans.ToDictionary(
            p => p.Key,
            p => p.Value.Running > 0
                ? new FolderScan(FolderScanState.Scanning, p.Value.Done, p.Value.Total)
                : new FolderScan(FolderScanState.Waiting, 0, 0));
        var latest = _scans.Values.Where(t => t.Running > 0).MaxBy(t => t.Started);
        var (name, done, total) = latest is null ? (null, 0, 0) : (latest.Name, latest.Done, latest.Total);

        var same = s.ScanFolder == name && s.ScanDone == done && s.ScanTotal == total
            && s.FolderScans.Count == folders.Count
            && folders.All(p => s.FolderScans.TryGetValue(p.Key, out var before) && before == p.Value);
        return same ? s : s with { ScanFolder = name, ScanDone = done, ScanTotal = total, FolderScans = folders };
    });

    // Guarded by _gate (ChangeScan runs inside Update's lock).
    private sealed class ScanTrack
    {
        public int Queued;
        public int Running;
        public string Name = "";
        public int Done;
        public int Total;
        public long Started;
    }

    private sealed class QueuedScan(ActivityHub hub, int folderId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            hub.ChangeScan(folderId, track => track.Queued = Math.Max(0, track.Queued - 1));
        }
    }
}
