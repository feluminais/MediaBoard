using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;

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
        SettingsViewModel settings)
    {
        _library = library;
        _home = new HomeViewModel(
            library,
            hub,
            addFolder: () =>
            {
                Navigate(FoldersPage);
                folders.AddFolderCommand.Execute(null); // the picker opens at once
            },
            addKey: () => Navigate(SettingsPage));
        _reviewCount = new BackgroundQuery(hub, "the review count");
        StatusBar = statusBar;
        Activity = activity;

        _pages = new Dictionary<string, PageViewModel>
        {
            [HomePage] = _home,
            [MoviesPage] = new PlaceholderPageViewModel("Movies", "No movies yet"),
            [ShowsPage] = new PlaceholderPageViewModel("Shows", "No shows yet"),
            [ReviewPage] = new PlaceholderPageViewModel("Needs review", "Nothing to review"),
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

    /// <summary>Sidebar entries below Search, in order.</summary>
    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    public partial ObservableObject CurrentPage { get; private set; }

    [ObservableProperty]
    public partial int ReviewCount { get; private set; }

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

        CurrentPage = target;
        foreach (var item in NavItems) item.IsSelected = item.Key == page;
        target.Opened();
    }

    [RelayCommand]
    private void OpenSearch() => IsSearchOpen = true;

    [RelayCommand]
    private void CloseSearch() => IsSearchOpen = false;

    [RelayCommand]
    private void ToggleActivity() => IsActivityOpen = !IsActivityOpen;

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
        foreach (var page in _pages.Values) _ = page.RefreshAsync();
    }

    // A library that can't be opened shows on Home and opens the activity panel, where the details are.
    private void OnStatusBarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StatusBarViewModel.LibraryErrorHint)) return;

        _home.LibraryError = StatusBar.LibraryErrorHint;
        if (StatusBar.LibraryErrorHint is not null) IsActivityOpen = true;
    }
}
