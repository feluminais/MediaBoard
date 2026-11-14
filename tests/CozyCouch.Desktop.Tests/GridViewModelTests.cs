using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CozyCouch.Core.Activity;
using CozyCouch.Core.Library;
using CozyCouch.Core.Metadata;
using CozyCouch.Desktop.Tests.Support;
using CozyCouch.Desktop.ViewModels;
using CozyCouch.Desktop.Views;

namespace CozyCouch.Desktop.Tests;

public class GridViewModelTests
{
    private readonly ActivityHub _hub = new();
    private readonly FakeLibrary _library = new();
    private readonly List<int> _opened = [];

    private TitleGridViewModel Create(TitleKind kind = TitleKind.Movie) =>
        new(kind, _library, _hub, Posters.None(), new PageActions(_opened.Add, _ => { }, _ => { }));

    private async Task<TitleGridViewModel> Loaded(TitleKind kind = TitleKind.Movie)
    {
        var grid = Create(kind);
        await grid.RefreshAsync();
        return grid;
    }

    [AvaloniaFact]
    public async Task Grid_BuildsOnlyTheVisibleCards()
    {
        for (var i = 0; i < 5000; i++) _library.TitleCards.Add(FakeLibrary.Card(i, $"Film {i:0000}"));
        var grid = await Loaded();
        var view = new GridView { DataContext = grid };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Ui.Flush();
        window.UpdateLayout();

        var built = view.GetVisualDescendants().OfType<PosterCard>().Count();

        Assert.InRange(built, 1, 120);
    }

    [AvaloniaFact]
    public async Task Grid_ListsTheKindItIsFor()
    {
        _library.TitleCards.Add(FakeLibrary.Card(1, "Coco"));
        _library.TitleCards.Add(FakeLibrary.Card(2, "Arcane", TitleKind.Show));

        var shows = await Loaded(TitleKind.Show);

        Assert.Equal("Shows", shows.Title);
        Assert.Equal(["Arcane"], shows.Items.Select(i => i.Name));
    }

    [AvaloniaFact]
    public async Task Grid_SortAndFilter()
    {
        _library.TitleCards.Add(FakeLibrary.Card(1, "Heat", year: 1995, added: new DateTime(2026, 3, 1)));
        _library.TitleCards.Add(FakeLibrary.Card(2, "Coco", year: 2017, added: new DateTime(2026, 1, 1)));
        _library.TitleCards.Add(FakeLibrary.Card(3, "Alien", year: 1979, added: new DateTime(2026, 2, 1)));
        _library.TitleCards.Add(FakeLibrary.Card(4, "Hidden Figures", year: 2016, hidden: true));
        var grid = await Loaded();
        Assert.Equal(["Alien", "Coco", "Heat"], grid.Items.Select(i => i.Name)); // by name, hidden left out
        Assert.Equal(["Name", "Year", "Date added"], grid.SortOptions.Select(o => o.Label));

        grid.SelectedSort = grid.SortOptions.Single(o => o.Sort == TitleSort.Year);
        await Ui.WaitUntil(() => grid.Items[0].Name == "Coco");
        Assert.Equal(["Coco", "Heat", "Alien"], grid.Items.Select(i => i.Name));
        Assert.Contains(_library.TitleQueries, q => q.Sort == TitleSort.Year);

        grid.SelectedSort = grid.SortOptions.Single(o => o.Sort == TitleSort.DateAdded);
        await Ui.WaitUntil(() => grid.Items[0].Name == "Heat");
        Assert.Equal(["Heat", "Alien", "Coco"], grid.Items.Select(i => i.Name));

        grid.FilterText = " CO ";
        Assert.Equal(["Coco"], grid.Items.Select(i => i.Name)); // instant, in memory
        var queries = _library.TitleQueries.Count;
        grid.FilterText = "";
        Assert.Equal(3, grid.Items.Count);
        Assert.Equal(queries, _library.TitleQueries.Count);
    }

    [AvaloniaFact]
    public async Task Grid_ShowHidden_IncludesHiddenTitles()
    {
        _library.TitleCards.Add(FakeLibrary.Card(1, "Coco"));
        _library.TitleCards.Add(FakeLibrary.Card(2, "Hidden Figures", hidden: true));
        var grid = await Loaded();
        Assert.Single(grid.Items);

        grid.ShowHidden = true;
        await Ui.WaitUntil(() => grid.Items.Count == 2);

        Assert.True(_library.TitleQueries[^1].IncludeHidden);
        Assert.True(grid.Items.Single(i => i.Name == "Hidden Figures").IsDimmed);
    }

    [AvaloniaFact]
    public async Task Grid_Empty_SaysWhy()
    {
        var movies = await Loaded();
        Assert.True(movies.ShowEmpty);
        Assert.Equal("No movies yet", movies.EmptyTitle);
        Assert.Equal("Add a folder that has movies.", movies.EmptyHint);

        var shows = await Loaded(TitleKind.Show);
        Assert.Equal("No shows yet", shows.EmptyTitle);
    }

    [AvaloniaTheory]
    [InlineData(false)] // a scan is running
    [InlineData(true)] // a folder waits its turn to scan
    public async Task Grid_Empty_WhileAScanRunsOrWaits_SaysScanning(bool waiting)
    {
        IDisposable? queued = null;
        if (waiting) queued = _hub.QueueScan(1);
        else _hub.BeginScan(1, "Torrent Downloads");
        using var _ = queued;

        var movies = await Loaded();
        var shows = await Loaded(TitleKind.Show);

        Assert.True(movies.ShowEmpty);
        Assert.Equal("Scanning folders…", movies.EmptyTitle);
        Assert.Equal("Titles appear as they're found.", movies.EmptyHint);
        Assert.True(movies.HasEmptyHint);
        Assert.Equal("Scanning folders…", shows.EmptyTitle);
    }

    [AvaloniaFact]
    public async Task Grid_Empty_WhileOnlyIdentificationRuns_SaysHowManyFiles()
    {
        _hub.SetPendingIdentification(3);
        var movies = await Loaded();
        Assert.Equal("Identifying 3 files…", movies.EmptyTitle);
        Assert.Equal("Titles appear as they're found.", movies.EmptyHint);

        _hub.SetPendingIdentification(1);
        Ui.Flush();
        Assert.Equal("Identifying 1 file…", movies.EmptyTitle);
    }

    [AvaloniaFact]
    public async Task Grid_EmptyText_FollowsTheHubLive()
    {
        var movies = await Loaded();
        Assert.Equal("No movies yet", movies.EmptyTitle);
        var changed = new List<string?>();
        movies.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _hub.BeginScan(1, "Torrent Downloads");
        Ui.Flush();
        Assert.Equal("Scanning folders…", movies.EmptyTitle);
        Assert.Contains(nameof(TitleGridViewModel.EmptyTitle), changed);

        _hub.EndScan(1);
        Ui.Flush();
        Assert.Equal("No movies yet", movies.EmptyTitle);
        Assert.Equal("Add a folder that has movies.", movies.EmptyHint);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)] // a scan runs meanwhile
    public async Task Grid_FilterWithNoMatch_SaysNothingMatches_EvenWhileScanning(bool scanning)
    {
        _library.TitleCards.Add(FakeLibrary.Card(1, "Coco"));
        if (scanning) _hub.BeginScan(1, "Torrent Downloads");
        var grid = await Loaded();
        Assert.False(grid.ShowEmpty);

        grid.FilterText = "zzz";

        Assert.True(grid.ShowEmpty);
        Assert.Equal("Nothing matches", grid.EmptyTitle);
    }

    [AvaloniaFact]
    public async Task List_RecentlyAdded_Empty_WhileAScanRuns_SaysScanning()
    {
        var list = new ListViewModel(ListKind.RecentlyAdded, _library, _hub, Posters.None(), new PageActions(_ => { }, _ => { }, _ => { }));
        await list.RefreshAsync();
        Assert.Equal("Nothing added yet", list.EmptyText);
        Assert.False(list.HasEmptyHint);

        _hub.BeginScan(1, "Torrent Downloads");
        Ui.Flush();

        Assert.True(list.ShowEmpty);
        Assert.Equal("Scanning folders…", list.EmptyText);
        Assert.Equal("Titles appear as they're found.", list.EmptyHint);
        Assert.True(list.HasEmptyHint);
    }

    [AvaloniaFact]
    public async Task List_ContinueWatching_Empty_StaysNothingInProgress_WhileScanning()
    {
        _hub.BeginScan(1, "Torrent Downloads");
        var list = new ListViewModel(ListKind.ContinueWatching, _library, _hub, Posters.None(), new PageActions(_ => { }, _ => { }, _ => { }));
        await list.RefreshAsync();

        Assert.Equal("Nothing in progress", list.EmptyText); // a scan never adds anything to this list
    }

    [AvaloniaFact]
    public async Task List_ContinueWatching_Remove_Failure_IsLogged()
    {
        _library.Continue.Add(FakeLibrary.Resume(1, "Klaus"));
        _library.DismissFailure = new InvalidOperationException("The server can't be reached.");
        var list = new ListViewModel(ListKind.ContinueWatching, _library, _hub, Posters.None(), new PageActions(_ => { }, _ => { }, _ => { }));
        await list.RefreshAsync();

        list.Items.Single().RemoveCommand.Execute(null);

        await Ui.WaitUntil(() => _hub.Recent.Any(e => e.Level == ActivityLevel.Error && e.Message.Contains("The server can't be reached.", StringComparison.Ordinal)));
    }

    [AvaloniaFact]
    public async Task Grid_ClickingACard_OpensTheTitle()
    {
        _library.TitleCards.Add(FakeLibrary.Card(9, "Coco"));
        var grid = await Loaded();

        grid.Items[0].OpenCommand.Execute(null);

        Assert.Equal([9], _opened);
    }

    [AvaloniaFact]
    public async Task Grid_SameLibrary_KeepsItsCards()
    {
        _library.TitleCards.Add(FakeLibrary.Card(1, "Coco"));
        var grid = await Loaded();
        var items = grid.Items;

        await grid.RefreshAsync();

        Assert.Same(items, grid.Items); // nothing changed, so the grid is not rebuilt
    }

    [AvaloniaFact]
    public async Task Grid_PicksUpNewTitles_WhenTheLibraryChanges()
    {
        _library.TitleCards.Add(FakeLibrary.Card(1, "Coco"));
        var grid = await Loaded();
        var coco = grid.Items[0];
        _library.TitleCards.Add(FakeLibrary.Card(2, "Heat"));

        await grid.RefreshAsync();

        Assert.Equal(["Coco", "Heat"], grid.Items.Select(i => i.Name));
        Assert.Same(coco, grid.Items[0]); // the unchanged card is reused
    }

    [AvaloniaFact]
    public async Task Grid_OpeningThePage_LoadsIt()
    {
        _library.TitleCards.Add(FakeLibrary.Card(1, "Coco"));
        var grid = Create();

        grid.Opened();
        await Ui.WaitUntil(() => grid.IsLoaded);

        Assert.Single(grid.Items);
    }
}
