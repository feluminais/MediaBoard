using MediaBoard.Core.Activity;
using MediaBoard.Core.Parsing;
using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Scanning;

/// <summary>
/// Keeps the database current while the app is open: one <see cref="FileSystemWatcher"/> per enabled library
/// folder, and a debounced rescan of a folder once its files stop changing. A folder never has two scans at
/// once; a change that lands during a scan queues exactly one more. It only reads the library folders.
/// </summary>
public sealed class LibraryWatcher : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    private const int BufferSize = 64 * 1024;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    // Extensions of files that are never videos or folders. A path that is already gone can't be checked, so
    // one with any other extension might have been a folder ("Heat.1995.1080p.x264-GRP", "Mr. Robot").
    private static readonly HashSet<string> NonVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".nfo", ".md", ".jpg", ".jpeg", ".png", ".srt", ".ass", ".sub", ".idx", ".sfv", ".md5",
        ".exe", ".bin", ".ini", ".bat", ".url", ".torrent",
    };

    private readonly LibraryScanner _scanner;
    private readonly FolderStore _folders;
    private readonly ActivityHub _hub;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly Dictionary<int, Watch> _watches = [];
    private bool _started;
    private bool _disposed;

    public LibraryWatcher(LibraryScanner scanner, FolderStore folders, ActivityHub hub, TimeSpan? debounce = null)
    {
        _scanner = scanner;
        _folders = folders;
        _hub = hub;
        _debounce = debounce ?? DefaultDebounce;
    }

    /// <summary>Raised on a background thread after a folder's rescan finished, with the folder id.</summary>
    public event Action<int>? Rescanned;

    /// <summary>Starts watching every enabled folder that exists. Does nothing when already started.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
        }

        Refresh();
    }

    /// <summary>
    /// Re-reads the folder list and adds or drops watchers to match: call it after a folder was added, removed,
    /// enabled, disabled or repointed, or came back after being unavailable. A watcher that reported an error is
    /// replaced when its folder exists. Does nothing before <see cref="Start"/>.
    /// </summary>
    public void Refresh()
    {
        // Read the store outside the lock; it only touches the database.
        var wanted = _folders.All()
            .Where(f => f.Enabled && Directory.Exists(f.Path))
            .ToDictionary(f => f.Id);

        List<Watch> dropped = [];
        HashSet<int> recovering = [];
        lock (_gate)
        {
            if (!_started || _disposed) return;

            foreach (var (id, watch) in _watches.ToArray())
            {
                // A watcher that reported an error may be dead (its folder vanished); a fresh one replaces it.
                if (wanted.TryGetValue(id, out var folder) && watch.Folder.Path == folder.Path && !watch.Broken) continue;
                // The broken watch had a rescan planned or running that its shutdown cancels.
                if (watch.Broken && wanted.ContainsKey(id)) recovering.Add(id);
                _watches.Remove(id);
                watch.Closed = true;
                dropped.Add(watch);
            }

            foreach (var folder in wanted.Values)
            {
                if (_watches.ContainsKey(folder.Id)) continue;
                var watch = TryWatch(folder);
                if (watch is null) continue;
                _watches[folder.Id] = watch;
                // Events were lost while the old watch was broken: the replacement rescans what it missed.
                if (recovering.Contains(folder.Id)) Schedule(watch);
            }
        }

        foreach (var watch in dropped) Shutdown(watch);
    }

    /// <summary>Stops watching and waits for running scans to end. <see cref="Start"/> can be called again.</summary>
    public void Stop()
    {
        List<Watch> all;
        lock (_gate)
        {
            _started = false;
            all = [.. _watches.Values];
            _watches.Clear();
            foreach (var watch in all) watch.Closed = true;
        }

        foreach (var watch in all) Shutdown(watch);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
    }

    private Watch? TryWatch(LibraryFolder folder)
    {
        FileSystemWatcher? fsw = null;
        Watch? watch = null;
        try
        {
            fsw = new FileSystemWatcher(folder.Path)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = BufferSize,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            var created = watch = new Watch(folder, fsw);
            created.Timer = new Timer(_ => OnTimer(created), null, Timeout.Infinite, Timeout.Infinite);

            // Only changes the scanner cares about: a torrent client writing a partial download, or a file in a
            // hidden folder, would otherwise keep the folder rescanning for as long as the download runs.
            fsw.Created += (_, e) =>
            {
                if (Matters(folder.Path, e.FullPath)) Schedule(created);
            };
            // A directory's own Changed event is only a timestamp update. Windows also fires it when the scan
            // opens a file for the first time, which would queue a pointless second scan after every scan.
            fsw.Changed += (_, e) =>
            {
                if (!Directory.Exists(e.FullPath) && Matters(folder.Path, e.FullPath)) Schedule(created);
            };
            fsw.Deleted += (_, e) =>
            {
                if (Matters(folder.Path, e.FullPath)) Schedule(created);
            };
            fsw.Renamed += (_, e) =>
            {
                if (Matters(folder.Path, e.FullPath) || Matters(folder.Path, e.OldFullPath)) Schedule(created);
            };
            fsw.Error += (_, e) =>
            {
                // The buffer overflowed or the folder vanished: individual events are lost, so rescan it all.
                created.Broken = true;
                _hub.Log(ActivityLevel.Warning,
                    $"Lost track of changes in {Path.GetFileName(folder.Path)}; rescanning it.",
                    e.GetException().Message);
                Schedule(created);
            };
            fsw.EnableRaisingEvents = true;
            return created;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _hub.Log(ActivityLevel.Warning, $"Can't watch {Path.GetFileName(folder.Path)} for changes.", ex.Message);
            fsw?.Dispose();
            watch?.Timer?.Dispose();
            watch?.Cancel.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Whether a change to <paramref name="fullPath"/> can change what a scan finds: a video outside hidden folders
    /// and not a partial download, or a folder (it may hold videos). A path that is already gone can't be checked,
    /// so it counts unless it has a known non-video file extension.
    /// </summary>
    private static bool Matters(string folderPath, string fullPath)
    {
        // The size is unknown here, so the small-sample rule is left to the scan.
        if (MediaFileRules.IsIgnored(Path.GetRelativePath(folderPath, fullPath), long.MaxValue)) return false;
        if (MediaFileRules.IsVideo(fullPath)) return true;
        if (Directory.Exists(fullPath)) return true;
        if (File.Exists(fullPath)) return false;
        return !NonVideoExtensions.Contains(Path.GetExtension(fullPath));
    }

    /// <summary>Restarts the folder's quiet-period timer.</summary>
    private void Schedule(Watch watch)
    {
        lock (_gate)
        {
            if (watch.Closed) return;
            watch.Timer!.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer(Watch watch)
    {
        lock (_gate)
        {
            if (watch.Closed) return;
            if (watch.Scanning)
            {
                // Already scanning: the scan loop runs exactly one more pass when this one ends.
                watch.Pending = true;
                return;
            }

            watch.Scanning = true;
            watch.Running = Task.Run(() => RunScans(watch));
        }
    }

    private async Task RunScans(Watch watch)
    {
        while (true)
        {
            try
            {
                await _scanner.ScanAsync(watch.Folder, watch.Cancel.Token).ConfigureAwait(false);
                if (!watch.Closed) Rescanned?.Invoke(watch.Folder.Id);
            }
            catch (OperationCanceledException) when (watch.Cancel.IsCancellationRequested)
            {
                // Stopping: the scan transaction rolled back, the next start rescans.
            }
            catch (Exception ex)
            {
                // A failed scan or a failing Rescanned subscriber must not end the watcher.
                _hub.Log(ActivityLevel.Error, $"Rescan of {Path.GetFileName(watch.Folder.Path)} failed.", ex.Message);
            }

            lock (_gate)
            {
                if (watch.Pending && !watch.Closed)
                {
                    watch.Pending = false;
                    continue;
                }

                watch.Scanning = false;
                return;
            }
        }
    }

    /// <summary>Stops one folder's events and timer, cancels its scan and waits for it to end. Needs Closed set.</summary>
    private void Shutdown(Watch watch)
    {
        watch.Watcher.EnableRaisingEvents = false;
        watch.Watcher.Dispose();

        Task? running;
        lock (_gate)
        {
            watch.Timer!.Dispose();
            running = watch.Running;
        }

        watch.Cancel.Cancel();
        try
        {
            running?.Wait(StopTimeout);
        }
        catch (AggregateException)
        {
            // RunScans handles its own failures; nothing to surface here.
        }

        watch.Cancel.Dispose();
    }

    private sealed class Watch(LibraryFolder folder, FileSystemWatcher watcher)
    {
        public LibraryFolder Folder { get; } = folder;
        public FileSystemWatcher Watcher { get; } = watcher;
        public CancellationTokenSource Cancel { get; } = new();
        public Timer? Timer { get; set; }
        public Task? Running { get; set; }
        public volatile bool Closed;
        public volatile bool Broken;
        public bool Scanning { get; set; }
        public bool Pending { get; set; }
    }
}
