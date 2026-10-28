using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>A top-level folder a scan skipped because no video is below it.</summary>
public sealed record IgnoredFolder(string Name, string Label);

/// <summary>One library folder as the Folders page shows it.</summary>
public sealed class FolderItem
{
    public const string IgnoredLabel = "Ignored: no video";

    /// <param name="scanFailed">The folder's last scan threw (the status bar says so too).</param>
    public FolderItem(FolderRow row, bool scanFailed = false)
    {
        Id = row.Id;
        Path = row.Path;
        IsEnabled = row.Enabled;
        IsUnavailable = row.Unavailable;
        Name = NameOf(row.Path);
        Ignored = row.IgnoredFolders.Select(name => new IgnoredFolder(name, IgnoredLabel)).ToList();
        Summary = row.Unavailable
            ? "Unavailable. Reconnect the drive, or locate the folder."
            : scanFailed ? "Scan failed"
            : !row.Enabled ? "Disabled"
            : row.FileCount switch { 0 => "No videos found", 1 => "1 file", var n => $"{n} files" };
    }

    public int Id { get; }

    public string Path { get; }

    /// <summary>The last part of the path, for the remove question.</summary>
    public string Name { get; }

    public bool IsEnabled { get; }

    public bool IsUnavailable { get; }

    /// <summary>A folder that can't be read offers to be pointed at its new location.</summary>
    public bool ShowRepoint => IsUnavailable;

    public string ToggleLabel => IsEnabled ? "Disable" : "Enable";

    public string Summary { get; }

    public IReadOnlyList<IgnoredFolder> Ignored { get; }

    public bool HasIgnored => Ignored.Count > 0;

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
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial IReadOnlyList<FolderItem> Folders { get; private set; } = [];

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

    public override Task RefreshAsync() => _query.RunAsync(_library.Folders, rows =>
    {
        var failed = _hub.Current.FailedScans;
        // The status bar lists failed scans by folder name, so match on that.
        Folders = rows.Select(row => new FolderItem(row, failed.Contains(FolderItem.NameOf(row.Path), StringComparer.OrdinalIgnoreCase))).ToList();
        IsLoaded = true;
    });

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
