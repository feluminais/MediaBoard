using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Playback;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>A sidebar entry. <see cref="Badge"/> is a count shown beside the label; 0 hides it.</summary>
public sealed partial class NavItem(string key, string label) : ObservableObject
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial int Badge { get; set; }

    public bool HasBadge => Badge > 0;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// The shell: sidebar navigation, the current page, the search palette flag, the status bar and the activity panel.
/// The library raises <see cref="ILibrary.Changed"/> on whatever thread changed it; the shell posts one refresh
/// to the UI thread however many changes arrive before it runs, and that refresh queries on the thread pool.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public const string HomePage = "Home";
    public const string MoviesPage = "Movies";
    public const string ShowsPage = "Shows";
    public const string ReviewPage = "Review";
    public const string FoldersPage = "Folders";
    public const string SettingsPage = "Settings";

    private readonly ILibrary _library;
    private readonly ActivityHub _hub;
    private readonly PosterLoader _posters;
    private readonly TitleServices _titles;
    private readonly TitleNavigation _titleNavigation;
    private readonly Func<Task>? _rescanAll;
    private readonly Stack<PageViewModel> _back = [];
    private readonly PageActions _actions;
    private readonly HomeViewModel _home;
    private readonly BackgroundQuery _reviewCount;
    private readonly Dictionary<string, PageViewModel> _pages;
    private readonly NavItem _reviewItem;
    private int _refreshQueued;

    public MainWindowViewModel(
        ILibrary library,
        StatusBarViewModel statusBar,
        ActivityPanelViewModel activity,
        ActivityHub hub,
        FoldersViewModel folders,
        SettingsViewModel settings,
        PosterLoader posters,
        TitleServices titles,
        Func<Task>? rescanAll = null,
        TimeProvider? timeProvider = null)
    {
        _library = library;
        _hub = hub;
        _posters = posters;
        _titles = titles;
        _rescanAll = rescanAll;
        _titleNavigation = new TitleNavigation(GoBack, () => Navigate(SettingsPage), ReplaceTitle);
        _actions = new PageActions(OpenTitle, PlayFile, OpenList);
        _home = new HomeViewModel(
            library,
            hub,
            posters,
            _actions,
            addFolder: () =>
            {
                Navigate(FoldersPage);
                folders.AddFolderCommand.Execute(null); // the picker opens at once
            },
            addKey: () => Navigate(SettingsPage));
        _reviewCount = new BackgroundQuery(hub, "the review count");
        StatusBar = statusBar;
        Activity = activity;
        Search = new SearchPaletteViewModel(
            titles.Search,
            library,
            new PaletteActions(OpenTitle, OpenOnlineTitle, () => Navigate(SettingsPage), RescanAsync, ShowHiddenItems),
            hub,
            timeProvider ?? TimeProvider.System,
            () => IsSearchOpen = false);

        _pages = new Dictionary<string, PageViewModel>
        {
            [HomePage] = _home,
            [MoviesPage] = new TitleGridViewModel(TitleKind.Movie, library, hub, posters, _actions),
            [ShowsPage] = new TitleGridViewModel(TitleKind.Show, library, hub, posters, _actions),
            [ReviewPage] = new ReviewViewModel(titles),
            [FoldersPage] = folders,
            [SettingsPage] = settings,
        };
        _reviewItem = new NavItem(ReviewPage, "Needs review");
        NavItems =
        [
            new NavItem(HomePage, "Home"),
            new NavItem(MoviesPage, "Movies"),
            new NavItem(ShowsPage, "Shows"),
            _reviewItem,
            new NavItem(FoldersPage, "Folders"),
            new NavItem(SettingsPage, "Settings"),
        ];

        CurrentPage = _pages[HomePage];
        NavItems[0].IsSelected = true;

        statusBar.SettingsRequested += () => Navigate(SettingsPage);
        statusBar.PropertyChanged += OnStatusBarChanged;
        library.Changed += OnLibraryChanged;
    }

    public StatusBarViewModel StatusBar { get; }

    public ActivityPanelViewModel Activity { get; }

    /// <summary>The Ctrl+F palette; it empties each time it opens.</summary>
    public SearchPaletteViewModel Search { get; }

    /// <summary>Sidebar entries below Search, in order.</summary>
    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    public partial ObservableObject CurrentPage { get; private set; }

    [ObservableProperty]
    public partial int ReviewCount { get; private set; }

    /// <summary>The title whose page was opened last; null until one was.</summary>
    public int? OpenTitleId { get; private set; }

    /// <summary>The Ctrl+F palette is open.</summary>
    [ObservableProperty]
    public partial bool IsSearchOpen { get; set; }

    [ObservableProperty]
    public partial bool IsActivityOpen { get; set; }

    /// <summary>Opens a page by its key (<see cref="HomePage"/>, <see cref="MoviesPage"/>, ...).</summary>
    [RelayCommand]
    private void Navigate(string page)
    {
        if (!_pages.TryGetValue(page, out var target)) return;

        _back.Clear();
        CurrentPage = target;
        foreach (var item in NavItems) item.IsSelected = item.Key == page;
        target.Opened();
    }

    // The page that was open stays on a stack; Back on the title page returns to it.
    private void OpenTitle(int titleId)
    {
        if (CurrentPage is PageViewModel current) _back.Push(current);
        ShowTitle(titleId);
    }

    /// <summary>Opens the page of a TMDB title that is not in the library (an online search result).</summary>
    public void OpenOnlineTitle(int tmdbId, TitleKind kind)
    {
        if (CurrentPage is PageViewModel current) _back.Push(current);
        Show(TitleViewModel.Online(tmdbId, kind, _titles, _titleNavigation));
    }

    private void ReplaceTitle(int titleId) => ShowTitle(titleId);

    private void ShowTitle(int titleId)
    {
        OpenTitleId = titleId;
        Show(new TitleViewModel(titleId, _titles, _titleNavigation));
    }

    // The page loads once it is open and again whenever the library changes.
    private void Show(TitleViewModel page)
    {
        CurrentPage = page;
        _ = page.RefreshAsync();
    }

    private void GoBack()
    {
        if (!_back.TryPop(out var page))
        {
            Navigate(HomePage);
            return;
        }

        CurrentPage = page;

        // The grids and Home may have changed behind the title page (a hidden title, a renamed one).
        if (_pages.ContainsValue(page)) page.Opened();
        else _ = page.RefreshAsync();
    }

    // The player is started on the thread pool: finding the player and the file can touch a slow drive.
    private void PlayFile(int fileId) => _ = _titles.Launcher.PlayAsync(fileId);

    // A Home row's arrow. The list reads the library once it is open and again whenever the library changes.
    private void OpenList(ListKind kind)
    {
        var list = new ListViewModel(kind, _library, _hub, _posters, _actions);
        CurrentPage = list;
        _ = list.RefreshAsync();
    }

    // The palette's "Show hidden items": the grid on screen if it is one, otherwise Movies.
    private void ShowHiddenItems()
    {
        var grid = CurrentPage as TitleGridViewModel ?? (TitleGridViewModel)_pages[MoviesPage];
        grid.ShowHidden = true;
        if (!ReferenceEquals(grid, CurrentPage)) Navigate(MoviesPage);
    }

    // The palette's "Rescan library": one call on the thread pool; the scans report through the status bar.
    private async Task RescanAsync()
    {
        if (_rescanAll is not { } rescan) return;

        try
        {
            await Task.Run(rescan);
        }
        catch (Exception e)
        {
            _hub.Log(ActivityLevel.Error, $"Couldn't rescan the library: {e.Message}", BackgroundQuery.FailureHint);
        }
    }

    [RelayCommand]
    private void OpenSearch() => IsSearchOpen = true;

    [RelayCommand]
    private void CloseSearch() => IsSearchOpen = false;

    [RelayCommand]
    private void ToggleActivity() => IsActivityOpen = !IsActivityOpen;

    partial void OnIsSearchOpenChanged(bool value)
    {
        if (value) Search.Reset();
        else Search.Cancel();
    }

    partial void OnCurrentPageChanged(ObservableObject oldValue, ObservableObject newValue)
    {
        if (oldValue is PageViewModel closed && !ReferenceEquals(oldValue, newValue)) closed.Closed();
    }

    partial void OnReviewCountChanged(int value) => _reviewItem.Badge = value;

    private void OnLibraryChanged()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _refreshQueued, 0);
            Refresh();
        });
    }

    // Starts the loads and returns at once: every query runs on the thread pool (see BackgroundQuery).
    private void Refresh()
    {
        _ = _reviewCount.RunAsync(_library.NeedsReviewCount, count => ReviewCount = count);
        foreach (var page in _pages.Values)
        {
            if (page.RefreshesWhileHidden || ReferenceEquals(page, CurrentPage)) _ = page.RefreshAsync();
        }

        if (CurrentPage is PageViewModel open && !_pages.Values.Contains(open)) _ = open.RefreshAsync(); // a list or title page
    }

    // A library that can't be opened shows on Home and opens the activity panel, where the details are.
    private void OnStatusBarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StatusBarViewModel.LibraryErrorHint)) return;

        _home.LibraryError = StatusBar.LibraryErrorHint;
        if (StatusBar.LibraryErrorHint is not null) IsActivityOpen = true;
    }
}
