using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>A page the sidebar opens.</summary>
public abstract class PageViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>
    /// Re-reads the library after it changed. Called on the UI thread; it must return at once, so pages load through
    /// a <see cref="BackgroundQuery"/>. The task completes when the new data is applied.
    /// </summary>
    public virtual Task RefreshAsync() => Task.CompletedTask;
}

/// <summary>A page later tasks fill in; until then it only says why it is empty.</summary>
public sealed class PlaceholderPageViewModel(string title, string emptyText) : PageViewModel(title)
{
    public string EmptyText { get; } = emptyText;
}

/// <summary>
/// Home: the "Continue watching" and "Recently added" rows, a prompt to add a folder when there is none, or why the
/// library can't be opened. Nothing shows until the first load, which runs once the database is open.
/// </summary>
public sealed partial class HomeViewModel : PageViewModel
{
    public const int RowLimit = 20;

    private readonly ILibrary _library;
    private readonly Action _addFolder;
    private readonly BackgroundQuery _query;

    public HomeViewModel(ILibrary library, ActivityHub hub, Action addFolder)
        : base("Home")
    {
        _library = library;
        _addFolder = addFolder;
        _query = new BackgroundQuery(hub, "Home");
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFolders), nameof(ShowRows))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFolders), nameof(ShowRows))]
    public partial bool HasNoFolders { get; private set; }

    /// <summary>What to do because the library could not be opened; set by the shell. Null when it opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError), nameof(ShowNoFolders), nameof(ShowRows))]
    public partial string? LibraryError { get; set; }

    public bool ShowError => LibraryError is not null;

    public bool ShowNoFolders => !ShowError && IsLoaded && HasNoFolders;

    public bool ShowRows => !ShowError && IsLoaded && !HasNoFolders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NothingInProgress))]
    public partial IReadOnlyList<ContinueItem> ContinueWatching { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NothingAdded))]
    public partial IReadOnlyList<TitleCard> RecentlyAdded { get; private set; } = [];

    public bool NothingInProgress => ContinueWatching.Count == 0;

    public bool NothingAdded => RecentlyAdded.Count == 0;

    [RelayCommand]
    private void AddFolder() => _addFolder();

    public override Task RefreshAsync() => _query.RunAsync(Load, data =>
    {
        HasNoFolders = data.NoFolders;
        ContinueWatching = data.ContinueWatching;
        RecentlyAdded = data.RecentlyAdded;
        IsLoaded = true;
    });

    // Thread pool. Folders() checks each folder's path, which can take seconds on a sleeping or network drive.
    private HomeData Load()
    {
        if (_library.Folders().Count == 0) return new HomeData(true, [], []);
        return new HomeData(false, _library.ContinueWatching(RowLimit), _library.RecentlyAdded(RowLimit));
    }

    private sealed record HomeData(bool NoFolders, IReadOnlyList<ContinueItem> ContinueWatching, IReadOnlyList<TitleCard> RecentlyAdded);
}
