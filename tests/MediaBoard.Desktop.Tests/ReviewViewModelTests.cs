using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Identification;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;
using MediaBoard.Core.Probing;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop.Tests;

public class ReviewViewModelTests
{
    private readonly ActivityHub _hub = new();
    private readonly FakeLibrary _library = new();
    private readonly Titles _t;

    public ReviewViewModelTests()
    {
        _hub.SetOnline(true);
        _hub.SetKeyState(KeyState.Ok);
        _t = new Titles(_library, _hub);
    }

    private static ReviewCandidate Movie(int tmdbId, string name, int? year) => new(tmdbId, TitleKind.Movie, name, year);

    private static ReviewCandidate Show(int tmdbId, string name, int? year) => new(tmdbId, TitleKind.Show, name, year);

    private static ReviewItem MovieFile(int id, string rel, string title, int? year, params ReviewCandidate[] candidates) =>
        new(id, Path.GetFileName(rel), rel, title, year, MediaKind.Movie, null, [], false, candidates);

    private static ReviewItem EpisodeFile(int id, string rel, string title, int season, int episode, string showFolder, params ReviewCandidate[] candidates) =>
        new(id, Path.GetFileName(rel), rel, title, null, MediaKind.Episode, season, [episode], false, candidates, showFolder);

    private async Task<ReviewViewModel> Loaded()
    {
        var page = new ReviewViewModel(_t.Services());
        await page.RefreshAsync();
        return page;
    }

    [AvaloniaFact]
    public async Task ListsFilesWithTopCandidates()
    {
        _library.Review.Add(MovieFile(1, @"Movies\Heat.1995.mkv", "Heat", 1995,
            Movie(949, "Heat", 1995), Movie(1, "Heat", 1986), Show(2, "Heat Vision and Jack", 1999), Movie(3, "Heat Wave", 2000), Movie(4, "Heated", 2001)));

        var page = await Loaded();

        var row = Assert.Single(Assert.Single(page.Groups).Rows);
        Assert.Equal(@"Movies\Heat.1995.mkv", row.Path);
        Assert.Equal("Heat (1995)", row.Summary);
        Assert.Equal(["Heat", "Heat", "Heat Vision and Jack"], row.Candidates.Select(c => c.Name)); // the best three of five
        Assert.Equal(["1995 · Movie", "1986 · Movie", "1999 · Show"], row.Candidates.Select(c => c.Detail));
        Assert.False(page.ShowEmpty);
    }

    [AvaloniaFact]
    public async Task FilesWithoutCandidates_StillListed_WithTheSearchBox()
    {
        _library.Review.Add(MovieFile(1, "Zzz.mkv", "Zzz", null));

        var row = Assert.Single(Assert.Single((await Loaded()).Groups).Rows);

        Assert.Empty(row.Candidates);
        Assert.False(row.HasCandidates);
        Assert.NotNull(row.SearchCommand);
    }

    [AvaloniaFact]
    public async Task GroupsEpisodesByShowFolder_WithACount()
    {
        _library.Review.Add(EpisodeFile(1, @"Mystery\Season 1\Mystery.S01E01.mkv", "Mystery", 1, 1, "Mystery"));
        _library.Review.Add(EpisodeFile(2, @"Mystery\Season 1\Mystery.S01E02.mkv", "Mystery", 1, 2, "Mystery"));
        _library.Review.Add(EpisodeFile(3, @"Mystery\Season 2\Mystery.S02E10.mkv", "Mystery", 2, 10, "Mystery"));
        _library.Review.Add(EpisodeFile(4, @"Other\Other.S01E01.mkv", "Other", 1, 1, "Other"));
        _library.Review.Add(MovieFile(5, @"Zebra.2020.mkv", "Zebra", 2020));

        var page = await Loaded();

        Assert.Equal(["Mystery", "Other", null], page.Groups.Select(g => g.Header));
        Assert.Equal(["3 files", "1 file", "1 file"], page.Groups.Select(g => g.CountText));
        Assert.Equal([true, true, false], page.Groups.Select(g => g.HasHeader));
        Assert.Equal([1, 2, 3], page.Groups[0].Rows.Select(r => r.FileId));
        Assert.Equal(["Mystery S01E01", "Mystery S01E02", "Mystery S02E10"], page.Groups[0].Rows.Select(r => r.Summary));
    }

    [AvaloniaFact]
    public async Task PickCandidate_FixesMatch_AndIdentifies()
    {
        _library.Review.Add(MovieFile(1, "Heat.1995.mkv", "Heat", 1995, Movie(949, "Heat", 1995), Movie(1, "Heat", 1986)));
        _library.Review.Add(MovieFile(2, "Alien.mkv", "Alien", null, Movie(348, "Alien", 1979)));
        var page = await Loaded();

        await page.Groups[0].Rows[0].Candidates[1].PickCommand.ExecuteAsync(null);
        await page.Identification;

        Assert.Equal(["fix:1:1:Movie"], _library.Edits);
        Assert.Equal(1, _t.Identified);
        Assert.Equal([2], page.Groups.SelectMany(g => g.Rows).Select(r => r.FileId));
        Assert.Null(page.Hint); // a movie has no folder to apply it to
    }

    [AvaloniaFact]
    public async Task PickCandidate_WhenTmdbCannotAnswer_SaysWhy_AndKeepsTheFile()
    {
        _library.Review.Add(MovieFile(1, "Heat.1995.mkv", "Heat", 1995, Movie(949, "Heat", 1995)));
        _library.OnFixMatch = _ => throw new MetadataUnavailableException(UnavailableReason.Offline);
        var page = await Loaded();
        var row = page.Groups[0].Rows[0];

        await row.Candidates[0].PickCommand.ExecuteAsync(null);

        Assert.Equal("Connect to the internet to fix this match", row.Notice);
        Assert.Equal(0, _t.Identified);
        Assert.Single(page.Groups[0].Rows);
    }

    [AvaloniaFact]
    public async Task SearchBox_FindsOtherTitles()
    {
        _library.Review.Add(MovieFile(1, "Heat.1995.mkv", "Heat", 1995, Movie(1, "Wrong", 1986)));
        _t.Search.Candidates.Add(new SearchCandidate(949, TitleKind.Movie, "Heat", null, 1995, 50, 8.3, null, null));
        _t.Search.Candidates.Add(new SearchCandidate(5, TitleKind.Show, "Heat Show", null, 2001, 5, 6.0, null, null));
        var page = await Loaded();
        var row = page.Groups[0].Rows[0];

        row.SearchCommand.Execute(null);
        var picker = Assert.IsType<FixMatchPickerViewModel>(row.Picker);
        await picker.SearchAsync();

        Assert.Equal("Heat", picker.Query); // starts from what the file name says
        Assert.Equal(["Heat"], picker.Results.Select(c => c.Name)); // a movie file filters to movies first
        picker.Filter = MatchFilter.All;
        Assert.Equal(["Heat", "Heat Show"], picker.Results.Select(c => c.Name));

        await picker.PickCommand.ExecuteAsync(picker.Results[0]);
        await page.Identification;

        Assert.Equal(["fix:1:949:Movie"], _library.Edits);
        Assert.Null(row.Picker);
        Assert.Empty(page.Groups);
    }

    [AvaloniaFact]
    public async Task SearchBox_ForAnEpisode_StartsWithShows()
    {
        _library.Review.Add(EpisodeFile(1, @"Mystery\Mystery.S01E01.mkv", "Mystery", 1, 1, "Mystery"));
        var page = await Loaded();

        page.Groups[0].Rows[0].SearchCommand.Execute(null);

        Assert.Equal(MatchFilter.Shows, page.Groups[0].Rows[0].Picker!.Filter);
    }

    [AvaloniaFact]
    public async Task SearchBox_OpensOnOneRowAtATime()
    {
        _library.Review.Add(MovieFile(1, "A.mkv", "A", null));
        _library.Review.Add(MovieFile(2, "B.mkv", "B", null));
        var page = await Loaded();
        var rows = page.Groups[0].Rows;

        rows[0].SearchCommand.Execute(null);
        rows[1].SearchCommand.Execute(null);

        Assert.Null(rows[0].Picker);
        Assert.NotNull(rows[1].Picker);

        rows[1].Picker!.CancelCommand.Execute(null);
        Assert.Null(rows[1].Picker);
    }

    [AvaloniaTheory]
    [InlineData(false, KeyState.Ok, "Connect to the internet to fix this match")]
    [InlineData(true, KeyState.Missing, "Add a TMDB key in Settings")]
    [InlineData(true, KeyState.Rejected, "TMDB rejected the key. Check it in Settings.")]
    public async Task SearchBox_WithoutTmdb_SaysWhy_AndOpensNoPicker(bool online, KeyState key, string message)
    {
        _library.Review.Add(MovieFile(1, "A.mkv", "A", null));
        var page = await Loaded();
        _hub.SetOnline(online);
        _hub.SetKeyState(key);
        var row = page.Groups[0].Rows[0];

        row.SearchCommand.Execute(null);

        Assert.Null(row.Picker);
        Assert.Equal(message, row.Notice);
    }

    [AvaloniaFact]
    public async Task NotAMovieOrShow_RemovesItem()
    {
        _library.Review.Add(MovieFile(1, "Holiday.mkv", "Holiday", null));
        _library.Review.Add(MovieFile(2, "Alien.mkv", "Alien", null));
        var page = await Loaded();

        await page.Groups[0].Rows[0].NotMediaCommand.ExecuteAsync(null);

        Assert.Equal(["Holiday.mkv"], _t.NotMediaAsked);
        Assert.Equal(["notmedia:1"], _library.Edits);
        Assert.Equal([2], page.Groups.SelectMany(g => g.Rows).Select(r => r.FileId));
    }

    [AvaloniaFact]
    public async Task NotAMovieOrShow_WhenDeclined_ChangesNothing()
    {
        _library.Review.Add(MovieFile(1, "Holiday.mkv", "Holiday", null));
        _t.ConfirmNotMedia = false;
        var page = await Loaded();

        await page.Groups[0].Rows[0].NotMediaCommand.ExecuteAsync(null);

        Assert.Empty(_library.Edits);
        Assert.Single(page.Groups[0].Rows);
    }

    [AvaloniaFact]
    public async Task Empty_ShowsNothingToReview()
    {
        var page = await Loaded();

        Assert.True(page.ShowEmpty);
        Assert.Equal("Nothing to review", page.EmptyText);
        Assert.Empty(page.Groups);
    }

    [AvaloniaFact]
    public async Task BeforeTheFirstLoad_NothingIsSaidYet()
    {
        var page = new ReviewViewModel(_t.Services());

        Assert.False(page.ShowEmpty);
        await page.RefreshAsync();
        Assert.True(page.ShowEmpty);
    }

    [AvaloniaFact]
    public async Task Refresh_QueriesOffTheUiThread_AndKeepsTheOpenSearch()
    {
        _library.Review.Add(MovieFile(1, "A.mkv", "A", null));
        var threads = new List<int>();
        _library.OnQuery = name =>
        {
            if (name == nameof(ILibrary.NeedsReview)) lock (threads) threads.Add(Environment.CurrentManagedThreadId);
        };
        var page = await Loaded();
        page.Groups[0].Rows[0].SearchCommand.Execute(null);
        var picker = page.Groups[0].Rows[0].Picker;

        await page.RefreshAsync(); // the library changed behind the page

        Assert.All(threads, id => Assert.NotEqual(Environment.CurrentManagedThreadId, id));
        Assert.Equal(2, threads.Count);
        Assert.Same(picker, page.Groups[0].Rows[0].Picker);
    }

    [AvaloniaFact]
    public async Task Candidates_GainRatingAndPoster_WhenTmdbAnswers()
    {
        var asked = new List<string>();
        _t.Posters = Posters.Local(asked);
        _t.Search.Details = new TitleDetails(949, TitleKind.Movie, "Heat", 1995, null, "About.", 8.3, "/heat.jpg", 170, []);
        _library.Review.Add(MovieFile(1, "Heat.1995.mkv", "Heat", 1995, Movie(949, "Heat", 1995)));

        var page = await Loaded();
        var candidate = page.Groups[0].Rows[0].Candidates[0];

        await Ui.WaitUntil(() => candidate.PosterLocalPath is not null);

        Assert.Equal("1995 · Movie · 8.3", candidate.Detail);
        Assert.Equal("local:/heat.jpg", candidate.PosterLocalPath);
    }

    [AvaloniaFact]
    public async Task Candidates_StayPlain_WhenTmdbCannotAnswer()
    {
        _t.Search.Details = null;
        _library.Review.Add(MovieFile(1, "Heat.1995.mkv", "Heat", 1995, Movie(949, "Heat", 1995)));

        var page = await Loaded();
        await page.Enrichment;

        var candidate = page.Groups[0].Rows[0].Candidates[0];
        Assert.Equal("1995 · Movie", candidate.Detail);
        Assert.Null(candidate.PosterLocalPath);
    }

    [AvaloniaFact]
    public async Task Page_ShowsGroupsCandidatesAndButtons()
    {
        _library.Review.Add(EpisodeFile(1, @"Mystery\Mystery.S01E01.mkv", "Mystery", 1, 1, "Mystery", Show(7, "Mystery Show", 2012)));
        _library.Review.Add(EpisodeFile(2, @"Mystery\Mystery.S01E02.mkv", "Mystery", 1, 2, "Mystery", Show(7, "Mystery Show", 2012)));
        var view = new ReviewView { DataContext = await Loaded() };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        window.Show();
        Ui.Flush();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();

        Assert.Contains("Mystery", texts);
        Assert.Contains("2 files", texts);
        Assert.Contains(@"Mystery\Mystery.S01E02.mkv", texts);
        Assert.Contains("Mystery Show", texts);
        Assert.Equal(2, buttons.Count(b => b == "Pick"));
        Assert.Equal(2, buttons.Count(b => b == "Search"));
        Assert.Equal(2, buttons.Count(b => b == "Not a movie or show"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Page_WhenEmpty_SaysNothingToReview()
    {
        var view = new ReviewView { DataContext = await Loaded() };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        window.Show();
        Ui.Flush();

        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Nothing to review");
        window.Close();
    }

    // ---- Enrichment stays small and quiet ----

    private static TitleDetails Details(int tmdbId) => new(tmdbId, TitleKind.Movie, "T" + tmdbId, 2000, null, null, 7.0, $"/{tmdbId}.jpg", null, []);

    [AvaloniaFact]
    public async Task Enrichment_WithARejectedKey_AsksNothing_AndLogsNothing()
    {
        _hub.SetKeyState(KeyState.Rejected);
        var logged = _hub.Recent.Count;
        _library.Review.Add(MovieFile(1, "Heat.1995.mkv", "Heat", 1995, Movie(949, "Heat", 1995)));

        var page = await Loaded();
        await page.Enrichment;

        Assert.Empty(_t.Search.DetailRequests);
        Assert.Equal(logged, _hub.Recent.Count);
    }

    [AvaloniaFact]
    public async Task Enrichment_StopsAtTheFirstOfflineFailure_AndAsksAgainOnceBackOnline()
    {
        _t.Search.OnDetails = (_, _) =>
        {
            _hub.SetOnline(false); // what the real client does when TMDB is unreachable
            return null;
        };
        _library.Review.Add(MovieFile(1, "A.mkv", "A", null, Movie(1, "A", 2000)));
        _library.Review.Add(MovieFile(2, "B.mkv", "B", null, Movie(2, "B", 2000)));
        var page = await Loaded();
        await page.Enrichment;

        Assert.Single(_t.Search.DetailRequests);

        _t.Search.OnDetails = (id, _) => Details(id);
        _hub.SetOnline(true);
        await page.RefreshAsync();
        await page.Enrichment;

        Assert.Equal(3, _t.Search.DetailRequests.Count); // the failed title was not blamed for the connection
    }

    [AvaloniaFact]
    public async Task Enrichment_DoesNotAskAgainForATitleThatFailed_UntilThePageIsReopened()
    {
        _t.Search.Details = null; // TMDB answers, but not for this id (a removed title)
        _library.Review.Add(MovieFile(1, "A.mkv", "A", null, Movie(1, "A", 2000)));
        var page = await Loaded();
        await page.Enrichment;
        Assert.Single(_t.Search.DetailRequests);

        await page.RefreshAsync(); // a library change
        await page.Enrichment;
        Assert.Single(_t.Search.DetailRequests);

        page.Opened();
        await Ui.WaitUntil(() => _t.Search.DetailRequests.Count == 2);
    }

    [AvaloniaFact]
    public async Task Enrichment_AsksOncePerDistinctTitle()
    {
        _t.Search.OnDetails = (id, _) => Details(id);
        for (var i = 1; i <= 4; i++)
            _library.Review.Add(MovieFile(i, $"F{i}.mkv", $"F{i}", null, Movie(1, "Same", 2000), Movie(2 + i % 2, "Other", 2001)));

        var page = await Loaded();
        await page.Enrichment;
        await page.RefreshAsync();
        await page.Enrichment;

        Assert.Equal(3, _t.Search.DetailRequests.Count);
        Assert.Equal(3, _t.Search.DetailRequests.Distinct().Count());
    }

    [AvaloniaFact]
    public async Task Enrichment_OnlyLooksAtTheFirstRows()
    {
        _t.Search.OnDetails = (id, _) => Details(id);
        for (var i = 1; i <= 100; i++) _library.Review.Add(MovieFile(i, $"F{i:000}.mkv", $"F{i}", null, Movie(i, "T", 2000)));

        var page = await Loaded();
        await page.Enrichment;

        Assert.Equal(ReviewViewModel.EnrichRowLimit, _t.Search.DetailRequests.Count);
    }

    [AvaloniaFact]
    public async Task Enrichment_StopsWhenThePageIsLeft()
    {
        var logged = _hub.Recent.Count;
        using var gate = new ManualResetEventSlim();
        _t.Search.OnDetails = (id, _) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return Details(id);
        };
        _library.Review.Add(MovieFile(1, "A.mkv", "A", null, Movie(1, "A", 2000)));
        _library.Review.Add(MovieFile(2, "B.mkv", "B", null, Movie(2, "B", 2000)));
        var page = new ReviewViewModel(_t.Services());
        _ = page.RefreshAsync();
        await Ui.WaitUntil(() => _t.Search.DetailRequests.Count == 1); // the first request is in flight

        page.Closed();
        gate.Set();
        await page.Enrichment;

        Assert.Single(_t.Search.DetailRequests);
        Assert.Equal(logged, _hub.Recent.Count); // the cancelled request is no failure
    }

    [AvaloniaFact]
    public async Task Enrichment_ReopeningWhileARequestIsStillInFlight_CarriesOn()
    {
        using var gate = new ManualResetEventSlim();
        _t.Search.OnDetails = (id, _) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return Details(id);
        };
        _library.Review.Add(MovieFile(1, "A.mkv", "A", null, Movie(1, "A", 2000)));
        _library.Review.Add(MovieFile(2, "B.mkv", "B", null, Movie(2, "B", 2000)));
        var logged = _hub.Recent.Count;
        var page = new ReviewViewModel(_t.Services());
        _ = page.RefreshAsync();
        await Ui.WaitUntil(() => _t.Search.DetailRequests.Count == 1); // the first request is in flight

        page.Closed();
        page.Opened(); // back again before that request ended
        gate.Set();

        var candidates = () => page.Groups.SelectMany(g => g.Rows).SelectMany(r => r.Candidates).ToList();
        await Ui.WaitUntil(() => candidates().Count == 2 && candidates().All(c => c.Detail.EndsWith("7.0")));
        Assert.Equal(logged, _hub.Recent.Count);
    }

    // ---- Refreshes keep what they can ----

    [AvaloniaFact]
    public async Task Removing_OneRow_KeepsTheOtherRowsAndGroups()
    {
        _library.Review.Add(EpisodeFile(1, @"Mystery\Mystery.S01E01.mkv", "Mystery", 1, 1, "Mystery"));
        _library.Review.Add(EpisodeFile(2, @"Mystery\Mystery.S01E02.mkv", "Mystery", 1, 2, "Mystery"));
        _library.Review.Add(EpisodeFile(3, @"Other\Other.S01E01.mkv", "Other", 1, 1, "Other"));
        _library.Review.Add(MovieFile(4, "Zebra.mkv", "Zebra", null));
        var page = await Loaded();
        var groups = page.Groups.ToList();
        var rows = groups.SelectMany(g => g.Rows).ToList();
        var items = page.Items.ToList();
        var changes = new List<NotifyCollectionChangedEventArgs>();
        page.Items.CollectionChanged += (_, e) => changes.Add(e);

        _library.Review.RemoveAll(r => r.FileId == 1);
        await page.RefreshAsync();

        Assert.True(groups.SequenceEqual(page.Groups, ReferenceEqualityComparer.Instance as IEqualityComparer<ReviewGroup>));
        Assert.True(rows.Where(r => r.FileId != 1).SequenceEqual(page.Groups.SelectMany(g => g.Rows), ReferenceEqualityComparer.Instance as IEqualityComparer<ReviewRowViewModel>));
        Assert.True(items.Where(i => i is not ReviewRowViewModel { FileId: 1 }).SequenceEqual(page.Items, ReferenceEqualityComparer.Instance as IEqualityComparer<object>));
        var change = Assert.Single(changes); // one removal, nothing rebuilt
        Assert.Equal(NotifyCollectionChangedAction.Remove, change.Action);
        Assert.Equal("1 file", groups[0].CountText);
    }

    [AvaloniaFact]
    public async Task Removing_TheLastRowOfAGroup_DropsItsHeader()
    {
        _library.Review.Add(EpisodeFile(1, @"Mystery\Mystery.S01E01.mkv", "Mystery", 1, 1, "Mystery"));
        _library.Review.Add(EpisodeFile(2, @"Other\Other.S01E01.mkv", "Other", 1, 1, "Other"));
        var page = await Loaded();
        var other = page.Groups[1];

        _library.Review.RemoveAll(r => r.FileId == 1);
        await page.RefreshAsync();

        Assert.Same(other, Assert.Single(page.Groups));
        Assert.Equal(2, page.Items.Count);
        Assert.Same(other, page.Items[0]);
        Assert.Same(other.Rows[0], page.Items[1]);
    }

    [AvaloniaFact]
    public async Task ARowThatNoLongerMatches_IsReplaced_KeepingItsOpenSearch()
    {
        _library.Review.Add(MovieFile(1, "Heat.mkv", "Heat", null, Movie(1, "Old", 2000)));
        var page = await Loaded();
        var first = page.Groups[0].Rows[0];
        first.SearchCommand.Execute(null);
        var picker = first.Picker;

        _library.Review.Clear();
        _library.Review.Add(MovieFile(1, "Heat.mkv", "Heat", null, Movie(2, "New", 2001)));
        await page.RefreshAsync();

        var second = Assert.Single(page.Groups[0].Rows);
        Assert.NotSame(first, second);
        Assert.Same(picker, second.Picker);
        Assert.Same(second, Assert.Single(page.Items));
    }

    [AvaloniaFact]
    public async Task TheList_BuildsOnlyTheRowsOnScreen()
    {
        for (var i = 1; i <= 1000; i++) _library.Review.Add(MovieFile(i, $"F{i:0000}.mkv", $"F{i}", null, Movie(i, "T", 2000)));
        var page = await Loaded();
        var view = new ReviewView { DataContext = page };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        window.Show();
        Ui.Flush();
        window.UpdateLayout();
        Ui.Flush();

        var panel = view.GetVisualDescendants().OfType<VirtualizingStackPanel>().First();
        var rowViews = view.GetVisualDescendants().OfType<Button>().Count(b => b.Content as string == "Not a movie or show");

        Assert.Equal(1000, page.Items.Count);
        Assert.InRange(panel.Children.Count, 1, 60);
        Assert.InRange(rowViews, 1, 60);
        window.Close();
    }

    // An ItemsControl does not scroll by itself: without a ScrollViewer around it only the first screenful was reachable.
    [AvaloniaFact]
    public async Task TheList_ScrollsToTheRowsBelowTheFirstScreen()
    {
        for (var i = 1; i <= 200; i++) _library.Review.Add(MovieFile(i, $"F{i:0000}.mkv", $"F{i}", null, Movie(i, "T", 2000)));
        var page = await Loaded();
        var view = new ReviewView { DataContext = page };
        var window = new Window { Width = 1000, Height = 640, Content = view };
        window.Show();
        Ui.Flush();
        window.UpdateLayout();

        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height * 5, "the list is much taller than the window");

        scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
        Ui.Flush();
        window.UpdateLayout();
        Ui.Flush();
        window.UpdateLayout();

        var summaries = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(summaries, text => text?.StartsWith("F200") == true);
        window.Close();
    }

    // ---- The real stack: a pick for one episode reaches its siblings through the folder rule ----

    private sealed class NoProbe : IDurationProbe
    {
        public Task<double?> GetDurationSecondsAsync(string path, CancellationToken ct) => Task.FromResult<double?>(null);
    }

    /// <summary>Answers for the one show the test picks, and nothing else.</summary>
    private sealed class ShowProvider : IMetadataProvider
    {
        public List<(string Title, int? Year, TitleKind Kind)> Searches { get; } = [];

        public Task<IReadOnlyList<SearchCandidate>> SearchAsync(string title, int? year, TitleKind kind, CancellationToken ct)
        {
            lock (Searches) Searches.Add((title, year, kind));
            return Task.FromResult<IReadOnlyList<SearchCandidate>>([]);
        }

        public Task<IReadOnlyList<SearchCandidate>> SearchAnyAsync(string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchCandidate>>([]);

        public Task<(int TmdbId, TitleKind Kind)?> FindByImdbIdAsync(string imdbId, CancellationToken ct) =>
            Task.FromResult<(int, TitleKind)?>(null);

        public Task<TitleDetails> GetTitleAsync(int tmdbId, TitleKind kind, CancellationToken ct) =>
            Task.FromResult(new TitleDetails(777, TitleKind.Show, "The Real Show", 2012, null, "Real.", 8.1, "/real.jpg", 40, [1]));

        public Task<SeasonDetails> GetSeasonAsync(int showTmdbId, int season, CancellationToken ct) =>
            Task.FromResult(new SeasonDetails(season, [
                new EpisodeDetails(season, 1, "First", 40, null),
                new EpisodeDetails(season, 2, "Second", 40, null),
                new EpisodeDetails(season, 3, "Third", 40, null)]));
    }

    [AvaloniaFact]
    public async Task PickCandidate_FixesMatch_AndRemovesSiblingsCoveredByRule()
    {
        var provider = new ShowProvider();
        using var stack = new ShellStack(services =>
        {
            services.AddSingleton<IMetadataProvider>(provider);
            services.AddSingleton<IDurationProbe>(new NoProbe());
        });
        stack.Settings.Save(new AppSettings { TmdbKey = "key" });
        var ct = TestContext.Current.CancellationToken;

        var root = stack.MakeLibrary(
            "Shows",
            @"Mystery\Season 1\Mystery.S01E01.mkv", @"Mystery\Season 1\Mystery.S01E02.mkv", @"Mystery\Season 1\Mystery.S01E03.mkv",
            @"Other\Season 1\Mystery.S01E01.mkv", @"Mystery\Mystery (2001).mkv");
        var folder = stack.Services.GetRequiredService<FolderStore>().Add(root);
        await stack.Services.GetRequiredService<LibraryScanner>().ScanAsync(folder, ct);
        var files = stack.Services.GetRequiredService<FileStore>();
        var matches = new MatchStore(stack.Services.GetRequiredService<LibraryDatabase>());
        var titles = stack.Services.GetRequiredService<TitleStore>();
        var guesses = new[] { new SearchCandidate(777, TitleKind.Show, "The Real Show", null, 2012, 1, 8.1, null, null) };
        foreach (var file in files.ByFolder(folder.Id))
            Assert.True(matches.SetReview(file.Id, guesses, titles.UpsertProvisional("Mystery", TitleKind.Show, null)));
        provider.Searches.Clear();

        var identification = stack.Services.GetRequiredService<IdentificationService>();
        var services = _t.Services() with
        {
            Library = stack.Library,
            Hub = stack.Hub,
            IdentifyPending = () => identification.RunPendingAsync(ct),
        };
        stack.Hub.SetOnline(true);
        stack.Hub.SetKeyState(KeyState.Ok);
        var page = new ReviewViewModel(services);
        await page.RefreshAsync();

        // Five files wait: three episodes of Mystery (one show folder), the same names in another folder, a loose file.
        Assert.Equal(["Mystery", "Other", null], page.Groups.Select(g => g.Header));
        Assert.Equal(["3 files", "1 file", "1 file"], page.Groups.Select(g => g.CountText));
        Assert.Equal(5, stack.Library.NeedsReviewCount());

        var first = page.Groups[0].Rows[0];
        Assert.EndsWith("Mystery.S01E01.mkv", first.Path);
        await first.Candidates[0].PickCommand.ExecuteAsync(null);

        // The library moved the picked file and put its two siblings back in the queue, so they already left the list.
        Assert.Equal(["Other", null], page.Groups.Select(g => g.Header));
        Assert.Equal(@"Saved for Mystery. Identifying 2 more files.", page.Hint);

        await page.Identification;
        await page.RefreshAsync();

        // The identification run answered for the siblings from the rule, without asking TMDB which show it is.
        Assert.Empty(provider.Searches);
        Assert.Equal(2, stack.Library.NeedsReviewCount());
        Assert.Equal(["Other", null], page.Groups.Select(g => g.Header)); // another show folder and a loose file are not covered
        var matched = stack.Library.Title(titles.ByTmdb(777, TitleKind.Show)!.Id)!;
        Assert.Equal(3, matched.Seasons.Single().Episodes.Count(e => e.FileId is not null));
    }
}
