using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>A top-level folder a scan skipped because no video is below it.</summary>
public sealed record IgnoredFolder(string Name, string Label);

/// <summary>
/// One library folder as the Folders page shows it. The row comes from the library; the scan state comes from the hub
/// and changes in place while a scan runs (<see cref="SetScan"/>), so the row's progress bar is not rebuilt.
/// </summary>
public sealed partial class FolderItem : ObservableObject
{
    public const string IgnoredLabel = "Ignored: no video";

    private string _idleSummary = "";

    /// <param name="scanFailed">The folder's last scan threw (the status bar says so too).</param>
    /// <param name="scan">Where the folder's scan is; null when none runs or waits.</param>
    public FolderItem(FolderRow row, bool scanFailed = false, FolderScan? scan = null)
    {
        Id = row.Id;
        Path = "";
        Name = "";
        Ignored = [];
        Fill(row, scanFailed);
        SetScan(scan);
    }

    /// <summary>
    /// Takes what the library says now. The row stays the same object, so the list keeps its controls and the
    /// button the viewer has focused is still there after the reload.
    /// </summary>
    internal void Refresh(FolderRow row, bool scanFailed, FolderScan? scan)
    {
        Fill(row, scanFailed);
        SetScan(scan);
        foreach (var name in Changeable) OnPropertyChanged(name);
    }

    private static readonly string[] Changeable =
    [
        nameof(Path), nameof(Name), nameof(IsEnabled), nameof(IsUnavailable), nameof(ShowRepoint), nameof(ToggleLabel),
        nameof(Ignored), nameof(HasIgnored),
    ];

    private void Fill(FolderRow row, bool scanFailed)
    {
        Path = row.Path;
        IsEnabled = row.Enabled;
        IsUnavailable = row.Unavailable;
        Name = NameOf(row.Path);
        Ignored = row.IgnoredFolders.Select(name => new IgnoredFolder(name, IgnoredLabel)).ToList();
        _idleSummary = row.Unavailable
            ? "Unavailable. Reconnect the drive, or locate the folder."
            : scanFailed ? "Scan failed"
            : !row.Enabled ? "Disabled"
            : row.FileCount switch { 0 => "No videos found", 1 => "1 file", var n => $"{n} files" };
    }

    public int Id { get; }

    public string Path { get; private set; }

    /// <summary>The last part of the path, for the remove question.</summary>
    public string Name { get; private set; }

    public bool IsEnabled { get; private set; }

    public bool IsUnavailable { get; private set; }

    /// <summary>A folder that can't be read offers to be pointed at its new location.</summary>
    public bool ShowRepoint => IsUnavailable;

    public string ToggleLabel => IsEnabled ? "Disable" : "Enable";

    /// <summary>"Scanning… 120 of 810 files", "Waiting to scan", "No videos found", "12 files"…</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    /// <summary>The scan state shown, or null when the row shows its counts.</summary>
    public FolderScan? Scan { get; private set; }

    /// <summary>A scan of this folder is running: a thin progress bar sits under the summary.</summary>
    [ObservableProperty]
    public partial bool IsScanning { get; private set; }

    /// <summary>The scan is still listing the folder, so how far it is can't be told yet.</summary>
    [ObservableProperty]
    public partial bool IsScanIndeterminate { get; private set; }

    /// <summary>0 to 100 while a scan with a known total runs.</summary>
    [ObservableProperty]
    public partial double ScanPercent { get; private set; }

    public IReadOnlyList<IgnoredFolder> Ignored { get; private set; }

    public bool HasIgnored => Ignored.Count > 0;

    /// <summary>Shows a scan that runs or waits, or (null) the folder's counts again.</summary>
    public void SetScan(FolderScan? scan)
    {
        // A folder switched off while still in line for a startup scan is skipped when its turn comes.
        if (!IsEnabled && scan?.State == FolderScanState.Waiting) scan = null;
        Scan = scan;
        Summary = scan switch
        {
            null => _idleSummary,
            { State: FolderScanState.Waiting } => "Waiting to scan",
            { Total: > 0 } => string.Create(CultureInfo.InvariantCulture, $"Scanning… {scan.Done} of {scan.Total} files"),
            _ => "Scanning…",
        };
        IsScanning = scan?.State == FolderScanState.Scanning;
        IsScanIndeterminate = IsScanning && scan!.Total <= 0;
        ScanPercent = IsScanning && !IsScanIndeterminate ? Math.Clamp(scan!.Done * 100.0 / scan.Total, 0, 100) : 0;
    }

    /// <summary>The last part of a path; a drive root has no name of its own, so it is the path.</summary>
    public static string NameOf(string path)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}

/// <summary>
/// Folders: the library folders with their state, and add, disable, remove and re-point. The folder picker and the
/// remove question are passed in so tests (and the real dialogs) can stand in for them. Commands never throw: a
/// refusal or a failure shows as <see cref="Message"/>.
/// </summary>
public sealed partial class FoldersViewModel : PageViewModel
{
    private readonly ILibrary _library;
    private readonly IFolderActions _actions;
    private readonly ActivityHub _hub;
    private readonly Func<Task<string?>> _pickFolder;
    private readonly Func<string, Task<bool>> _confirm;
    private readonly BackgroundQuery _query;

    public FoldersViewModel(
        ILibrary library,
        IFolderActions actions,
        ActivityHub hub,
        Func<Task<string?>> pickFolder,
        Func<string, Task<bool>> confirm)
        : base("Folders")
    {
        _library = library;
        _actions = actions;
        _hub = hub;
        _pickFolder = pickFolder;
        _confirm = confirm;
        _query = new BackgroundQuery(hub, "the folders");
        _ = new StatusFeed(hub, ApplyScans);
    }

    /// <summary>The rows. A reload keeps the row objects it can (<see cref="FolderItem.Refresh"/>), so focus on a row's button stays.</summary>
    public ObservableCollection<FolderItem> Folders { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>Why the last action did nothing ("That folder is already in the library."); null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    public bool HasMessage => Message is not null;

    public bool ShowEmpty => IsLoaded && Folders.Count == 0;

    public override void Opened()
    {
        Message = null;
        _ = RefreshAsync();
    }

    public override Task RefreshAsync()
    {
        // Folders that are busy now may have their result recorded before the rows are read, or after.
        var busy = _hub.Current;
        return _query.RunAsync(_library.Folders, rows =>
        {
            var status = _hub.Current;
            var shown = Folders.ToDictionary(f => f.Id);
            var items = rows.Select(row =>
            {
                var failed = status.FailedScanIds.Contains(row.Id);
                var scan = status.ScanOf(row.Id) ?? HeldScan(row.Id, busy, shown);
                if (!shown.TryGetValue(row.Id, out var item)) return new FolderItem(row, failed, scan);

                item.Refresh(row, failed, scan);
                return item;
            }).ToList();
            CollectionSync.Sync(Folders, items);
            IsLoaded = true;
            OnPropertyChanged(nameof(ShowEmpty));
        });
    }

    // A folder that was busy when these rows were read and is idle now may come with counts from before its scan, and
    // for a folder just added that says "No videos found". Its row keeps the scan it showed; recording the scan's
    // result tells the library it changed, and the reload that follows reads the new counts with the folder idle.
    private static FolderScan? HeldScan(int folderId, StatusSnapshot busy, Dictionary<int, FolderItem> shown) =>
        busy.ScanOf(folderId) is { } then ? shown.GetValueOrDefault(folderId)?.Scan ?? then : null;

    // UI thread, while scans run. A row whose scan just ended keeps showing it until the reload after the recorded
    // result brings its new counts: reloading here would read the library before that result is in.
    private void ApplyScans(StatusSnapshot status)
    {
        foreach (var item in Folders)
        {
            var scan = status.ScanOf(item.Id);
            if (scan is not null || item.Scan is null) item.SetScan(scan);
        }
    }

    [RelayCommand]
    private Task AddFolderAsync() => ActAsync(async () =>
    {
        var path = await _pickFolder();
        return path is null ? FolderOutcome.Done : await _actions.AddAsync(path);
    });

    [RelayCommand]
    private Task RemoveAsync(FolderItem? item) => item is null ? Task.CompletedTask : ActAsync(async () =>
    {
        // Removing forgets the folder's rows, and with them its watch progress and overrides (FolderStore.Remove cascades).
        var question = $"Remove \"{item.Name}\" from the library? Your files aren't touched, but MediaBoard forgets its watch progress and " +
                       "your edits, such as renames and hidden items. To keep those, use Disable instead, or Locate… if the folder moved.";
        return await _confirm(question) ? await _actions.RemoveAsync(item.Id) : FolderOutcome.Done;
    });

    [RelayCommand]
    private Task ToggleEnabledAsync(FolderItem? item) =>
        item is null ? Task.CompletedTask : ActAsync(() => _actions.SetEnabledAsync(item.Id, !item.IsEnabled));

    [RelayCommand]
    private Task RepointAsync(FolderItem? item) => item is null ? Task.CompletedTask : ActAsync(async () =>
    {
        var path = await _pickFolder();
        return path is null ? FolderOutcome.Done : await _actions.RepointAsync(item.Id, path);
    });

    private async Task ActAsync(Func<Task<FolderOutcome>> action)
    {
        Message = null;
        try
        {
            Message = (await action()).Problem;
        }
        catch (Exception e)
        {
            _hub.Log(ActivityLevel.Error, $"Folder action failed: {e.Message}", BackgroundQuery.FailureHint);
            Message = "Something went wrong. See the activity panel.";
        }

        await RefreshAsync();
    }
}
