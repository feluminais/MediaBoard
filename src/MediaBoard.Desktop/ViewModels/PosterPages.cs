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
public sealed partial class ListViewModel : PageViewModel
{
    private readonly ListKind _kind;
    private readonly ILibrary _library;
    private readonly PosterLoader _posters;
    private readonly PageActions _actions;
    private readonly BackgroundQuery _query;
    private IReadOnlyDictionary<string, PosterCardViewModel> _known = new Dictionary<string, PosterCardViewModel>();

    public ListViewModel(ListKind kind, ILibrary library, ActivityHub hub, PosterLoader posters, PageActions actions)
        : base(kind == ListKind.ContinueWatching ? "Continue watching" : "Recently added")
    {
        _kind = kind;
        _library = library;
        _posters = posters;
        _actions = actions;
        _query = new BackgroundQuery(hub, Title);
        EmptyText = kind == ListKind.ContinueWatching ? "Nothing in progress" : "Nothing added yet";
    }

    public ListKind Kind => _kind;

    public string EmptyText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial IReadOnlyList<PosterCardViewModel> Items { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoaded { get; private set; }

    public bool ShowEmpty => IsLoaded && Items.Count == 0;

    public override Task RefreshAsync()
    {
        var known = _known;
        return _query.RunAsync(() => Load(known), cards =>
        {
            if (!PosterCards.SameCards(Items, cards)) Items = cards;
            _known = PosterCards.Index(cards);
            IsLoaded = true;
        });
    }

    // Thread pool. int.MaxValue asks for everything.
    private IReadOnlyList<PosterCardViewModel> Load(IReadOnlyDictionary<string, PosterCardViewModel> known) =>
        _kind == ListKind.ContinueWatching
            ? PosterCards.Build(_library.ContinueWatching(int.MaxValue), known,
                item => PosterCardViewModel.For(item, _posters, card => _actions.PlayFile(card.FileId!.Value)))
            : PosterCards.Build(_library.RecentlyAdded(int.MaxValue), known,
                card => PosterCardViewModel.For(card, _posters, c => _actions.OpenTitle(c.TitleId), withDate: true));
}

/// <summary>A sort the grid offers.</summary>
public sealed record SortOption(TitleSort Sort, string Label);

/// <summary>
/// The Movies or Shows page: a poster grid with a sort, a text filter and a "Show hidden" switch. The library sorts
/// and hides; the text filter runs in memory on the loaded titles, so typing never queries the database.
/// </summary>
public sealed partial class TitleGridViewModel : PageViewModel
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
    private IReadOnlyList<PosterCardViewModel> _all = [];
    private IReadOnlyDictionary<string, PosterCardViewModel> _known = new Dictionary<string, PosterCardViewModel>();

    public TitleGridViewModel(TitleKind kind, ILibrary library, ActivityHub hub, PosterLoader posters, PageActions actions)
        : base(kind == TitleKind.Movie ? "Movies" : "Shows")
    {
        _kind = kind;
        _library = library;
        _posters = posters;
        _actions = actions;
        _query = new BackgroundQuery(hub, Title);
    }

    public IReadOnlyList<SortOption> SortOptions => Sorts;

    [ObservableProperty]
    public partial SortOption SelectedSort { get; set; } = Sorts[0];

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    /// <summary>Also list the titles the user hid; they are drawn faded.</summary>
    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    /// <summary>The cards to show: all of them, or those that match the filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(EmptyTitle), nameof(EmptyHint), nameof(HasEmptyHint))]
    public partial IReadOnlyList<PosterCardViewModel> Items { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(EmptyTitle), nameof(EmptyHint), nameof(HasEmptyHint))]
    public partial bool IsLoaded { get; private set; }

    public bool ShowEmpty => IsLoaded && Items.Count == 0;

    public string EmptyTitle => _all.Count > 0 ? "Nothing matches" : _kind == TitleKind.Movie ? "No movies yet" : "No shows yet";

    public string EmptyHint => _all.Count > 0 ? "" : _kind == TitleKind.Movie ? "Add a folder that has movies." : "Add a folder that has shows.";

    public bool HasEmptyHint => EmptyHint.Length > 0;

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
        if (!PosterCards.SameCards(Items, shown)) Items = shown;
        OnPropertyChanged(nameof(EmptyTitle)); // _all may have changed without Items changing
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(HasEmptyHint));
    }
}
