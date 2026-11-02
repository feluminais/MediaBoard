using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Desktop.Controls;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;

namespace MediaBoard.Desktop.Tests;

/// <summary>The page of a show in the library: the episode graph over the viewer's progress, and the list of one season under it.</summary>
public class LibraryEpisodeMapTests
{
    private readonly ActivityHub _hub = new();
    private readonly FakeLibrary _library = new();
    private readonly Titles _t;

    public LibraryEpisodeMapTests()
    {
        _hub.SetOnline(true);
        _hub.SetKeyState(KeyState.Ok);
        _t = new Titles(_library, _hub);
    }

    // Season 1: watched, half way, no file, missing file. Season 2: three episodes, one hidden. Season 3: one.
    private void AddShow(int nextFile = 32)
    {
        _library.Details[2] = FakeLibrary.Show(2, "Dark",
            FakeLibrary.Season(2, 1,
                FakeLibrary.Episode(201, 1, 1, "Secrets", 21, watched: true),
                FakeLibrary.Episode(202, 1, 2, "Lies", 22, position: 600),
                FakeLibrary.Episode(203, 1, 3, "Nobody has this", null),
                FakeLibrary.Episode(204, 1, 4, "Gone", 24, missing: true)),
            FakeLibrary.Season(2, 2,
                FakeLibrary.Episode(205, 2, 1, "Beginnings", 31),
                FakeLibrary.Episode(206, 2, 2, "Endings", 32),
                FakeLibrary.Episode(207, 2, 3, "Hidden one", 33, hidden: true)),
            FakeLibrary.Season(2, 3, FakeLibrary.Episode(208, 3, 1, "Last", 41)));
        _library.Next[2] = nextFile;
    }

    private async Task<TitleViewModel> Loaded()
    {
        var page = _t.Page(2);
        await page.RefreshAsync();
        return page;
    }

    [AvaloniaFact]
    public async Task ShowPage_ListsTheSeasonHoldingTheNextEpisode_AndMarksIt()
    {
        AddShow();

        var page = await Loaded();

        Assert.Equal(2, page.ListedSeason!.Number);
        Assert.Equal(["Beginnings", "Endings", "Hidden one"], page.Rows.OfType<EpisodeItem>().Select(e => e.Name));
        Assert.Equal([false, true, false], page.Rows.OfType<EpisodeItem>().Select(e => e.IsNext));
        Assert.Equal(3 * TitleViewModel.RowHeight, page.ListHeight);
    }

    [AvaloniaFact]
    public async Task Graph_ShowsProgress_PerEpisode_AndSeasonsInOrder()
    {
        AddShow();

        var graph = (await Loaded()).Graph!;

        Assert.True(graph.IsLibrary);
        Assert.Equal(GraphMode.Progress, graph.Mode);
        Assert.Equal([1, 2, 3], graph.Seasons.Select(s => s.Number));
        Assert.Equal(
            [EpisodeState.Watched, EpisodeState.Started, EpisodeState.NoFile, EpisodeState.Missing],
            graph.Seasons[0].Episodes.Select(e => e.State));
        Assert.Equal(600 / 2700.0, graph.Seasons[0].Episodes[1].Progress, 3);
        Assert.Equal(EpisodeState.Unwatched, graph.Seasons[1].Episodes[0].State);
        Assert.True(graph.Seasons[1].Episodes[2].Hidden);
        Assert.Equal(2, graph.SelectedSeason);
    }

    [AvaloniaFact]
    public async Task PressingAnEpisodeInTheGraph_ListsItsSeason_AndAsksForTheRow()
    {
        AddShow();
        var page = await Loaded();
        var asked = new List<string>();
        page.ScrollRequested += e => asked.Add(e.Name);

        page.Graph!.Choose(1, 3);

        Assert.Equal(1, page.ListedSeason!.Number);
        Assert.Equal(["Secrets", "Lies", "Nobody has this", "Gone"], page.Rows.OfType<EpisodeItem>().Select(e => e.Name));
        Assert.Equal(["Nobody has this"], asked);
        Assert.Equal(1, page.Graph.SelectedSeason);

        page.Graph.Choose(3, null); // a season's heading: no row to scroll to
        Assert.Equal(3, page.ListedSeason.Number);
        Assert.Single(asked);
    }

    [AvaloniaFact]
    public async Task TheViewersSeason_SurvivesARefresh()
    {
        AddShow();
        var page = await Loaded();
        page.Graph!.Choose(1, null);

        await page.RefreshAsync();

        Assert.Equal(1, page.ListedSeason!.Number); // not back to the season of the next episode
    }

    [AvaloniaFact]
    public async Task SeasonArrows_StepThroughTheSeasons_AndStopAtTheEnds()
    {
        AddShow(nextFile: 21);
        var page = await Loaded();
        var season = page.ListedSeason!;

        Assert.Equal(1, season.Number);
        Assert.False(season.CanPrevious);
        Assert.True(season.CanNext);
        Assert.False(season.PreviousCommand.CanExecute(null));

        season.NextCommand.Execute(null);
        Assert.Equal(2, page.ListedSeason!.Number);
        page.ListedSeason.NextCommand.Execute(null);
        Assert.Equal(3, page.ListedSeason!.Number);
        Assert.False(page.ListedSeason.CanNext);

        page.ListedSeason.PreviousCommand.Execute(null);
        Assert.Equal(2, page.ListedSeason!.Number);
    }

    [AvaloniaFact]
    public async Task SeasonHeader_SaysHowFarTheViewerIs()
    {
        AddShow(nextFile: 21);
        var page = await Loaded();

        Assert.Equal("1 of 3 watched, 1 without a file", page.ListedSeason!.Summary);

        page.Graph!.Choose(2, null);
        Assert.Equal("0 of 3 watched", page.ListedSeason!.Summary);
    }

    [AvaloniaFact]
    public async Task Rows_SayWhatIsKnown_InOneLineEach()
    {
        _library.Details[2] = FakeLibrary.Show(2, "Dark",
            FakeLibrary.Season(2, 1,
                FakeLibrary.Episode(201, 1, 1, "Secrets", 21) with { AirDate = "2017-12-01" },
                FakeLibrary.Episode(202, 1, 2, "Lies", 22) with { AirDate = null, RuntimeMin = null }));
        _library.Next[2] = 21;

        var rows = (await Loaded()).Rows.OfType<EpisodeItem>().ToList();

        Assert.Equal("1 Dec 2017, 45 min", rows[0].Meta);
        Assert.Equal("", rows[1].Meta);
    }

    [AvaloniaFact]
    public async Task WatchingAnEpisode_ChangesItsCell()
    {
        AddShow();
        var page = await Loaded();
        Assert.Equal(EpisodeState.Unwatched, page.Graph!.Seasons[1].Episodes[0].State);

        await page.ListedSeason!.Episodes[0].ToggleWatchedCommand.ExecuteAsync(null);

        Assert.Equal(EpisodeState.Watched, page.Graph.Seasons[1].Episodes[0].State);
    }

    [AvaloniaFact]
    public async Task Ratings_ArriveInTheBackground_AndBecomeASecondView()
    {
        AddShow();
        _t.Search.Seasons[(102, 1)] = new SeasonDetails(1, [new EpisodeDetails(1, 1, "Secrets", 50, null, "Plot.", 9.1, 4000, "/s.jpg")]);
        var page = await Loaded();
        var graph = page.Graph!;
        await Ui.WaitUntil(() => graph.HasRatings);

        Assert.True(graph.ShowModes);
        Assert.Equal(GraphMode.Progress, graph.Mode); // loading ratings does not take the view away
        var episode = graph.Seasons[0].Episodes[0];
        Assert.Equal(9.1, episode.Rating);
        Assert.Equal("Plot.", episode.Overview);
        Assert.Equal(EpisodeState.Watched, episode.State);
        Assert.Contains("Watched", episode.Facts);

        graph.ShowRatingsCommand.Execute(null);
        Assert.Equal(GraphMode.Ratings, graph.Mode);
    }

    [AvaloniaFact]
    public async Task WithoutRatings_ThereIsNoModeSwitch_AndNoFailureShown()
    {
        AddShow(); // TMDB knows none of the seasons
        var page = await Loaded();
        var graph = page.Graph!;
        await Ui.WaitUntil(() => !graph.IsLoading);

        Assert.False(graph.ShowModes);
        Assert.False(graph.ShowStatus); // "3 seasons didn't load" would be noise on a page about progress
        Assert.Equal(GraphMode.Progress, graph.Mode);
    }

    [AvaloniaFact]
    public async Task OfflineShow_GetsTheGraph_WithoutAskingTmdb()
    {
        AddShow();
        _library.Details[2] = _library.Details[2] with { Offline = true };

        var page = await Loaded();

        Assert.NotNull(page.Graph);
        Assert.Empty(_t.Search.SeasonRequests);
    }

    [AvaloniaFact]
    public async Task AMovie_HasNoGraphAndNoSeason()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Next[1] = 10;

        var page = _t.Page(1);
        await page.RefreshAsync();

        Assert.Null(page.Graph);
        Assert.Null(page.ListedSeason);
        Assert.Empty(page.Rows);
    }

    // The bug this page was rebuilt for: rows of different heights made the scroll bar change size as the list scrolled.
    [AvaloniaFact]
    public async Task ScrollingALongSeason_NeverChangesTheScrollExtent_OrTheRowHeight()
    {
        var names = new[] { "Go", "A very long episode name that keeps going and going until it has no more room in its row whatsoever, really", "Mid length name", "x" };
        var episodes = Enumerable.Range(1, 300)
            .Select(i => FakeLibrary.Episode(i, 1, i, names[i % names.Length], 1000 + i, watched: i % 7 == 0, position: i % 5 == 0 ? 300 : 0, hidden: i % 11 == 0))
            .ToArray();
        _library.Details[2] = FakeLibrary.Show(2, "Long runner", FakeLibrary.Season(2, 1, episodes));
        _library.Next[2] = 1005;
        var page = await Loaded();
        var view = new TitleView { DataContext = page };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Ui.Flush();
        window.UpdateLayout();
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        var list = view.FindControl<ItemsControl>("List")!;

        var extents = new HashSet<double>();
        var heights = new HashSet<double>();
        for (var y = 0.0; y < scroller.Extent.Height; y += 137)
        {
            scroller.Offset = new Vector(0, y);
            Ui.Flush();
            window.UpdateLayout();
            extents.Add(Math.Round(scroller.Extent.Height, 1));
            foreach (var row in list.ItemsPanelRoot!.Children) heights.Add(Math.Round(row.Bounds.Height, 1));
        }

        Assert.Single(extents);
        Assert.Equal([TitleViewModel.RowHeight], heights);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PressingALateEpisodeInTheGraph_ScrollsTheListToItsRow()
    {
        var episodes = Enumerable.Range(1, 300).Select(i => FakeLibrary.Episode(i, 1, i, $"Episode {i}", 1000 + i)).ToArray();
        _library.Details[2] = FakeLibrary.Show(2, "Long runner", FakeLibrary.Season(2, 1, episodes));
        _library.Next[2] = 1001;
        var page = await Loaded();
        var view = new TitleView { DataContext = page };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Ui.Flush();
        window.UpdateLayout();
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        var before = scroller.Offset.Y;

        page.Graph!.Choose(1, 250);
        Ui.Flush();
        window.UpdateLayout();
        Ui.Flush();
        window.UpdateLayout();

        var row = view.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "Episode 250");
        Assert.True(scroller.Offset.Y > before + 5000, $"the page scrolled from {before} to {scroller.Offset.Y}");
        Assert.NotNull(row);
        window.Close();
    }
}
