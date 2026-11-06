using MediaBoard.Core.Activity;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Metadata;

public sealed class MetadataRefresherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-refresh-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly FakeMetadataProvider _tmdb = new();
    private readonly LibraryDatabase _db;
    private readonly ActivityHub _hub;
    private readonly TitleStore _titles;
    private readonly Connectivity _connectivity;
    private volatile bool _networkUp = true;

    public MetadataRefresherTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _hub = new ActivityHub(_clock);
        _titles = new TitleStore(_db, _clock);
        // The delay keeps the retry loop from spinning while the fake clock makes every wait instant.
        _connectivity = new Connectivity(
            async ct =>
            {
                await Task.Delay(5, ct);
                return _networkUp;
            },
            _clock,
            _hub);
    }

    public void Dispose()
    {
        _connectivity.Dispose();
        // Pooled connections keep the file locked on Windows.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MetadataRefresher Refresher(IMetadataProvider? provider = null) =>
        new(provider ?? _tmdb, _titles, _connectivity, _hub, _clock);

    private static TitleDetails Film(int id, string name, double rating = 7.0) =>
        new(id, TitleKind.Movie, name, 2001, null, "A film.", rating, "/f.jpg", 120, []);

    private static TitleDetails Show(int id, string name, params int[] seasons) =>
        new(id, TitleKind.Show, name, 2010, null, "A show.", 8.0, "/s.jpg", 45, seasons);

    private static SeasonDetails Season(int season, params string[] names) =>
        new(season, names.Select((n, i) => new EpisodeDetails(season, i + 1, n, 45, null)).ToList());

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Stores the title as if it had been fetched the given number of days before the fake now.</summary>
    private int StoreAged(TitleDetails title, double daysAgo) =>
        new TitleStore(_db, new FakeClock(_clock.GetUtcNow().AddDays(-daysAgo))).Upsert(title);

    private async Task GoOfflineAsync()
    {
        _networkUp = false;
        await _connectivity.CheckAsync(Ct);
        Assert.False(_connectivity.IsOnline);
    }

    [Fact]
    public async Task Refresh_UpdatesFetchedAt()
    {
        var id = StoreAged(Film(10, "Old Name", rating: 6.0), daysAgo: 40);
        _tmdb.OnTitle(Film(10, "New Name", rating: 8.5));

        await Refresher().RefreshAsync(id, Ct);

        var row = _titles.Get(id)!;
        Assert.Equal("New Name", row.Name);
        Assert.Equal(8.5, row.Rating);
        Assert.Equal(Now, row.FetchedAt);
    }

    [Fact]
    public async Task Refresh_Show_UpdatesStoredSeasonsAndKeepsEpisodeIds()
    {
        var id = StoreAged(Show(20, "Show", 1, 2), daysAgo: 40);
        _titles.UpsertEpisodes(id, Season(1, "Pilot", "Second"));
        var before = _titles.EpisodeIds(id, [(1, 1), (1, 2)]);
        _tmdb.OnTitle(Show(20, "Show", 1, 2))
            .OnSeason(20, Season(1, "Pilot (renamed)", "Second", "Third"))
            .OnSeason(20, Season(2, "Never stored"));

        await Refresher().RefreshAsync(id, Ct);

        // Only the stored season is fetched; season 2 was never stored.
        Assert.Equal([(20, 1)], _tmdb.SeasonFetches);
        var season = _titles.GetSeason(id, 1)!;
        Assert.Equal(["Pilot (renamed)", "Second", "Third"], season.Episodes.Select(e => e.Name));
        Assert.Equal(before, _titles.EpisodeIds(id, [(1, 1), (1, 2)]));
        Assert.Null(_titles.GetSeason(id, 2));
    }

    [Fact]
    public async Task Refresh_ProvisionalTitle_IsSkipped()
    {
        var id = _titles.UpsertProvisional("Some File", TitleKind.Movie, 1999);

        await Refresher().RefreshAsync(id, Ct);

        Assert.Empty(_tmdb.TitleFetches);
        Assert.Null(_titles.Get(id)!.FetchedAt);
    }

    [Fact]
    public async Task Refresh_UnknownTitle_IsSkipped()
    {
        await Refresher().RefreshAsync(999, Ct);

        Assert.Empty(_tmdb.TitleFetches);
    }

    [Fact]
    public async Task Refresh_WhenASeasonFails_KeepsTheOldRows()
    {
        var show = StoreAged(Show(31, "Show", 1), daysAgo: 40);
        _titles.UpsertEpisodes(show, Season(1, "Pilot"));
        _tmdb.OnTitle(Show(31, "Show renamed", 1)); // No season registered: the fake answers 404.

        await Assert.ThrowsAsync<MetadataUnavailableException>(() => Refresher().RefreshAsync(show, Ct));

        var row = _titles.Get(show)!;
        Assert.Equal("Show", row.Name);
        Assert.Equal(Now.AddDays(-40), row.FetchedAt);
    }

    [Fact]
    public async Task Stale_OnlyOlderThan30Days()
    {
        var old = StoreAged(Film(1, "Old"), daysAgo: 31);
        var exactly = StoreAged(Film(2, "Exactly"), daysAgo: 30);
        var fresh = StoreAged(Film(3, "Fresh"), daysAgo: 29);
        var provisional = _titles.UpsertProvisional("Loose File", TitleKind.Movie, null);
        _tmdb.OnTitle(Film(1, "Old")).OnTitle(Film(2, "Exactly")).OnTitle(Film(3, "Fresh"));

        await Refresher().RefreshStaleAsync(Ct);

        Assert.Equal([(1, TitleKind.Movie)], _tmdb.TitleFetches);
        Assert.Equal(Now, _titles.Get(old)!.FetchedAt);
        Assert.Equal(Now.AddDays(-30), _titles.Get(exactly)!.FetchedAt);
        Assert.Equal(Now.AddDays(-29), _titles.Get(fresh)!.FetchedAt);
        Assert.Null(_titles.Get(provisional)!.FetchedAt);
    }

    [Fact]
    public async Task Stale_LogsOneInfoLineWithTheCount()
    {
        StoreAged(Film(1, "A"), daysAgo: 45);
        StoreAged(Film(2, "B"), daysAgo: 60);
        _tmdb.OnTitle(Film(1, "A")).OnTitle(Film(2, "B"));

        await Refresher().RefreshStaleAsync(Ct);

        var entry = Assert.Single(_hub.Recent);
        Assert.Equal(ActivityLevel.Info, entry.Level);
        Assert.Contains("2", entry.Message);
    }

    [Fact]
    public async Task Stale_NothingDue_LogsNothing()
    {
        StoreAged(Film(1, "A"), daysAgo: 1);

        await Refresher().RefreshStaleAsync(Ct);

        Assert.Empty(_hub.Recent);
        Assert.Empty(_tmdb.TitleFetches);
    }

    [Fact]
    public async Task Stale_Offline_DoesNothing()
    {
        StoreAged(Film(1, "A"), daysAgo: 45);
        _tmdb.OnTitle(Film(1, "A"));
        await GoOfflineAsync();

        await Refresher().RefreshStaleAsync(Ct);

        Assert.Empty(_tmdb.TitleFetches);
        Assert.Empty(_hub.Recent);
    }

    [Theory]
    [InlineData(UnavailableReason.Offline)]
    [InlineData(UnavailableReason.NoKey)]
    public async Task Stale_StopsAtTheFirstUnavailable(UnavailableReason reason)
    {
        var first = StoreAged(Film(1, "A"), daysAgo: 45);
        StoreAged(Film(2, "B"), daysAgo: 50);
        if (reason == UnavailableReason.Offline) _networkUp = false; // else the retry probe flips it back online
        var fetched = _titles.Get(first)!.FetchedAt; // the offline retry moves the clock, so read it now
        var provider = new FailingProvider(reason);

        await Refresher(provider).RefreshStaleAsync(Ct);

        Assert.Equal(1, provider.TitleCalls);
        Assert.Equal(fetched, _titles.Get(first)!.FetchedAt);
        Assert.Empty(_hub.Recent);
        if (reason == UnavailableReason.Offline) Assert.False(_connectivity.IsOnline);
    }

    [Fact]
    public async Task Stale_ServerError_SkipsThatTitleAndContinues()
    {
        var missing = StoreAged(Film(1, "Removed from TMDB"), daysAgo: 45);
        var kept = StoreAged(Film(2, "Fine"), daysAgo: 50);
        _tmdb.OnTitle(Film(2, "Fine")); // Title 1 is unknown to the fake: it answers 404.

        await Refresher().RefreshStaleAsync(Ct);

        Assert.Equal(Now.AddDays(-45), _titles.Get(missing)!.FetchedAt);
        Assert.Equal(Now, _titles.Get(kept)!.FetchedAt);
        Assert.Contains("1", Assert.Single(_hub.Recent).Message);
    }

    private sealed class FailingProvider(UnavailableReason reason) : IMetadataProvider
    {
        public int TitleCalls { get; private set; }

        public Task<TitleDetails> GetTitleAsync(int tmdbId, TitleKind kind, CancellationToken ct)
        {
            TitleCalls++;
            throw new MetadataUnavailableException(reason);
        }

        public Task<IReadOnlyList<SearchCandidate>> SearchAsync(string title, int? year, TitleKind kind, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SearchCandidate>> SearchAnyAsync(string query, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<(int TmdbId, TitleKind Kind)?> FindByImdbIdAsync(string imdbId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SeasonDetails> GetSeasonAsync(int showTmdbId, int season, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
