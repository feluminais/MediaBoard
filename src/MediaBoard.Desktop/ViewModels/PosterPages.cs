using CommunityToolkit.Mvvm.ComponentModel;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>The full lists the Home rows open.</summary>
public enum ListKind { ContinueWatching, RecentlyAdded }

/// <summary>
/// The list behind a Home row's arrow: every in-progress item, or the whole library newest first. Rows are virtualized,
/// so a library of thousands costs one screenful of controls.
/// </summary>
public sealed partial class ListViewModel : PageViewModel, IServerPage
{
    private readonly ListKind _kind;
    private readonly ILibrary _library;
    private readonly PosterLoader _posters;
    private readonly PageActions _actions;
    private readonly BackgroundQuery _query;
    private readonly ActivityHub _hub;
    private IReadOnlyDictionary<string, PosterCardViewModel> _known = new Dictionary<string, PosterCardViewModel>();
    private StatusFeed? _feed;

    public ListViewModel(ListKind kind, ILibrary library, ActivityHub hub, PosterLoader posters, PageActions actions, AppCapabilities? capabilities = null)
        : base(kind == ListKind.ContinueWatching ? "Continue watching" : "Recently added")
    {
        _kind = kind;
        _library = library;
        _posters = posters;
        _actions = actions;
        _hub = hub;
        _query = new BackgroundQuery(hub, Title, NoteLoadFailed, capabilities);
    }

    public ListKind Kind => _kind;

    public string EmptyText => _kind == ListKind.ContinueWatching ? "Nothing in progress" : BusyTitle ?? "Nothing added yet";

    /// <summary>"Titles appear as they're found." under <see cref="EmptyText"/> while the library is filling; empty otherwise.</summary>
    public string EmptyHint => BusyTitle is null ? "" : LibraryBusy.Line;

    public bool HasEmptyHint => EmptyHint.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText), nameof(EmptyHint), nameof(HasEmptyHint))]
    private partial string? BusyTitle { get; set; }

    /// <summary>The rows. A refresh that adds or removes a few changes the list row by row, so the focused row stays.</summary>
    public BulkObservableCollection<PosterCardViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoaded { get; private set; }

    public bool ShowEmpty => IsLoaded && Items.Count == 0;

    // The shell opens a list (and brings one back from the back stack) through RefreshAsync.
    public override Task RefreshAsync()
    {
        FollowScans();
        var known = _known;
        return _query.RunAsync(() => Load(known), cards =>
        {
            if (!PosterCards.SameCards(Items, cards))
            {
                CollectionUpdate.Apply(Items, cards);
                OnPropertyChanged(nameof(ShowEmpty));
            }

            _known = PosterCards.Index(cards);
            IsLoaded = true;
        });
    }

    // A new list is made for each visit, and the hub lives as long as the app: leaving lets go of it.
    public override void Closed()
    {
        _feed?.Dispose();
        _feed = null;
    }

    // A scan only ever adds to the newest-first list; nothing it finds is in progress yet.
    private void FollowScans()
    {
        if (_kind != ListKind.RecentlyAdded || _feed is not null) return;
        BusyTitle = LibraryBusy.Title(_hub.Current);
        _feed = new StatusFeed(_hub, status => BusyTitle = LibraryBusy.Title(status));
    }

    // Thread pool. int.MaxValue asks for everything.
    private IReadOnlyList<PosterCardViewModel> Load(IReadOnlyDictionary<string, PosterCardViewModel> known) =>
        _kind == ListKind.ContinueWatching
            ? PosterCards.Build(_library.ContinueWatching(int.MaxValue), known,
                item => PosterCardViewModel.For(item, _posters, card => _actions.PlayFile(card.FileId!.Value),
                    card => PosterCards.Dismiss(_library, _hub, card.TitleId), card => _actions.OpenTitle(card.TitleId)))
            : PosterCards.Build(_library.RecentlyAdded(int.MaxValue), known,
                card => PosterCardViewModel.For(card, _posters, c => _actions.OpenTitle(c.TitleId), withDate: true));
}

/// <summary>A sort the grid offers.</summary>
public sealed record SortOption(TitleSort Sort, string Label);

/// <summary>
/// The Movies or Shows page: a poster grid with a sort, a text filter and a "Show hidden" switch. The library sorts
/// and hides; the text filter runs in memory on the loaded titles, so typing never queries the database.
/// </summary>
public sealed partial class TitleGridViewModel : PageViewModel, IServerPage
{
    private static readonly SortOption[] Sorts =
    [
        new SortOption(TitleSort.Name, "Name"),
        new SortOption(TitleSort.Year, "Year"),
        new SortOption(TitleSort.DateAdded, "Date added"),
    ];

    private readonly TitleKind _kind;
    private readonly ILibrary _library;
    private readonly PosterLoader _posters;
    private readonly PageActions _actions;
    private readonly BackgroundQuery _query;
    private readonly AppCapabilities _capabilities;
    private IReadOnlyList<PosterCardViewModel> _all = [];
    private IReadOnlyDictionary<string, PosterCardViewModel> _known = new Dictionary<string, PosterCardViewModel>();

    public TitleGridViewModel(TitleKind kind, ILibrary library, ActivityHub hub, PosterLoader posters, PageActions actions, AppCapabilities? capabilities = null)
        : base(kind == TitleKind.Movie ? "Movies" : "Shows")
    {
        _kind = kind;
        _library = library;
        _posters = posters;
        _actions = actions;
        _capabilities = capabilities ?? AppCapabilities.Local;
        _query = new BackgroundQuery(hub, Title, NoteLoadFailed, _capabilities);
        BusyTitle = LibraryBusy.Title(hub.Current);
        _ = new StatusFeed(hub, status => BusyTitle = LibraryBusy.Title(status));
    }

    public IReadOnlyList<SortOption> SortOptions => Sorts;

    /// <summary>Hidden titles never leave the owner's PC, so a viewer has nothing to switch on.</summary>
    public bool CanShowHidden => _capabilities.CanEditLibrary;

    [ObservableProperty]
    public partial SortOption SelectedSort { get; set; } = Sorts[0];

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    /// <summary>Also list the titles the user hid; they are drawn faded.</summary>
    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    /// <summary>
    /// The cards to show: all of them, or those that match the filter. A refresh that adds or removes a few changes the
    /// list card by card, so the focused card stays; another sort or a typed filter replaces it in one change.
    /// </summary>
    public BulkObservableCollection<PosterCardViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(EmptyTitle), nameof(EmptyHint), nameof(HasEmptyHint))]
    public partial bool IsLoaded { get; private set; }

    public bool ShowEmpty => IsLoaded && Items.Count == 0;

    public string EmptyTitle => _all.Count > 0 ? "Nothing matches"
        : BusyTitle ?? (_kind == TitleKind.Movie ? "No movies yet" : "No shows yet");

    public string EmptyHint => _all.Count > 0 ? ""
        : BusyTitle is not null ? LibraryBusy.Line
        : !_capabilities.ManagesFolders ? ""
        : _kind == TitleKind.Movie ? "Add a folder that has movies." : "Add a folder that has shows.";

    public bool HasEmptyHint => EmptyHint.Length > 0;

    /// <summary>"Scanning folders…" or "Identifying 3 files…" while the library is filling; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyTitle), nameof(EmptyHint), nameof(HasEmptyHint))]
    private partial string? BusyTitle { get; set; }

    public override bool RefreshesWhileHidden => false;

    public override void Opened() => _ = RefreshAsync();

    public override Task RefreshAsync()
    {
        var sort = SelectedSort.Sort;
        var includeHidden = ShowHidden;
        var known = _known;
        return _query.RunAsync(() => Load(sort, includeHidden, known), cards =>
        {
            _known = PosterCards.Index(cards);
            if (!IsLoaded || !PosterCards.SameCards(_all, cards))
            {
                _all = cards;
                ApplyFilter();
            }

            IsLoaded = true;
        });
    }

    partial void OnSelectedSortChanged(SortOption value) => _ = RefreshAsync();

    partial void OnShowHiddenChanged(bool value) => _ = RefreshAsync();

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    // Thread pool.
    private IReadOnlyList<PosterCardViewModel> Load(TitleSort sort, bool includeHidden, IReadOnlyDictionary<string, PosterCardViewModel> known) =>
        PosterCards.Build(_library.Titles(_kind, sort, includeHidden), known,
            card => PosterCardViewModel.For(card, _posters, c => _actions.OpenTitle(c.TitleId)));

    private void ApplyFilter()
    {
        var text = (FilterText ?? "").Trim();
        var shown = text.Length == 0
            ? _all
            : _all.Where(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!PosterCards.SameCards(Items, shown)) CollectionUpdate.Apply(Items, shown);
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyTitle)); // _all may have changed without Items changing
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(HasEmptyHint));
    }
}
