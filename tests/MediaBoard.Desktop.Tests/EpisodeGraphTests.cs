using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Metadata;
using MediaBoard.Desktop.Controls;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;

namespace MediaBoard.Desktop.Tests;

public class EpisodeGraphTests
{
    private readonly ActivityHub _hub = new();
    private readonly Titles _t;

    public EpisodeGraphTests()
    {
        _hub.SetOnline(true);
        _hub.SetKeyState(KeyState.Ok);
        _t = new Titles(new FakeLibrary(), _hub);
    }

    private static SeasonDetails Season(int number, int episodes, Func<int, double?>? rating = null) =>
        new(number, Enumerable.Range(1, episodes)
            .Select(i => new EpisodeDetails(number, i, $"Episode {i}", 24, null, $"Plot of {number}x{i}.", rating?.Invoke(i) ?? 7.5, 100 + i, $"/s{number}{i}.jpg"))
            .ToList());

    private static TitleDetails Show(int tmdbId, params int[] seasons) =>
        new(tmdbId, TitleKind.Show, "Dark", 2017, null, "A town.", 8.0, "/p.jpg", 60, seasons);

    // One Piece on TMDB: about 22 seasons, 1,150 episodes, one of them very long.
    private static readonly int[] OnePiece = [8, 17, 22, 39, 13, 52, 53, 39, 52, 54, 62, 52, 53, 52, 53, 52, 52, 52, 52, 52, 52, 56];

    private void AddSeasons(int tmdbId, params int[] episodeCounts)
    {
        for (var i = 0; i < episodeCounts.Length; i++) _t.Search.Seasons[(tmdbId, i + 1)] = Season(i + 1, episodeCounts[i], e => 5 + (e % 50) / 10.0);
    }

    // ---- Layout ----

    [Fact]
    public void Layout_SeasonsSideBySide_OnlyTheFirstColumnHasEpisodeLabels()
    {
        var layout = GraphLayout.Compute([10, 10, 10], 980);

        Assert.Equal([true, false, false], layout.Columns.Select(c => c.Rail));
        Assert.Equal([0, 1, 2], layout.Columns.Select(c => c.Season));
        var m = GraphMetrics.Regular;
        Assert.Equal(m.HeaderHeight + 10 * m.Pitch - m.Gap, layout.Size.Height);
    }

    [Fact]
    public void Layout_ASeasonLongerThanTheCap_ContinuesInAnotherColumnWithItsOwnLabels()
    {
        var layout = GraphLayout.Compute([5, 130, 5], 2000);

        Assert.Equal(
            [(0, 0, 5, true), (1, 0, 60, false), (1, 60, 60, true), (1, 120, 10, true), (2, 0, 5, true)],
            layout.Columns.Select(c => (c.Season, c.Start, c.Count, c.Rail)));
    }

    [Fact]
    public void Layout_WrapsIntoBandsInsteadOfGrowingWider()
    {
        var layout = GraphLayout.Compute(Enumerable.Repeat(10, 30).ToList(), 600);

        Assert.True(layout.Size.Width <= 600);
        var bands = layout.Columns.Select(c => c.Top).Distinct().ToList();
        Assert.True(bands.Count > 1);
        Assert.All(layout.Columns.Where(c => c.Top == bands[1]).Take(1), c => Assert.True(c.Rail)); // a band starts with labels
    }

    [Fact]
    public void Layout_OneSeasonOfAThousandAndAHalfEpisodes_StaysInsideTheWidth()
    {
        var layout = GraphLayout.Compute([1500], 980);

        Assert.Equal(25, layout.Columns.Count);
        Assert.True(layout.Size.Width <= 980);
        var m = GraphMetrics.Regular;
        Assert.True(layout.Size.Height < 6 * (m.HeaderHeight + m.MaxRows * m.Pitch));
    }

    [Fact]
    public void Layout_Empty_HasNoSize()
    {
        Assert.Equal(default, GraphLayout.Compute([], 980).Size);
        Assert.Equal(default, GraphLayout.Compute([0, 0], 980).Size);
    }

    [Fact]
    public void HitTest_FindsEveryCellOfALongShow_AndNothingInTheGaps()
    {
        var layout = GraphLayout.Compute(OnePiece, 980);

        var hits = 0;
        foreach (var column in layout.Columns)
        {
            for (var row = 0; row < column.Count; row++)
            {
                var rect = layout.CellRect(column, row);
                Assert.Equal((column.Season, column.Start + row), layout.HitTest(rect.Center));
                Assert.Null(layout.HitTest(new Point(rect.Center.X, rect.Bottom + layout.Metrics.Gap / 2)));
                hits++;
            }
        }

        Assert.Equal(OnePiece.Sum(), hits);
        Assert.True(layout.Size.Width <= 980);
    }

    [Theory]
    [InlineData(null, RatingBand.Unrated)]
    [InlineData(9.7, RatingBand.Absolute)]
    [InlineData(9.6, RatingBand.Awesome)]
    [InlineData(9.0, RatingBand.Awesome)]
    [InlineData(8.9, RatingBand.Great)]
    [InlineData(8.0, RatingBand.Great)]
    [InlineData(7.9, RatingBand.Good)]
    [InlineData(7.0, RatingBand.Good)]
    [InlineData(6.9, RatingBand.Average)]
    [InlineData(6.0, RatingBand.Average)]
    [InlineData(5.9, RatingBand.Bad)]
    [InlineData(4.0, RatingBand.Bad)]
    [InlineData(3.9, RatingBand.Garbage)]
    public void RatingBands_SplitWhereTheLegendSays(double? rating, RatingBand band) => Assert.Equal(band, RatingBands.Of(rating));

    [Fact]
    public void Episode_ColoursByTheNumberItShows()
    {
        var episode = new GraphEpisode(1, 1, "A", 9.66, 10, null, null); // shown as 9.7

        Assert.Equal("9.7", episode.RatingText);
        Assert.Equal(RatingBand.Absolute, RatingBands.Of(episode.RoundedRating));
        Assert.Equal("?", new GraphEpisode(1, 1, "A", null, 0, null, null).RatingText);
        Assert.Equal("Season 3 Episode 5", new GraphEpisode(3, 5, "A", 8.2, 1387, null, null).Subtitle);
        Assert.Equal("1,387 votes", new GraphEpisode(3, 5, "A", 8.2, 1387, null, null).Facts);
        Assert.Equal("1 vote", new GraphEpisode(3, 5, "A", 8.2, 1, null, null).Facts);
        Assert.Equal("Watched, 2021-03-12, 24 min", new GraphEpisode(1, 1, "A", null, 0, null, null, EpisodeState.Watched, 0, false, 24, "2021-03-12", "Watched").Facts);
    }

    // ---- The page ----

    [AvaloniaFact]
    public async Task OnlineShow_GetsTheGraphUnderItsHeader_SeasonsInOrder_WithoutSpecials()
    {
        _t.Search.Details = Show(1399, 0, 1, 2, 3);
        AddSeasons(1399, 6, 4, 8);
        var page = _t.OnlinePage(1399, TitleKind.Show);

        await page.RefreshAsync();
        var graph = page.Graph!;
        await Ui.WaitUntil(() => graph.IsComplete);

        Assert.Empty(page.Rows); // the graph sits above the list, not in it
        Assert.Equal([1, 2, 3], graph.Seasons.Select(s => s.Number));
        Assert.Equal([6, 4, 8], graph.Seasons.Select(s => s.Episodes.Count));
        Assert.DoesNotContain(_t.Search.SeasonRequests, r => r.Season == 0);
        Assert.Null(graph.Status);
        Assert.False(graph.IsLoading);
    }

    [AvaloniaFact]
    public async Task OnlineMovie_AndLibraryTitles_HaveNoGraph()
    {
        _t.Search.Details = new TitleDetails(603, TitleKind.Movie, "The Matrix", 1999, null, "x", 8.2, "/m.jpg", 136, []);
        var movie = _t.OnlinePage(603, TitleKind.Movie);
        await movie.RefreshAsync();

        Assert.Null(movie.Graph);
        Assert.Empty(movie.Rows);
        Assert.Empty(_t.Search.SeasonRequests);
    }

    [AvaloniaFact]
    public async Task ASeasonThatFails_IsReported_AndRetryAsksOnlyForIt()
    {
        _t.Search.Details = Show(1399, 1, 2, 3);
        AddSeasons(1399, 6, 4, 8);
        _t.Search.Seasons.Remove((1399, 2));
        var page = _t.OnlinePage(1399, TitleKind.Show);
        await page.RefreshAsync();
        var graph = page.Graph!;
        await Ui.WaitUntil(() => !graph.IsLoading);

        Assert.Equal("1 season didn't load", graph.Status);
        Assert.True(graph.ShowRetry);
        Assert.Equal([1, 3], graph.Seasons.Select(s => s.Number));

        _t.Search.SeasonRequests.Clear();
        _t.Search.Seasons[(1399, 2)] = Season(2, 4);
        await graph.LoadCommand.ExecuteAsync(null);

        Assert.Equal([(1399, 2)], _t.Search.SeasonRequests);
        Assert.Equal([1, 2, 3], graph.Seasons.Select(s => s.Number));
        Assert.Null(graph.Status);
        Assert.False(graph.ShowRetry);
    }

    [AvaloniaFact]
    public async Task LeavingThePage_StopsAskingTmdb_AndComingBackFinishesTheRest()
    {
        _t.Search.Details = Show(1399, 1, 2, 3);
        AddSeasons(1399, 6, 4, 8);
        var held = new TaskCompletionSource<SeasonDetails?>();
        _t.Search.OnSeason = (id, season, ct) => season == 2 ? held.Task.WaitAsync(ct) : Task.FromResult<SeasonDetails?>(_t.Search.Seasons[(id, season)]);
        var page = _t.OnlinePage(1399, TitleKind.Show);
        await page.RefreshAsync();
        var graph = page.Graph!;
        await Ui.WaitUntil(() => graph.Seasons.Count == 1);

        page.Closed();
        await Ui.WaitUntil(() => !graph.IsLoading);

        Assert.Equal([1], graph.Seasons.Select(s => s.Number));
        Assert.Null(graph.Status);

        _t.Search.OnSeason = null;
        page.Opened();
        await Ui.WaitUntil(() => graph.IsComplete);
        Assert.Equal([1, 2, 3], graph.Seasons.Select(s => s.Number));
    }

    [AvaloniaFact]
    public async Task AShowWithNoEpisodesListed_SaysSo()
    {
        _t.Search.Details = Show(1399, 1);
        _t.Search.Seasons[(1399, 1)] = new SeasonDetails(1, []);
        var page = _t.OnlinePage(1399, TitleKind.Show);
        await page.RefreshAsync();
        await Ui.WaitUntil(() => !page.Graph!.IsLoading);

        Assert.True(page.Graph!.HasNoEpisodes);
    }

    // ---- The card ----

    [AvaloniaFact]
    public async Task Card_ShowsTheEpisode_AndFetchesTheStillOnlyOnceThePointerRests()
    {
        var asked = new List<string>();
        var card = new EpisodeCardViewModel((path, _) =>
        {
            asked.Add(path);
            return Task.FromResult<string?>("local:" + path);
        });
        var first = new GraphEpisode(1, 1, "Pilot", 8.4, 90, "A town wakes up.", "/a.jpg");
        var second = new GraphEpisode(1, 2, "Second", null, 0, null, "/b.jpg");

        card.Show(first);
        card.Show(second); // the pointer moved on before the delay
        Assert.True(card.IsVisible);
        Assert.Equal("Second", card.Title);
        Assert.False(card.HasOverview);
        Assert.Empty(asked);

        await Ui.WaitUntil(() => card.StillLocalPath is not null);
        Assert.Equal(["/b.jpg"], asked);
        Assert.Equal("local:/b.jpg", card.StillLocalPath);

        card.Show(null);
        Assert.False(card.IsVisible);
        Assert.Null(card.StillLocalPath);
    }

    // ---- The view, on a show with over a thousand episodes ----

    [AvaloniaFact]
    public async Task LongShow_FillsTheWidth_DrawsFast_AndTheCardFollowsThePointer()
    {
        _t.Search.Details = Show(37854, Enumerable.Range(1, OnePiece.Length).ToArray());
        AddSeasons(37854, OnePiece);
        var page = _t.OnlinePage(37854, TitleKind.Show);
        await page.RefreshAsync();
        await Ui.WaitUntil(() => page.Graph!.IsComplete);

        var view = new TitleView { DataContext = page };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        var timer = Stopwatch.StartNew();
        Ui.Flush();
        var graphView = view.GetVisualDescendants().OfType<EpisodeGraphView>().Single();
        var graph = graphView.GetVisualDescendants().OfType<EpisodeGraph>().Single();
        window.UpdateLayout();
        timer.Stop();

        Assert.Equal(OnePiece.Sum(), page.Graph!.Seasons.Sum(s => s.Episodes.Count));
        Assert.True(graph.Layout.Size.Width <= graph.Bounds.Width + 1, "the graph never needs a sideways scroll");
        Assert.True(timer.ElapsedMilliseconds < 2000, $"laying out and drawing took {timer.ElapsedMilliseconds} ms");

        var column = graph.Layout.Columns[3];
        graph.Hover((column.Season, column.Start + 4));
        var box = graphView.FindControl<Border>("CardBox")!;
        Assert.True(box.IsVisible);
        Assert.Contains("Episode 5", box.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.True(box.Margin.Left >= 0 && box.Margin.Top > 0);

        graph.Hover(null);
        Assert.False(box.IsVisible);
        window.Close();
    }
}
