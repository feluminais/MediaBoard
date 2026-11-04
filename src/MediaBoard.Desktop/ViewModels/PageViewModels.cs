using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>A page the sidebar opens.</summary>
public abstract class PageViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>Where the page's list was scrolled to (pixels from the top), kept for the next time its view is built.</summary>
    public double ScrollOffset { get; set; }

    /// <summary>
    /// Re-reads the library after it changed. Called on the UI thread; it must return at once, so pages load through
    /// a <see cref="BackgroundQuery"/>. The task completes when the new data is applied.
    /// </summary>
    public virtual Task RefreshAsync() => Task.CompletedTask;

    /// <summary>The sidebar just opened this page. Pages whose data can change without the library saying so (a drive plugged in, a player installed) reload here.</summary>
    public virtual void Opened()
    {
    }

    /// <summary>Another page replaced this one (it may come back through <see cref="Opened"/> or a refresh). Stop work only this page needs.</summary>
    public virtual void Closed()
    {
    }

    /// <summary>
    /// Whether a library change reloads the page while another page is open. A page that reloads in <see cref="Opened"/>
    /// anyway and is costly to load (a grid of thousands) says no.
    /// </summary>
    public virtual bool RefreshesWhileHidden => true;
}

/// <summary>What the poster pages can ask the shell to do. All run on the UI thread and return at once.</summary>
/// <param name="OpenTitle">Opens the title page.</param>
/// <param name="PlayFile">Resumes a file in the player.</param>
/// <param name="OpenList">Opens the full list behind a Home row.</param>
public sealed record PageActions(Action<int> OpenTitle, Action<int> PlayFile, Action<ListKind> OpenList);

/// <summary>
/// Home: the "Continue watching" and "Recently added" rows, the first-run prompts when there is no folder ("Add a
/// folder", and "Add TMDB key" while there is no key), or why the library can't be opened. Nothing shows until the first load, which runs once the database is open.
/// </summary>
public sealed partial class HomeViewModel : PageViewModel
{
    public const int RowLimit = 20;

    private readonly ILibrary _library;
    private readonly PosterLoader _posters;
    private readonly PageActions _actions;
    private readonly Action _addFolder;
    private readonly Action _addKey;
    private readonly BackgroundQuery _query;
    private IReadOnlyDictionary<string, PosterCardViewModel> _known = new Dictionary<string, PosterCardViewModel>();

    public HomeViewModel(ILibrary library, ActivityHub hub, PosterLoader posters, PageActions actions, Action addFolder, Action addKey)
        : base("Home")
    {
        _library = library;
        _posters = posters;
        _actions = actions;
        _addFolder = addFolder;
        _addKey = addKey;
        _query = new BackgroundQuery(hub, "Home");
        SyncStatus(hub.Current);
        _ = new StatusFeed(hub, SyncStatus);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFolders), nameof(ShowRows), nameof(ShowKeyPrompt))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFolders), nameof(ShowRows), nameof(ShowKeyPrompt))]
    public partial bool HasNoFolders { get; private set; }

    /// <summary>What to do because the library could not be opened; set by the shell. Null when it opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError), nameof(ShowNoFolders), nameof(ShowRows), nameof(ShowKeyPrompt))]
    public partial string? LibraryError { get; set; }

    /// <summary>No TMDB key is saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowKeyPrompt))]
    public partial bool HasNoKey { get; private set; }

    public bool ShowError => LibraryError is not null;

    public bool ShowNoFolders => !ShowError && IsLoaded && HasNoFolders;

    /// <summary>First run: no folder and no key. The key prompt goes away once a key is saved.</summary>
    public bool ShowKeyPrompt => ShowNoFolders && HasNoKey;

    public bool ShowRows => !ShowError && IsLoaded && !HasNoFolders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NothingInProgress))]
    public partial IReadOnlyList<PosterCardViewModel> ContinueWatching { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NothingAdded), nameof(ShowNothingAdded), nameof(ShowBusy))]
    public partial IReadOnlyList<PosterCardViewModel> RecentlyAdded { get; private set; } = [];

    /// <summary>"Scanning folders…" or "Identifying 3 files…" while the library is filling; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNothingAdded), nameof(ShowBusy))]
    public partial string? BusyTitle { get; private set; }

    public string BusyLine => LibraryBusy.Line;

    public bool NothingInProgress => ContinueWatching.Count == 0;

    public bool NothingAdded => RecentlyAdded.Count == 0;

    /// <summary>"Recently added" is empty and nothing is on its way.</summary>
    public bool ShowNothingAdded => NothingAdded && BusyTitle is null;

    /// <summary>"Recently added" is empty because the scan or identification has not got there yet.</summary>
    public bool ShowBusy => NothingAdded && BusyTitle is not null;

    [RelayCommand]
    private void AddFolder() => _addFolder();

    [RelayCommand]
    private void AddKey() => _addKey();

    /// <summary>The arrow next to "Continue watching": every in-progress item.</summary>
    [RelayCommand]
    private void OpenContinueAll() => _actions.OpenList(ListKind.ContinueWatching);

    /// <summary>The arrow next to "Recently added": the whole library, newest first.</summary>
    [RelayCommand]
    private void OpenRecentAll() => _actions.OpenList(ListKind.RecentlyAdded);

    private void SyncStatus(StatusSnapshot status)
    {
        HasNoKey = status.KeyState == KeyState.Missing;
        BusyTitle = LibraryBusy.Title(status);
    }

    // Before the first load the database may not be open yet; that load comes with the first library change.
    public override void Opened()
    {
        if (IsLoaded) _ = RefreshAsync();
    }

    public override Task RefreshAsync()
    {
        var known = _known;
        return _query.RunAsync(() => Load(known), data =>
        {
            HasNoFolders = data.NoFolders;
            if (!PosterCards.SameCards(ContinueWatching, data.ContinueWatching)) ContinueWatching = data.ContinueWatching;
            if (!PosterCards.SameCards(RecentlyAdded, data.RecentlyAdded)) RecentlyAdded = data.RecentlyAdded;
            _known = PosterCards.Index(data.ContinueWatching.Concat(data.RecentlyAdded));
            IsLoaded = true;
        });
    }

    // Thread pool. Folders() checks each folder's path, which can take seconds on a sleeping or network drive.
    private HomeData Load(IReadOnlyDictionary<string, PosterCardViewModel> known)
    {
        if (_library.Folders().Count == 0) return new HomeData(true, [], []);

        var playing = PosterCards.Build(_library.ContinueWatching(RowLimit), known,
            item => PosterCardViewModel.For(item, _posters, card => _actions.PlayFile(card.FileId!.Value),
                card => _library.DismissContinue(card.TitleId), card => _actions.OpenTitle(card.TitleId)));
        var added = PosterCards.Build(_library.RecentlyAdded(RowLimit), known,
            card => PosterCardViewModel.For(card, _posters, c => _actions.OpenTitle(c.TitleId)));
        return new HomeData(false, playing, added);
    }

    private sealed record HomeData(bool NoFolders, IReadOnlyList<PosterCardViewModel> ContinueWatching, IReadOnlyList<PosterCardViewModel> RecentlyAdded);
}
