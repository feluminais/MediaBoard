using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Storage;

namespace MediaBoard.Desktop;

/// <param name="Problem">Why nothing was done, in words for the user. Null when it worked.</param>
public sealed record FolderOutcome(string? Problem)
{
    public static FolderOutcome Done { get; } = new((string?)null);

    public bool Ok => Problem is null;

    public static FolderOutcome Refused(string problem) => new(problem);
}

/// <summary>
/// What the Folders page can do. Each call writes to the database and returns; scanning and identification carry on
/// in the background (<see cref="WhenIdleAsync"/> waits for them).
/// </summary>
public interface IFolderActions
{
    Task<FolderOutcome> AddAsync(string path);

    Task<FolderOutcome> RemoveAsync(int folderId);

    Task<FolderOutcome> SetEnabledAsync(int folderId, bool enabled);

    Task<FolderOutcome> RepointAsync(int folderId, string newPath);

    /// <summary>Completes when the scans and identification runs these calls started have finished.</summary>
    Task WhenIdleAsync();
}

/// <summary>
/// Adds, removes, disables and re-points library folders. The database write comes first and the library is told
/// at once, so the page shows the change; after that, off the UI thread, the folder is scanned (which records the
/// scan, runs identification and tells the library again) and the watcher is refreshed. Removing or switching a
/// folder off first stops its scan and its watcher, so nothing writes rows for a folder that is going away.
/// It never touches a folder on disk except to read it.
/// </summary>
public sealed class FolderActions(
    FolderStore folders,
    LibraryWatcher watcher,
    AppHost host,
    LocalLibrary library,
    ActivityHub hub) : IFolderActions
{
    private sealed record ScanRun(CancellationTokenSource Cancel, Task Task);

    private readonly object _gate = new();
    private readonly HashSet<Task> _inFlight = [];
    private readonly Dictionary<int, ScanRun> _scans = [];

    public Task<FolderOutcome> AddAsync(string path) => RunAsync("add the folder", () =>
    {
        var normalized = FolderStore.Normalize(path);
        if (!Directory.Exists(normalized)) return Task.FromResult(FolderOutcome.Refused("Can't find that folder."));
        if (Conflict(normalized, folders.All(), ignoreId: null) is { } problem) return Task.FromResult(FolderOutcome.Refused(problem));

        var folder = folders.Add(normalized);
        library.NotifyChanged();
        StartScan(folder);
        return Task.FromResult(FolderOutcome.Done);
    });

    public Task<FolderOutcome> RemoveAsync(int folderId) => RunAsync("remove the folder", async () =>
    {
        await StopScanAsync(folderId).ConfigureAwait(false);

        // Switch it off and let the watcher drop it before the rows go: a rescan of a folder being deleted would fail.
        folders.SetEnabled(folderId, false);
        watcher.Refresh();
        folders.Remove(folderId);
        host.ForgetFolder(folderId);
        library.NotifyChanged();
        return FolderOutcome.Done;
    });

    public Task<FolderOutcome> SetEnabledAsync(int folderId, bool enabled) => RunAsync(enabled ? "enable the folder" : "disable the folder", async () =>
    {
        var folder = folders.All().FirstOrDefault(f => f.Id == folderId);
        if (folder is null) return Gone;

        if (enabled)
        {
            folders.SetEnabled(folderId, true);
            library.NotifyChanged();
            StartScan(folder with { Enabled = true });
            return FolderOutcome.Done;
        }

        await StopScanAsync(folderId).ConfigureAwait(false);
        folders.SetEnabled(folderId, false);
        watcher.Refresh();
        host.ForgetFolder(folderId);
        library.NotifyChanged();
        return FolderOutcome.Done;
    });

    public Task<FolderOutcome> RepointAsync(int folderId, string newPath) => RunAsync("change the folder's location", async () =>
    {
        var all = folders.All();
        var folder = all.FirstOrDefault(f => f.Id == folderId);
        if (folder is null) return Gone;

        var normalized = FolderStore.Normalize(newPath);
        if (!Directory.Exists(normalized)) return FolderOutcome.Refused("Can't find that folder.");
        if (Conflict(normalized, all, ignoreId: folderId) is { } problem) return FolderOutcome.Refused(problem);

        await StopScanAsync(folderId).ConfigureAwait(false);
        folders.Repoint(folderId, normalized);
        library.NotifyChanged();
        var moved = folder with { Path = normalized };
        if (moved.Enabled) StartScan(moved);
        else watcher.Refresh();
        return FolderOutcome.Done;
    });

    public Task WhenIdleAsync()
    {
        Task[] running;
        lock (_gate) running = [.. _inFlight];
        return Task.WhenAll(running);
    }

    private static readonly FolderOutcome Gone = FolderOutcome.Refused("That folder is no longer in the library.");

    // The database work runs on the thread pool, and a failure reaches the page as a message, not an exception.
    private Task<FolderOutcome> RunAsync(string what, Func<Task<FolderOutcome>> work) => Task.Run(async () =>
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            hub.Log(ActivityLevel.Error, $"Couldn't {what}: {e.Message}", "Restart MediaBoard to try again.");
            return FolderOutcome.Refused($"Couldn't {what}. See the activity panel.");
        }
    });

    /// <summary>
    /// Scans the folder, then refreshes the watcher so it watches the folder (or its new location) from now on.
    /// A scan already running for the same folder is cancelled, and this one starts after it has stopped.
    /// </summary>
    private void StartScan(LibraryFolder folder)
    {
        lock (_gate)
        {
            _scans.TryGetValue(folder.Id, out var previous);
            previous?.Cancel.Cancel();

            var cancel = new CancellationTokenSource();
            var task = Task.Run(() => ScanThenWatchAsync(folder, previous?.Task, cancel.Token));
            _scans[folder.Id] = new ScanRun(cancel, task);
            Track(task);
        }
    }

    private async Task ScanThenWatchAsync(LibraryFolder folder, Task? previous, CancellationToken ct)
    {
        try
        {
            if (previous is not null) await previous.ConfigureAwait(false); // never throws: it catches its own
            await host.ScanFolderAsync(folder, ct).ConfigureAwait(false);
            watcher.Refresh();
        }
        catch (OperationCanceledException)
        {
            // Removed, switched off or scanned again: whoever cancelled it carries on from here.
        }
        catch (Exception e)
        {
            hub.Log(ActivityLevel.Error, $"Couldn't finish setting up {folder.Path}: {e.Message}", "Restart MediaBoard to try again.");
        }
    }

    private async Task StopScanAsync(int folderId)
    {
        ScanRun? run;
        lock (_gate) _scans.Remove(folderId, out run);
        if (run is null) return;

        await run.Cancel.CancelAsync().ConfigureAwait(false);
        await run.Task.ConfigureAwait(false);
    }

    private void Track(Task task)
    {
        _inFlight.Add(task); // the caller holds _gate
        _ = task.ContinueWith(
            finished =>
            {
                lock (_gate) _inFlight.Remove(finished);
            },
            TaskScheduler.Default);
    }

    private static string? Conflict(string path, IEnumerable<LibraryFolder> existing, int? ignoreId)
    {
        foreach (var other in existing)
        {
            if (other.Id == ignoreId) continue;

            var otherPath = FolderStore.Normalize(other.Path);
            if (string.Equals(otherPath, path, StringComparison.OrdinalIgnoreCase))
                return "That folder is already in the library.";
            if (IsInside(path, otherPath))
                return $"That folder is inside {otherPath}, which is already in the library.";
            if (IsInside(otherPath, path))
                return $"That folder contains {otherPath}, which is already in the library. Remove that folder first.";
        }

        return null;
    }

    private static bool IsInside(string child, string parent) =>
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
