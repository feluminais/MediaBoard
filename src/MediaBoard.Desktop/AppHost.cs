using MediaBoard.Core.Activity;
using MediaBoard.Core.Identification;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;

namespace MediaBoard.Desktop;

/// <summary>
/// Runs the app's background work. <see cref="Start"/> returns at once; everything slow happens on the thread pool,
/// so the window shows before the database is open or any scan has finished. Startup order: database, connectivity,
/// identification (which picks up files left pending last time), a scan of each enabled folder followed by an
/// identification run, the watcher, then one metadata refresh. <see cref="StopAsync"/> undoes it in reverse.
/// </summary>
public sealed class AppHost(
    ActivityHub hub,
    SettingsStore settings,
    LibraryDatabase db,
    Connectivity connectivity,
    FolderStore folders,
    LibraryScanner scanner,
    LibraryWatcher watcher,
    IdentificationService identification,
    MetadataRefresher refresher,
    LocalLibrary library,
    PlaybackService playback)
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _keyGate = new();
    private readonly Dictionary<int, string> _failedScans = [];
    private string? _lastKey;
    private Task _startup = Task.CompletedTask;
    private int _lastPending;

    /// <summary>The background startup; completes when the first scans, identification and refresh are done.</summary>
    public Task Startup => _startup;

    /// <summary>Call once, from the UI thread, after the window was created.</summary>
    public void Start()
    {
        // The hub starts out "key missing" and "offline"; show what is known now instead of waiting for TMDB.
        SyncKeyState(settings.Load().TmdbKey);
        hub.SetOnline(connectivity.IsOnline);

        settings.Changed += OnSettingsChanged;
        hub.StatusChanged += OnStatusChanged;
        watcher.Rescanned += OnRescanned;

        _startup = Task.Run(() => RunStartupAsync(_stopping.Token));
    }

    /// <summary>
    /// Stops the background work in reverse order of <see cref="Start"/>: the open player first (it saves the
    /// position), then startup, watcher, identification and connectivity. The watcher can take up to 30 s, so the
    /// caller bounds the wait. The HTTP client is left to the service provider.
    /// </summary>
    public async Task StopAsync()
    {
        settings.Changed -= OnSettingsChanged;
        hub.StatusChanged -= OnStatusChanged;
        watcher.Rescanned -= OnRescanned;
        await _stopping.CancelAsync().ConfigureAwait(false);

        await playback.DisposeAsync().ConfigureAwait(false);
        await _startup.ConfigureAwait(false); // never throws: RunStartupAsync catches everything
        watcher.Dispose();
        identification.Dispose();
        connectivity.Dispose();
    }

    private async Task RunStartupAsync(CancellationToken ct)
    {
        try
        {
            if (!TryOpenDatabase()) return; // nothing else can work without it
            library.NotifyChanged(); // the pages can read it now

            connectivity.Start();
            identification.Start();

            foreach (var folder in folders.All().Where(f => f.Enabled))
            {
                ct.ThrowIfCancellationRequested();
                if (await ScanAsync(folder, ct).ConfigureAwait(false)) _ = IdentifyAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            watcher.Start();

            // A run started by identification.Start() is not tied to ct, so stop waiting for it on close.
            await identification.WhenIdleAsync().WaitAsync(ct).ConfigureAwait(false);
            await refresher.RefreshStaleAsync(ct).ConfigureAwait(false);
            library.NotifyChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Closing.
        }
        catch (Exception e)
        {
            hub.Log(ActivityLevel.Error, $"Library update stopped: {e.Message}", "Restart MediaBoard to try again.");
        }
    }

    // Creates or upgrades the database here rather than on the UI thread. A locked or corrupt library.db, or a
    // data folder that can't be written, ends startup: the status bar and Home say so and the activity panel opens.
    private bool TryOpenDatabase()
    {
        try
        {
            db.Open().Dispose();
            hub.SetLibraryError(null);
            return true;
        }
        catch (Exception e)
        {
            var hint = $"Make sure no other program is using {db.FilePath} and that you can write to it, then restart MediaBoard.";
            hub.Log(ActivityLevel.Error, $"Can't open the library: {e.Message}", hint);
            hub.SetLibraryError(hint);
            return false;
        }
    }

    /// <summary>
    /// Scans one folder now, records the result, then identifies what the scan found and tells the pages. For a
    /// folder the user just added, enabled or re-pointed. A failed scan is logged and shown on the status bar, not
    /// thrown. Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> or the app closing cancels it.
    /// </summary>
    public async Task ScanFolderAsync(LibraryFolder folder, CancellationToken ct)
    {
        bool scanned;
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token))
            scanned = await ScanAsync(folder, linked.Token).ConfigureAwait(false);

        // Identification covers every folder, so only closing the app cancels it, not this folder's token.
        if (scanned) await IdentifyAsync(_stopping.Token).ConfigureAwait(false);
    }

    /// <summary>Forgets a removed or switched-off folder's failed scan and unavailable state, so the status bar stops naming it.</summary>
    public void ForgetFolder(int folderId)
    {
        SetScanFailed(folderId, null);
        scanner.Forget(folderId);
    }

    /// <returns>True when the scan worked.</returns>
    private async Task<bool> ScanAsync(LibraryFolder folder, CancellationToken ct)
    {
        try
        {
            var result = await scanner.ScanAsync(folder, ct).ConfigureAwait(false);
            library.RecordScan(folder.Id, result);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            hub.Log(ActivityLevel.Error, $"Couldn't scan {folder.Path}: {e.Message}", "Restart MediaBoard to scan it again.");
            SetScanFailed(folder.Id, FolderName(folder.Path));
            return false;
        }

        SetScanFailed(folder.Id, null);
        return true;
    }

    /// <summary>Records that a folder's last scan threw (a name) or worked (null), and publishes the failed names.</summary>
    private void SetScanFailed(int folderId, string? name)
    {
        string[] names;
        lock (_failedScans)
        {
            var changed = name is null ? _failedScans.Remove(folderId) : _failedScans.TryAdd(folderId, name);
            if (!changed) return;
            names = _failedScans.Values.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        hub.SetFailedScans(names);
    }

    private static string FolderName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name; // a drive root has no name of its own
    }

    private async Task IdentifyAsync(CancellationToken ct)
    {
        await identification.RunPendingAsync(ct).ConfigureAwait(false);
        library.NotifyChanged();
    }

    // A watcher rescan only reports the folder id, so there is no result for RecordScan. The watcher raises this
    // only after a scan that worked; a failed rescan is logged by the watcher itself.
    private void OnRescanned(int folderId)
    {
        SetScanFailed(folderId, null);
        library.NotifyChanged();
        _ = IdentifyAsync(_stopping.Token);
    }

    // Identification also runs by itself when the connection returns or the key changes. When its queue empties,
    // the library has new matches to show.
    private void OnStatusChanged(StatusSnapshot status)
    {
        var previous = Interlocked.Exchange(ref _lastPending, status.PendingIdentification);
        if (previous > 0 && status.PendingIdentification == 0) library.NotifyChanged();
    }

    private void OnSettingsChanged(AppSettings changed) => SyncKeyState(changed.TmdbKey);

    // A new key is assumed to work until TMDB says otherwise; TmdbClient reports Rejected on its first 401.
    private void SyncKeyState(string? key)
    {
        key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        lock (_keyGate)
        {
            if (key is null) hub.SetKeyState(KeyState.Missing);
            else if (key != _lastKey || hub.Current.KeyState == KeyState.Missing) hub.SetKeyState(KeyState.Ok);
            _lastKey = key;
        }
    }
}
