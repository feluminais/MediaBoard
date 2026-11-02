using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Library;

public sealed class SearchServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly FakeMetadataProvider _tmdb = new();
    private readonly FakeLibrary _library = new();
    private readonly LibraryDatabase _db;
    private readonly TitleStore _titles;
    private readonly Connectivity _connectivity;
    private readonly SearchService _service;

    public SearchServiceTests()
    {
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _titles = new TitleStore(_db, _clock);
        _connectivity = new Connectivity(_ => Task.FromResult(false), _clock, new ActivityHub(_clock));
        _service = new SearchService(_library, _tmdb, _connectivity, _titles);
    }

    public void Dispose()
    {
        _connectivity.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TitleCard Card(int id, string name, TitleKind kind = TitleKind.Movie) =>
        new(id, kind, name, 2021, null, "/p.jpg", 7.8, false, false, false, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static SearchCandidate Hit(int tmdbId, string name, TitleKind kind = TitleKind.Movie) =>
        new(tmdbId, kind, name, null, 2021, 50, 8.1, "/o.jpg", null);

    private static TitleDetails Details(int tmdbId, string name, TitleKind kind = TitleKind.Movie) =>
        new(tmdbId, kind, name, 2021, null, "An overview.", 8.1, "/o.jpg", 155, []);

    [Fact]
    public async Task LibraryResults_ComeFirst_AndWorkOffline()
    {
        _library.Add(Card(1, "Dune"));
        _library.Add(Card(2, "Arrival"));
        _connectivity.ReportOffline();

        var results = await _service.SearchAsync("dune", Ct);

        Assert.Equal([1], results.Library.Select(c => c.TitleId));
        Assert.Empty(results.Online);
        Assert.Equal(OnlineState.Offline, results.State);
        Assert.Empty(_tmdb.AnySearches);
    }

    [Fact]
    public async Task LibraryResults_Survive_AnOnlineFailure()
    {
        _library.Add(Card(1, "Dune"));
        _tmdb.Unavailable = UnavailableReason.ServerError;

        var results = await _service.SearchAsync("dune", Ct);

        Assert.Equal([1], results.Library.Select(c => c.TitleId));
        Assert.Empty(results.Online);
        Assert.Equal(OnlineState.Offline, results.State);
    }

    [Fact]
    public async Task Offline_StateOffline_NoThrow()
    {
        _tmdb.Unavailable = UnavailableReason.Offline;

        var results = await _service.SearchAsync("dune", Ct);

        Assert.Equal(OnlineState.Offline, results.State);
        Assert.Empty(results.Online);
        Assert.False(_connectivity.IsOnline);
    }

    [Theory]
    [InlineData(UnavailableReason.NoKey, OnlineState.NoKey)]
    [InlineData(UnavailableReason.KeyRejected, OnlineState.KeyRejected)]
    [InlineData(UnavailableReason.ServerError, OnlineState.Offline)]
    public async Task ProviderFailure_MapsToState(UnavailableReason reason, OnlineState expected)
    {
        _tmdb.Unavailable = reason;

        var results = await _service.SearchAsync("dune", Ct);

        Assert.Equal(expected, results.State);
    }

    [Fact]
    public async Task QueryUnder2Chars_SkipsOnline()
    {
        _library.Add(Card(1, "Alien"));
        _tmdb.OnSearchAny("a", Hit(10, "Avatar"));

        var results = await _service.SearchAsync("  a ", Ct);

        Assert.Equal(OnlineState.TooShort, results.State);
        Assert.Empty(results.Online);
        Assert.Equal([1], results.Library.Select(c => c.TitleId));
        Assert.Empty(_tmdb.AnySearches);
    }

    [Fact]
    public async Task Online_AnswersWithTrimmedQuery()
    {
        _tmdb.OnSearchAny("dune", Hit(438631, "Dune"));

        var results = await _service.SearchAsync("  dune ", Ct);

        Assert.Equal(OnlineState.Ok, results.State);
        var online = Assert.Single(results.Online);
        Assert.Equal(438631, online.Candidate.TmdbId);
        Assert.Null(online.LibraryTitleId);
    }

    [Fact]
    public async Task OnlineHitInLibrary_LinksLocalTitle()
    {
        var localId = _titles.Upsert(Details(438631, "Dune"));
        _tmdb.OnSearchAny("dune", Hit(438631, "Dune"), Hit(841, "Dune"), Hit(438631, "Dune", TitleKind.Show));

        var results = await _service.SearchAsync("dune", Ct);

        Assert.Equal(OnlineState.Ok, results.State);
        Assert.Equal(localId, results.Online[0].LibraryTitleId);
        Assert.Null(results.Online[1].LibraryTitleId);
        Assert.Null(results.Online[2].LibraryTitleId); // same TMDB id, other kind
    }

    [Fact]
    public async Task ResultsAreLimitedToTen()
    {
        for (var i = 1; i <= 14; i++) _library.Add(Card(i, $"Dune {i}"));
        _tmdb.OnSearchAny("dune", Enumerable.Range(1, 14).Select(i => Hit(i, $"Dune {i}")).ToArray());

        var results = await _service.SearchAsync("dune", Ct);

        Assert.Equal(10, SearchService.MaxLibraryResults);
        Assert.Equal(10, SearchService.MaxOnlineResults);
        Assert.Equal(10, results.Library.Count);
        Assert.Equal(10, results.Online.Count);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.SearchAsync("dune", cts.Token));
    }

    [Fact]
    public async Task OnlineDetails_ComeFromTmdb()
    {
        _tmdb.OnTitle(Details(438631, "Dune"));

        var details = await _service.GetOnlineDetailsAsync(438631, TitleKind.Movie, Ct);

        Assert.Equal("Dune", details?.Name);
    }

    [Theory]
    [InlineData(UnavailableReason.Offline)]
    [InlineData(UnavailableReason.NoKey)]
    [InlineData(UnavailableReason.KeyRejected)]
    [InlineData(UnavailableReason.ServerError)]
    public async Task OnlineDetails_Unavailable_ReturnsNull(UnavailableReason reason)
    {
        _tmdb.OnTitle(Details(438631, "Dune"));
        _tmdb.Unavailable = reason;

        Assert.Null(await _service.GetOnlineDetailsAsync(438631, TitleKind.Movie, Ct));
    }

    [Fact]
    public async Task OnlineSeason_IsAskedOnce_AndKept()
    {
        var season = new SeasonDetails(2, [new EpisodeDetails(2, 1, "Pilot", null, null, "Plot.", 8.4, 90, "/s.jpg")]);
        _tmdb.OnSeason(1399, season);

        Assert.Same(season, await _service.GetOnlineSeasonAsync(1399, 2, Ct));
        Assert.Same(season, await _service.GetOnlineSeasonAsync(1399, 2, Ct));

        Assert.Equal([(1399, 2)], _tmdb.SeasonFetches);
    }

    [Theory]
    [InlineData(UnavailableReason.Offline)]
    [InlineData(UnavailableReason.NoKey)]
    [InlineData(UnavailableReason.KeyRejected)]
    [InlineData(UnavailableReason.ServerError)]
    public async Task OnlineSeason_Unavailable_ReturnsNull(UnavailableReason reason)
    {
        _tmdb.OnSeason(1399, new SeasonDetails(1, []));
        _tmdb.Unavailable = reason;

        Assert.Null(await _service.GetOnlineSeasonAsync(1399, 1, Ct));
    }

    [Fact]
    public async Task OnlineSeason_AFailureIsNotKept()
    {
        _tmdb.OnSeason(1399, new SeasonDetails(1, []));
        _tmdb.Unavailable = UnavailableReason.ServerError;
        Assert.Null(await _service.GetOnlineSeasonAsync(1399, 1, Ct));

        _tmdb.Unavailable = null;

        Assert.NotNull(await _service.GetOnlineSeasonAsync(1399, 1, Ct));
    }

    [Fact]
    public void Commands_AreTheThreeFixedOnes()
    {
        var all = _service.Commands("");

        Assert.Equal(
            [("rescan", "Rescan library"), ("settings", "Open settings"), ("hidden", "Show hidden items")],
            all.Select(c => (c.Id, c.Label)));
    }

    [Theory]
    [InlineData("re", new[] { "rescan" })]
    [InlineData("RES", new[] { "rescan" })]
    [InlineData("lib", new[] { "rescan" })] // a prefix of any word in the label
    [InlineData("o", new[] { "settings" })]
    [InlineData("s", new[] { "settings", "hidden" })] // "settings" and "Show"
    [InlineData("hid", new[] { "hidden" })]
    [InlineData("items", new[] { "hidden" })]
    [InlineData("scan", new string[0])] // not a prefix of any word
    [InlineData("zzz", new string[0])]
    public void Commands_FilterByPrefix(string filter, string[] expectedIds)
    {
        Assert.Equal(expectedIds, _service.Commands(filter).Select(c => c.Id));
    }

    [Fact]
    public void Commands_BlankFilter_ReturnsAll()
    {
        Assert.Equal(3, _service.Commands("   ").Count);
    }

    private sealed class FakeLibrary : ILibrary
    {
        private readonly List<TitleCard> _cards = [];

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public void Add(TitleCard card) => _cards.Add(card);

        public IReadOnlyList<TitleCard> SearchLocal(string query, int limit) =>
            _cards.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList();

        public IReadOnlyList<ContinueItem> ContinueWatching(int limit) => throw new NotSupportedException();
        public IReadOnlyList<TitleCard> RecentlyAdded(int limit) => throw new NotSupportedException();
        public IReadOnlyList<TitleCard> Titles(TitleKind kind, TitleSort sort, bool includeHidden) => throw new NotSupportedException();
        public TitleDetailsView? Title(int titleId) => throw new NotSupportedException();
        public IReadOnlyList<ReviewItem> NeedsReview() => throw new NotSupportedException();
        public int NeedsReviewCount() => throw new NotSupportedException();
        public IReadOnlyList<FolderRow> Folders() => throw new NotSupportedException();
        public int? NextEpisodeFileId(int titleId) => throw new NotSupportedException();
        public void Rename(TargetKind kind, int id, string? displayName) => throw new NotSupportedException();
        public void SetHidden(TargetKind kind, int id, bool hidden) => throw new NotSupportedException();
        public void SetWatched(int fileId, bool watched) => throw new NotSupportedException();
        public Task FixMatchAsync(int fileId, int tmdbId, TitleKind kind, CancellationToken ct) => throw new NotSupportedException();
        public void MarkNotMedia(int fileId) => throw new NotSupportedException();
        public DeletePreview PreviewDelete(int titleId) => throw new NotSupportedException();
        public Task<DeleteResult> DeleteFromDiskAsync(DeletePreview preview) => throw new NotSupportedException();
    }
}
