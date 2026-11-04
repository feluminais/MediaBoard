using System.Globalization;
using Dapper;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Identification;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;
using MediaBoard.Core.Probing;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Identification;

public sealed class IdentificationServiceTests : IDisposable
{
    private readonly TempLibrary _lib = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-idsvc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly FakeMetadataProvider _tmdb = new();
    private readonly StubProbe _probe = new();
    private readonly LibraryDatabase _db;
    private readonly ActivityHub _hub;
    private readonly FileStore _files;
    private readonly TitleStore _titles;
    private readonly MatchStore _matches;
    private readonly LibraryScanner _scanner;
    private readonly SettingsStore _settings;
    private readonly Connectivity _connectivity;
    private readonly IdentificationService _service;
    private readonly LibraryFolder _folder;
    private volatile bool _networkUp = true;

    public IdentificationServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _hub = new ActivityHub(_clock);
        _files = new FileStore(_db);
        _titles = new TitleStore(_db, _clock);
        _matches = new MatchStore(_db);
        _scanner = new LibraryScanner(_db, _hub, _clock);
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        _settings.Save(new AppSettings { TmdbKey = "first-key" });
        _connectivity = new Connectivity(
            async ct =>
            {
                await Task.Delay(1, ct);
                return _networkUp;
            },
            _clock,
            _hub);
        _service = new IdentificationService(_db, _tmdb, _probe, _connectivity, _settings, _hub, _clock);
        _folder = new FolderStore(_db, _clock).Add(_lib.Root);
    }

    public void Dispose()
    {
        _service.Dispose();
        _connectivity.Dispose();
        // Pooled connections keep the file locked on Windows.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        _lib.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SearchCandidate ShowHit(int id, string name, int? year) =>
        new(id, TitleKind.Show, name, null, year, 50, 8.0, null, null);

    private static SearchCandidate FilmHit(int id, string name, int? year) =>
        new(id, TitleKind.Movie, name, null, year, 50, 7.0, null, null);

    private static TitleDetails ShowTitle(int id, string name, int? year, params int[] seasons) =>
        new(id, TitleKind.Show, name, year, null, "A show.", 8.0, "/p.jpg", 45, seasons);

    private static TitleDetails FilmTitle(int id, string name, int? year) =>
        new(id, TitleKind.Movie, name, year, null, "A film.", 7.0, "/f.jpg", 120, []);

    private static SeasonDetails Season(int season, params string[] names) =>
        new(season, names.Select((name, i) => new EpisodeDetails(season, i + 1, name, 45, null)).ToList());

    private void KnowLost()
    {
        _tmdb.OnSearch("Lost", null, TitleKind.Show, ShowHit(4607, "Lost", 2004))
            .OnTitle(ShowTitle(4607, "Lost", 2004, 1, 2))
            .OnSeason(4607, Season(1, "Pilot", "Walkabout"))
            .OnSeason(4607, Season(2, "Man of Science", "Adrift"));
    }

    private void KnowHeat()
    {
        _tmdb.OnSearch("Heat", 1995, TitleKind.Movie, FilmHit(949, "Heat", 1995))
            .OnTitle(FilmTitle(949, "Heat", 1995));
    }

    private async Task Scan() => await _scanner.ScanAsync(_folder, Ct);

    private Task Run() => _service.RunPendingAsync(Ct);

    private MediaFileRow Row(string relPath) => _files.ByFolder(_folder.Id).Single(r => r.RelPath == relPath);

    private void ClearProviderCalls()
    {
        _tmdb.Searches.Clear();
        _tmdb.ImdbLookups.Clear();
        _tmdb.TitleFetches.Clear();
        _tmdb.SeasonFetches.Clear();
    }

    private T Scalar<T>(string sql, object? args = null)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<T>(sql, args)!;
    }

    private string LinkedTitleName(int fileId) => Scalar<string>(
        "SELECT t.name FROM file_titles ft JOIN titles t ON t.id = ft.title_id WHERE ft.file_id = @fileId", new { fileId });

    private string LinkSource(int fileId) =>
        Scalar<string>("SELECT source FROM file_titles WHERE file_id = @fileId", new { fileId });

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task Matched_StoresTitleEpisodesAndStatus()
    {
        KnowLost();
        KnowHeat();
        _lib.File(@"Lost\Season 1\Lost.S01E02.mkv");
        _lib.File(@"Heat (1995).mkv");
        await Scan();

        await Run();

        var episode = Row(@"Lost\Season 1\Lost.S01E02.mkv");
        var movie = Row("Heat (1995).mkv");
        Assert.Equal("matched", episode.Status);
        Assert.Equal("matched", movie.Status);

        var show = _titles.ByTmdb(4607, TitleKind.Show);
        Assert.NotNull(show);
        Assert.Equal("Lost", show.Name);
        Assert.Equal(2004, show.Year);
        Assert.Equal("A show.", show.Overview);
        Assert.NotNull(show.FetchedAt);
        Assert.Equal("Lost", LinkedTitleName(episode.Id));
        Assert.Equal("auto", LinkSource(episode.Id));

        // The whole season is stored; the file points at episode 2 only.
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM episodes WHERE title_id = @id", new { id = show.Id }));
        Assert.Equal("Walkabout", Scalar<string>(
            "SELECT e.name FROM file_episodes fe JOIN episodes e ON e.id = fe.episode_id WHERE fe.file_id = @id",
            new { id = episode.Id }));

        Assert.Equal("Heat", LinkedTitleName(movie.Id));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM file_episodes WHERE file_id = @id", new { id = movie.Id }));
        Assert.Equal(0, _hub.Current.PendingIdentification);
    }

    [Fact]
    public async Task AlreadyMatched_NoProviderCalls()
    {
        KnowLost();
        _lib.File(@"Lost\Season 1\Lost.S01E01.mkv");
        await Scan();
        await Run();
        ClearProviderCalls();
        var probed = _probe.Paths.Count;

        await Scan();
        await Run();

        Assert.Empty(_tmdb.Searches);
        Assert.Empty(_tmdb.TitleFetches);
        Assert.Empty(_tmdb.SeasonFetches);
        Assert.Equal(probed, _probe.Paths.Count);
        Assert.Equal("matched", Row(@"Lost\Season 1\Lost.S01E01.mkv").Status);
    }

    [Fact]
    public async Task Duration_IsProbedOncePerFile_AndStored()
    {
        KnowHeat();
        _probe.Result = 6000;
        _lib.File(@"Heat (1995).mkv");
        await Scan();

        await Run();

        Assert.Single(_probe.Paths);
        Assert.Equal(6000, Row("Heat (1995).mkv").DurationS);
    }

    [Fact]
    public async Task Offline_FileGetsProvisionalTitle_StatusOffline()
    {
        _tmdb.Unavailable = UnavailableReason.Offline;
        _networkUp = false;
        _lib.File(@"Lost\Season 1\Lost.S01E01.mkv");
        _lib.File(@"Lost\Season 1\Lost.S01E02.mkv");
        _lib.File(@"Heat (1995).mkv");
        await Scan();

        await Run();

        var first = Row(@"Lost\Season 1\Lost.S01E01.mkv");
        var second = Row(@"Lost\Season 1\Lost.S01E02.mkv");
        var movie = Row("Heat (1995).mkv");
        Assert.All(new[] { first, second, movie }, f => Assert.Equal("offline", f.Status));
        Assert.Equal("Lost", LinkedTitleName(first.Id));
        Assert.Equal("Heat", LinkedTitleName(movie.Id));
        Assert.Equal("auto", LinkSource(first.Id));
        Assert.Equal(0.0, Scalar<double>("SELECT confidence FROM file_titles WHERE file_id = @id", new { id = first.Id }));

        // Both episodes share one provisional show; the film keeps its year.
        Assert.Equal(
            Scalar<int>("SELECT title_id FROM file_titles WHERE file_id = @id", new { id = first.Id }),
            Scalar<int>("SELECT title_id FROM file_titles WHERE file_id = @id", new { id = second.Id }));
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM titles WHERE tmdb_id IS NULL"));
        Assert.Equal("show", Scalar<string>("SELECT kind FROM titles WHERE name = 'Lost'"));
        Assert.Equal(1995, Scalar<int>("SELECT year FROM titles WHERE name = 'Heat'"));
        Assert.False(_connectivity.IsOnline);
        Assert.Equal(3, _hub.Current.PendingIdentification);
    }

    [Fact]
    public async Task Review_StoresCandidates_AndProvisionalTitle()
    {
        _tmdb.OnSearch("Heat", 1995, TitleKind.Movie, FilmHit(1, "Heat Wave", 1990), FilmHit(2, "Heated", 1996));
        _lib.File(@"Heat (1995).mkv");
        await Scan();

        await Run();

        var movie = Row("Heat (1995).mkv");
        Assert.Equal("review", movie.Status);
        Assert.Equal("Heat", LinkedTitleName(movie.Id));
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM review_candidates WHERE file_id = @id", new { id = movie.Id }));
        Assert.Equal(0, _hub.Current.PendingIdentification);
    }

    [Fact]
    public async Task LaterMatch_ReplacesProvisionalTitle_AndDeletesIt()
    {
        _tmdb.Unavailable = UnavailableReason.Offline;
        _networkUp = false;
        _lib.File(@"Heat (1995).mkv");
        await Scan();
        await Run();
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM titles WHERE tmdb_id IS NULL"));

        _tmdb.Unavailable = null;
        _networkUp = true;
        KnowHeat();
        await WaitUntil(() => _connectivity.IsOnline);
        await Run();

        Assert.Equal("matched", Row("Heat (1995).mkv").Status);
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM titles WHERE tmdb_id IS NULL"));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM file_titles"));
    }

    [Fact]
    public async Task ConnectivityRestored_ProcessesOfflineFiles()
    {
        _tmdb.Unavailable = UnavailableReason.Offline;
        _networkUp = false;
        KnowHeat();
        _lib.File(@"Heat (1995).mkv");
        await Scan();
        _service.Start();
        await _service.WhenIdleAsync();
        Assert.Equal("offline", Row("Heat (1995).mkv").Status);

        _tmdb.Unavailable = null;
        _networkUp = true;

        await WaitUntil(() => Row("Heat (1995).mkv").Status == "matched");
        Assert.Contains(TimeSpan.FromSeconds(60), _clock.Delays);
        await WaitUntil(() => _hub.Current.PendingIdentification == 0);
    }

    [Fact]
    public async Task KeyRejected_PausesQueue_ResumesOnKeyChange()
    {
        _tmdb.Unavailable = UnavailableReason.KeyRejected;
        KnowHeat();
        _lib.File(@"Heat (1995).mkv");
        await Scan();
        _service.Start();
        await _service.WhenIdleAsync();
        Assert.Equal("offline", Row("Heat (1995).mkv").Status);

        // TMDB would answer now, but the key has not changed, so nothing is asked.
        _tmdb.Unavailable = null;
        await Run();
        _settings.Save(new AppSettings { TmdbKey = "first-key", CustomPlayerPath = "x" });
        await _service.WhenIdleAsync();
        Assert.Equal("offline", Row("Heat (1995).mkv").Status);
        Assert.Empty(_tmdb.Searches);

        _settings.Save(new AppSettings { TmdbKey = "second-key" });
        await _service.WhenIdleAsync();

        Assert.Equal("matched", Row("Heat (1995).mkv").Status);
    }

    [Fact]
    public async Task NoKey_WaitsForAKey()
    {
        _settings.Save(new AppSettings());
        _tmdb.Unavailable = UnavailableReason.NoKey;
        KnowHeat();
        _lib.File(@"Heat (1995).mkv");
        await Scan();
        _service.Start();
        await _service.WhenIdleAsync();
        Assert.Equal("offline", Row("Heat (1995).mkv").Status);

        _tmdb.Unavailable = null;
        _settings.Save(new AppSettings { TmdbKey = "new-key" });
        await _service.WhenIdleAsync();

        Assert.Equal("matched", Row("Heat (1995).mkv").Status);
    }

    [Fact]
    public async Task Rule_AppliesToSameShowFolderAndTitle_Only()
    {
        _lib.File(@"Lost\Season 1\Lost.S01E02.mkv");
        _lib.File(@"Other\Season 1\Lost.S01E02.mkv");
        _lib.File("Lost (2004).mkv");
        _lib.File(@"Lost\Lost (2004).mkv");
        await Scan();
        var showId = _titles.Upsert(ShowTitle(4607, "Lost", 2004, 1, 2));
        _titles.UpsertEpisodes(showId, Season(1, "Pilot", "Walkabout"));
        _matches.AddRule(_folder.Id, "Lost", "Lost", showId);

        await Run();

        var ruled = Row(@"Lost\Season 1\Lost.S01E02.mkv");
        Assert.Equal("matched", ruled.Status);
        Assert.Equal("rule", LinkSource(ruled.Id));
        Assert.Equal("Lost", LinkedTitleName(ruled.Id));
        Assert.Equal("Walkabout", Scalar<string>(
            "SELECT e.name FROM file_episodes fe JOIN episodes e ON e.id = fe.episode_id WHERE fe.file_id = @id",
            new { id = ruled.Id }));

        // The rule is for one show folder and one parsed title.
        foreach (var rel in new[] { @"Other\Season 1\Lost.S01E02.mkv", "Lost (2004).mkv", @"Lost\Lost (2004).mkv" })
        {
            var file = Row(rel);
            Assert.NotEqual("matched", file.Status);
            Assert.NotEqual("rule", LinkSource(file.Id));
        }

        // The stored show answered the rule: TMDB was only asked about the other three files.
        Assert.DoesNotContain(_tmdb.TitleFetches, f => f.TmdbId == 4607);
        Assert.Empty(_tmdb.SeasonFetches);
    }

    [Fact]
    public async Task Rule_FindsEpisodesByTitle_WhenTheNumberDisagreesOrIsMissing()
    {
        _tmdb.OnTitle(ShowTitle(615, "Futurama", 1999, 0, 1, 4, 5))
            .OnSeason(615, Season(0, "Futurama: The Lost Adventure"))
            .OnSeason(615, Season(1, "Space Pilot 3000"))
            .OnSeason(615, Season(4, "Roswell That Ends Well", "A Tale of Two Santas"))
            .OnSeason(615, Season(5, "Crimes of the Hot", "Kif Gets Knocked Up a Notch", "Obsoletely Fabulous"));
        _lib.File(@"Futurama\Season 04\S04E01 Kif Gets Knocked Up a Notch.mkv");
        _lib.File(@"Futurama\Season 04\S04E13 Obsoletely Fabulous.mkv");
        _lib.File(@"Futurama\Season 04\S04E02 A Tale of Two Santas.mkv");
        await Scan();
        var showId = _titles.Upsert(ShowTitle(615, "Futurama", 1999, 0, 1, 4, 5));
        _titles.UpsertEpisodes(showId, Season(4, "Roswell That Ends Well", "A Tale of Two Santas"));
        _matches.AddRule(_folder.Id, "Futurama", "Futurama", showId);

        await Run();

        string EpisodeOf(string rel) => Scalar<string>(
            "SELECT e.season || 'x' || e.number || ' ' || e.name FROM file_episodes fe JOIN episodes e ON e.id = fe.episode_id WHERE fe.file_id = @id",
            new { id = Row(rel).Id });
        Assert.All(_files.ByFolder(_folder.Id), f => Assert.Equal("matched", f.Status));
        Assert.Equal("5x2 Kif Gets Knocked Up a Notch", EpisodeOf(@"Futurama\Season 04\S04E01 Kif Gets Knocked Up a Notch.mkv"));
        Assert.Equal("5x3 Obsoletely Fabulous", EpisodeOf(@"Futurama\Season 04\S04E13 Obsoletely Fabulous.mkv"));
        Assert.Equal("4x2 A Tale of Two Santas", EpisodeOf(@"Futurama\Season 04\S04E02 A Tale of Two Santas.mkv"));
        // Each season once in the run; season 4 came from the stored episodes.
        Assert.Equal([(615, 0), (615, 1), (615, 5)], _tmdb.SeasonFetches.Order());
    }

    [Fact]
    public async Task Rule_TitleNamingNoEpisode_IsMatchedToTheShowWithoutAnEpisode()
    {
        _tmdb.OnTitle(ShowTitle(615, "Futurama", 1999, 0, 4, 5))
            .OnSeason(615, Season(0, "Futurama: The Lost Adventure"))
            .OnSeason(615, Season(4, "Roswell That Ends Well", "A Tale of Two Santas"))
            .OnSeason(615, Season(5, "Crimes of the Hot"));
        _lib.File(@"Futurama\Season 04\S04E01 Not An Episode Anywhere.mkv");
        await Scan();
        var showId = _titles.Upsert(ShowTitle(615, "Futurama", 1999, 0, 4, 5));
        _matches.AddRule(_folder.Id, "Futurama", "Futurama", showId);

        await Run();

        var file = Row(@"Futurama\Season 04\S04E01 Not An Episode Anywhere.mkv");
        Assert.Equal("matched", file.Status);
        Assert.Equal("Futurama", LinkedTitleName(file.Id));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM file_episodes WHERE file_id = @id", new { id = file.Id }));
    }

    [Theory]
    [InlineData("Old Working Name")] // the stored episode has another name
    [InlineData(null)] // the stored season lacks the episode
    public async Task Rule_StoredNameDisagreesOrEpisodeIsNew_RefreshesTheStaleStoredSeason(string? storedThirdName)
    {
        _tmdb.OnTitle(ShowTitle(615, "Futurama", 1999, 4))
            .OnSeason(615, Season(4, "Roswell That Ends Well", "A Tale of Two Santas", "Brand New One"));
        _lib.File(@"Futurama\Season 04\S04E03 Brand New One.mkv");
        await Scan();
        var showId = _titles.Upsert(ShowTitle(615, "Futurama", 1999, 4));
        string[] stored = storedThirdName is null
            ? ["Roswell That Ends Well", "A Tale of Two Santas"]
            : ["Roswell That Ends Well", "A Tale of Two Santas", storedThirdName];
        _titles.UpsertEpisodes(showId, Season(4, stored));
        _matches.AddRule(_folder.Id, "Futurama", "Futurama", showId);

        await Run();

        var file = Row(@"Futurama\Season 04\S04E03 Brand New One.mkv");
        Assert.Equal("matched", file.Status);
        Assert.Equal("rule", LinkSource(file.Id));
        Assert.Equal("Brand New One", Scalar<string>(
            "SELECT e.name FROM file_episodes fe JOIN episodes e ON e.id = fe.episode_id WHERE fe.file_id = @id",
            new { id = file.Id }));
    }

    [Fact]
    public async Task SegmentedTitles_ReadEachSeasonOncePerRun()
    {
        _tmdb.OnTitle(ShowTitle(387, "SpongeBob", 1999, 0, 4, 5, 6))
            .OnSeason(387, Season(0, "Christmas Who?"))
            .OnSeason(387, Season(4, "Rise and Shine", "Waiting", "Sing a Song of Patrick"))
            .OnSeason(387, Season(5, "Boat Smarts", "Fungus Among Us", "The Krusty Sponge"))
            .OnSeason(387, Season(6, "Krabby Road"));
        _lib.File(@"SpongeBob\Season 05\S05E01 Rise and Shine & Waiting & Fungus Among Us.mkv");
        _lib.File(@"SpongeBob\Season 05\S05E02 The Krusty Sponge & Sing a Song of Patrick.mkv");
        await Scan();
        var showId = _titles.Upsert(ShowTitle(387, "SpongeBob", 1999, 0, 4, 5, 6));
        _matches.AddRule(_folder.Id, "SpongeBob", "SpongeBob", showId);

        await Run();

        string EpisodesOf(string rel) => Scalar<string>(
            """
            SELECT group_concat(k, ' ; ') FROM (
                SELECT e.season || 'x' || e.number AS k FROM file_episodes fe JOIN episodes e ON e.id = fe.episode_id
                WHERE fe.file_id = @id ORDER BY e.season, e.number)
            """,
            new { id = Row(rel).Id });
        Assert.Equal("4x1 ; 4x2 ; 5x2", EpisodesOf(@"SpongeBob\Season 05\S05E01 Rise and Shine & Waiting & Fungus Among Us.mkv"));
        Assert.Equal("4x3 ; 5x3", EpisodesOf(@"SpongeBob\Season 05\S05E02 The Krusty Sponge & Sing a Song of Patrick.mkv"));
        Assert.Equal(_tmdb.SeasonFetches.Distinct().Order(), _tmdb.SeasonFetches.Order());
    }

    [Fact]
    public async Task OlderParserVersion_RedoesTheParse_AndRequeuesAllButUserAndRuleFiles()
    {
        const string southPark = @"South Park (1997)\Season 06\E01. Jared Has Aides (2002).mkv";
        const string lost = @"Lost\Season 1\Lost.S01E01.mkv";
        _lib.File(southPark);
        _lib.File(lost);
        await Scan();
        var sp = Row(southPark);
        var lostRow = Row(lost);
        var lostId = _titles.Upsert(ShowTitle(4607, "Lost", 2004, 1));
        _matches.SetUserMatch(lostRow.Id, lostId, [], null);

        // A row stored before AirYear: the air year was the show year, and the file went to review on it.
        using (var conn = _db.Open())
        {
            conn.Execute(
                "UPDATE files SET status = 'review', parsed_json = @json WHERE id = @id",
                new { id = sp.Id, json = """{"Kind":"Episode","Title":"South Park","Year":2002,"Season":6,"Episodes":[1],"EpisodeTitles":["Jared Has Aides"],"ShowFolder":"South Park (1997)","TmdbId":null,"ImdbId":null}""" });
            conn.Execute("DELETE FROM meta");
        }

        _tmdb.OnSearch("South Park", 1997, TitleKind.Show, ShowHit(2190, "South Park", 1997))
            .OnTitle(ShowTitle(2190, "South Park", 1997, 6))
            .OnSeason(2190, Season(6, "Jared Has Aides"));

        await Run();

        var redone = Row(southPark);
        Assert.Equal((1997, 2002), (redone.Parsed.Year, redone.Parsed.AirYear));
        Assert.Equal("matched", redone.Status);
        Assert.Equal("South Park", LinkedTitleName(redone.Id));
        Assert.Equal([("South Park", (int?)1997, TitleKind.Show)], _tmdb.Searches);

        // The user's match is left alone: not requeued, not asked about.
        Assert.Equal("matched", Row(lost).Status);
        Assert.Equal("user", LinkSource(lostRow.Id));
        Assert.Equal(PathParser.Version.ToString(CultureInfo.InvariantCulture),
            Scalar<string>("SELECT value FROM meta WHERE key = 'parser_version'"));
    }

    [Fact]
    public async Task RequeuedAutomaticMatch_KeepsItsTitleAndEpisodesWhileTmdbIsUnreachable_AndIsRedoneLater()
    {
        KnowLost();
        const string rel = @"Lost\Season 1\Lost.S01E02.mkv";
        _lib.File(rel);
        await Scan();
        await Run();
        var id = Row(rel).Id;
        using (var conn = _db.Open()) conn.Execute("DELETE FROM meta");
        _tmdb.Unavailable = UnavailableReason.Offline;
        _networkUp = false;

        await Run();

        // The library shows the same as before, but the file still waits to be identified again.
        Assert.Equal("pending", Row(rel).Status);
        Assert.Equal(4607, Scalar<int>(
            "SELECT t.tmdb_id FROM file_titles ft JOIN titles t ON t.id = ft.title_id WHERE ft.file_id = @id", new { id }));
        Assert.Equal("auto", LinkSource(id));
        Assert.Equal("Walkabout", Scalar<string>(
            "SELECT e.name FROM file_episodes fe JOIN episodes e ON e.id = fe.episode_id WHERE fe.file_id = @id", new { id }));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM titles WHERE tmdb_id IS NULL"));

        ClearProviderCalls();
        _tmdb.Unavailable = null;
        _networkUp = true;
        _service.Start();

        await WaitUntil(() => Row(rel).Status == "matched");
        Assert.Contains(_tmdb.Searches, s => s.Title == "Lost");
        Assert.Equal("Walkabout", Scalar<string>(
            "SELECT e.name FROM file_episodes fe JOIN episodes e ON e.id = fe.episode_id WHERE fe.file_id = @id", new { id }));
    }

    [Fact]
    public async Task OlderParserVersion_RequeuesAnAutomaticMatch_ButNotRuleOrNotMediaFiles()
    {
        KnowLost();
        _lib.File(@"Lost\Season 1\Lost.S01E01.mkv");
        _lib.File(@"Mystery\Season 1\Mystery.S01E01.mkv");
        _lib.File("Heat (1995).mkv");
        await Scan();
        var ruleShow = _titles.Upsert(ShowTitle(777, "Mystery", 2010, 1));
        _titles.UpsertEpisodes(ruleShow, Season(1, "First"));
        _matches.AddRule(_folder.Id, "Mystery", "Mystery", ruleShow);
        await Run();
        Assert.Equal("auto", LinkSource(Row(@"Lost\Season 1\Lost.S01E01.mkv").Id));
        Assert.Equal("rule", LinkSource(Row(@"Mystery\Season 1\Mystery.S01E01.mkv").Id));
        _matches.SetNotMedia(Row("Heat (1995).mkv").Id);
        // Without its rule, a requeued Mystery file would be searched for and end in review.
        using (var conn = _db.Open()) conn.Execute("DELETE FROM meta; DELETE FROM match_rules;");
        ClearProviderCalls();

        await Run();

        Assert.Equal(["Lost"], _tmdb.Searches.Select(s => s.Title).Distinct());
        Assert.Equal("matched", Row(@"Lost\Season 1\Lost.S01E01.mkv").Status);
        Assert.Equal("matched", Row(@"Mystery\Season 1\Mystery.S01E01.mkv").Status);
        Assert.Equal("rule", LinkSource(Row(@"Mystery\Season 1\Mystery.S01E01.mkv").Id));
        Assert.Equal("not_media", Row("Heat (1995).mkv").Status);
    }

    [Fact]
    public async Task SeasonsFetchedOncePerShow()
    {
        KnowLost();
        _lib.File(@"Lost\Season 1\Lost.S01E01.mkv");
        _lib.File(@"Lost\Season 1\Lost.S01E02.mkv");
        _lib.File(@"Lost\Season 2\Lost.S02E01.mkv");
        _lib.File(@"Lost\Season 2\Lost.S02E02.mkv");
        await Scan();

        await Run();

        Assert.All(_files.ByFolder(_folder.Id), f => Assert.Equal("matched", f.Status));
        Assert.Equal(new[] { (4607, 1), (4607, 2) }, _tmdb.SeasonFetches.Order().ToArray());
        Assert.Single(_tmdb.TitleFetches);
    }

    [Fact]
    public async Task ShowIsSearchedOnce_HoweverManyAirYearsItsEpisodesCarry()
    {
        _tmdb.OnSearch("South Park", 1997, TitleKind.Show, ShowHit(2190, "South Park", 1997))
            .OnTitle(ShowTitle(2190, "South Park", 1997, 6, 13))
            .OnSeason(2190, Season(6, "Jared Has Aides", "Asspen"))
            .OnSeason(2190, Season(13, "The Ring"));
        _lib.File(@"South Park (1997) BDRip-AVC\Season 06\E01. Jared Has Aides (2002) BDRip-AVC.mkv");
        _lib.File(@"South Park (1997) BDRip-AVC\Season 06\E02. Asspen (2002) BDRip-AVC.mkv");
        _lib.File(@"South Park (1997) BDRip-AVC\Season 13\E01. The Ring (2009) BDRip-AVC.mkv");
        await Scan();

        await Run();

        Assert.All(_files.ByFolder(_folder.Id), f => Assert.Equal("matched", f.Status));
        Assert.Equal([("South Park", (int?)1997, TitleKind.Show)], _tmdb.Searches);
    }

    [Fact]
    public async Task UserMatch_IsNeverReidentified_AndIsRestoredToMatched()
    {
        KnowHeat();
        _lib.File(@"Heat (1995).mkv");
        await Scan();
        var file = Row("Heat (1995).mkv");
        var titleId = _titles.Upsert(FilmTitle(111, "Chosen By Hand", 1990));
        _matches.SetMatch(file.Id, titleId, [], 1.0, "user");
        using (var conn = _db.Open())
            conn.Execute("UPDATE files SET status = 'pending' WHERE id = @id", new { id = file.Id });

        await Run();

        Assert.Equal("matched", Row("Heat (1995).mkv").Status);
        Assert.Equal("Chosen By Hand", LinkedTitleName(file.Id));
        Assert.Equal("user", LinkSource(file.Id));
        Assert.Empty(_tmdb.Searches);
        Assert.Empty(_tmdb.TitleFetches);
    }

    [Theory]
    [InlineData(UnavailableReason.Offline)]
    [InlineData(UnavailableReason.NoKey)]
    [InlineData(UnavailableReason.KeyRejected)]
    public async Task UserMatch_SurvivesARunThatStopsBeforeReachingIt(UnavailableReason reason)
    {
        _tmdb.Unavailable = reason;
        _lib.File(@"A\Lost.S01E01.mkv");
        _lib.File(@"B\Heat (1995).mkv");
        await Scan();
        var file = Row(@"B\Heat (1995).mkv");
        var titleId = _titles.Upsert(FilmTitle(111, "Chosen By Hand", 1990));
        _matches.SetMatch(file.Id, titleId, [], 1.0, "user");
        ResetToPending(file.Id);

        // The file in A stops the run, so the user's file in B is one of the remaining ones.
        await Run();

        Assert.Equal("offline", Row(@"A\Lost.S01E01.mkv").Status);
        Assert.Equal("matched", Row(@"B\Heat (1995).mkv").Status);
        Assert.Equal("user", LinkSource(file.Id));
        Assert.Equal("Chosen By Hand", LinkedTitleName(file.Id));
    }

    [Theory]
    [InlineData(UnavailableReason.Offline)]
    [InlineData(UnavailableReason.KeyRejected)]
    public async Task UserMatch_SurvivesARunThatStartsStopped(UnavailableReason reason)
    {
        _tmdb.Unavailable = reason;
        _networkUp = false;
        _lib.File(@"Heat (1995).mkv");
        _lib.File(@"Lost\Season 1\Lost.S01E01.mkv");
        await Scan();
        await Run(); // learns that TMDB cannot answer, so the next run starts stopped
        var file = Row("Heat (1995).mkv");
        var titleId = _titles.Upsert(FilmTitle(111, "Chosen By Hand", 1990));
        _matches.SetMatch(file.Id, titleId, [], 1.0, "user");
        ResetToPending(file.Id);

        await Run();

        Assert.Equal("matched", Row("Heat (1995).mkv").Status);
        Assert.Equal("user", LinkSource(file.Id));
        Assert.Equal("Chosen By Hand", LinkedTitleName(file.Id));
    }

    [Fact]
    public async Task AutoWrites_NeverReplaceAUserLink()
    {
        _lib.File(@"Heat (1995).mkv");
        await Scan();
        var file = Row("Heat (1995).mkv");
        var chosen = _titles.Upsert(FilmTitle(111, "Chosen By Hand", 1990));
        var other = _titles.Upsert(FilmTitle(949, "Heat", 1995));
        var provisional = _titles.UpsertProvisional("Heat", TitleKind.Movie, 1995);
        _matches.SetMatch(file.Id, chosen, [], 1.0, "user");

        Assert.False(_matches.SetMatch(file.Id, other, [], 0.9, "auto"));
        Assert.False(_matches.SetMatch(file.Id, other, [], 1.0, "rule"));
        Assert.False(_matches.SetMatch(file.Id, other, [], 1.0, "id_tag"));
        Assert.False(_matches.SetProvisional(file.Id, provisional));
        Assert.False(_matches.SetReview(file.Id, [FilmHit(949, "Heat", 1995)], provisional));

        Assert.Equal("user", LinkSource(file.Id));
        Assert.Equal("Chosen By Hand", LinkedTitleName(file.Id));
        Assert.Equal("matched", Scalar<string>("SELECT status FROM files WHERE id = @id", new { id = file.Id }));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM review_candidates"));

        // The user can still choose again.
        Assert.True(_matches.SetMatch(file.Id, other, [], 1.0, "user"));
        Assert.Equal("Heat", LinkedTitleName(file.Id));
    }

    private void ResetToPending(int fileId)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE files SET status = 'pending' WHERE id = @fileId", new { fileId });
    }

    [Fact]
    public async Task ErrorInsideARun_IsLogged_NotThrown()
    {
        _lib.File(@"Heat (1995).mkv");
        await Scan();
        _probe.Throw = true;

        await Run();

        Assert.Contains(_hub.Recent, e => e.Level == ActivityLevel.Error);
    }

    [Fact]
    public async Task RequestDuringARun_SchedulesOneMore()
    {
        KnowHeat();
        _lib.File(@"Heat (1995).mkv");
        await Scan();
        var gate = new TaskCompletionSource();
        _probe.Gate = gate.Task;

        var first = _service.RunPendingAsync(Ct);
        await WaitUntil(() => _probe.Paths.Count == 1);
        _lib.File(@"Heat (1995) 2.mkv");
        await Scan();
        var second = _service.RunPendingAsync(Ct);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.All(_files.ByFolder(_folder.Id), f => Assert.Equal("matched", f.Status));
    }

    [Fact]
    public async Task NotMedia_MarkedWhileTmdbIsAsked_IsKept()
    {
        KnowHeat();
        _lib.File("Heat (1995).mkv");
        await Scan();
        var id = Row("Heat (1995).mkv").Id;
        using var service = new IdentificationService(
            _db, new HookedProvider(_tmdb, () => _matches.SetNotMedia(id)), _probe, _connectivity, _settings, _hub, _clock);

        await service.RunPendingAsync(Ct);

        Assert.Equal("not_media", Row("Heat (1995).mkv").Status);
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM file_titles WHERE file_id = @id", new { id }));
    }

    [Fact]
    public async Task NotMedia_MarkedMidPass_IsKept_WhenTmdbGoesOffline()
    {
        _lib.File("Heat (1995).mkv");
        _lib.File(@"Lost\Season 1\Lost.S01E02.mkv");
        await Scan();
        var lost = Row(@"Lost\Season 1\Lost.S01E02.mkv").Id;
        using var service = new IdentificationService(
            _db,
            new HookedProvider(_tmdb, () =>
            {
                _matches.SetNotMedia(lost); // the user, while the pass asks TMDB about Heat
                throw new MetadataUnavailableException(UnavailableReason.Offline, "TMDB is unreachable.");
            }),
            _probe, _connectivity, _settings, _hub, _clock);

        await service.RunPendingAsync(Ct);

        Assert.Equal("not_media", Row(@"Lost\Season 1\Lost.S01E02.mkv").Status);
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM file_titles WHERE file_id = @lost", new { lost }));
    }

    [Fact]
    public async Task MatchStore_AutomaticWrites_SkipANotMediaFile()
    {
        _lib.File("Heat (1995).mkv");
        await Scan();
        var id = Row("Heat (1995).mkv").Id;
        var title = _titles.UpsertProvisional("Heat", TitleKind.Movie, 1995);
        _matches.SetNotMedia(id);

        Assert.False(_matches.SetMatch(id, title, [], 0.9, "auto"));
        Assert.False(_matches.SetProvisional(id, title));
        Assert.False(_matches.SetReview(id, [FilmHit(949, "Heat", 1995)]));
        _matches.RestoreMatched(id);

        Assert.Equal("not_media", Row("Heat (1995).mkv").Status);
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM file_titles WHERE file_id = @id", new { id }));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM review_candidates WHERE file_id = @id", new { id }));
    }

    [Fact]
    public async Task DisabledFolder_IsNotIdentified()
    {
        KnowHeat();
        _lib.File("Heat (1995).mkv");
        await Scan();
        new FolderStore(_db, _clock).SetEnabled(_folder.Id, false);

        await Run();

        Assert.Empty(_tmdb.Searches);
        Assert.Equal("pending", Row("Heat (1995).mkv").Status);
        Assert.Equal(0, _hub.Current.PendingIdentification);
    }

    [Fact]
    public async Task FolderRemovedMidPass_SkipsItsFilesQuietly()
    {
        KnowHeat();
        _lib.File("Heat (1995).mkv");
        _lib.File("Heat 2 (1996).mkv");
        _lib.File("Heat 3 (1997).mkv");
        await Scan();
        using var service = new IdentificationService(
            _db, new HookedProvider(_tmdb, () => new FolderStore(_db, _clock).Remove(_folder.Id)), _probe, _connectivity, _settings, _hub, _clock);

        await service.RunPendingAsync(Ct);

        Assert.DoesNotContain(_hub.Recent, e => e.Level == ActivityLevel.Error);
        Assert.Empty(_files.ByStatus("pending"));
        Assert.Equal(0, _hub.Current.PendingIdentification);
    }

    [Fact]
    public async Task FileWhosePathCannotBeResolvedMidPass_StillBringsThePendingCountToZero()
    {
        KnowHeat();
        _lib.File("Heat (1995).mkv");
        _lib.File("Heat 2 (1996).mkv");
        await Scan();
        using var other = new TempLibrary();
        var otherFolder = new FolderStore(_db, _clock).Add(other.Root);
        // The second file now belongs to another folder, but the pass still holds the row it read with the old
        // folder id, and that folder is gone: the file is active, yet its path can't be resolved.
        using var service = new IdentificationService(
            _db,
            new HookedProvider(_tmdb, () =>
            {
                using (var conn = _db.Open())
                    conn.Execute("UPDATE files SET folder_id = @other WHERE rel_path = 'Heat 2 (1996).mkv'", new { other = otherFolder.Id });
                new FolderStore(_db, _clock).Remove(_folder.Id);
            }),
            _probe, _connectivity, _settings, _hub, _clock);

        await service.RunPendingAsync(Ct);

        Assert.Equal(0, _hub.Current.PendingIdentification);
    }

    [Fact]
    public async Task HiddenPlaceholder_ItsFileStaysHidden_OnceIdentified()
    {
        _lib.File("Heat (1995).mkv");
        var placeholder = await OfflineFirst();
        new OverrideStore(_db).SetHidden(TargetKind.Title, placeholder, true);
        var id = Row("Heat (1995).mkv").Id;

        await OnlineAgain(KnowHeat);

        Assert.Equal("matched", Row("Heat (1995).mkv").Status);
        Assert.Contains(new OverrideStore(_db).All(), o => o is { Kind: TargetKind.File, Hidden: true } && o.TargetId == id);
    }

    [Fact]
    public async Task RenamedPlaceholder_NameMovesToTheTitle_WhenAllItsFilesGoThere()
    {
        _lib.File(@"Lost\Season 1\Lost.S01E01.mkv");
        _lib.File(@"Lost\Season 1\Lost.S01E02.mkv");
        var placeholder = await OfflineFirst();
        new OverrideStore(_db).SetDisplayName(TargetKind.Title, placeholder, "Lost (my copy)");

        await OnlineAgain(KnowLost);

        var show = _titles.ByTmdb(4607, TitleKind.Show)!;
        Assert.Contains(new OverrideStore(_db).All(),
            o => o.Kind == TargetKind.Title && o.TargetId == show.Id && o.DisplayName == "Lost (my copy)");
    }

    [Fact]
    public async Task RenamedPlaceholder_DoesNotReplaceTheTitlesOwnName()
    {
        _lib.File("Heat (1995).mkv");
        var placeholder = await OfflineFirst();
        new OverrideStore(_db).SetDisplayName(TargetKind.Title, placeholder, "Heat (placeholder)");
        var heat = _titles.Upsert(FilmTitle(949, "Heat", 1995));
        new OverrideStore(_db).SetDisplayName(TargetKind.Title, heat, "Heat (director's cut)");

        await OnlineAgain(KnowHeat);

        Assert.Contains(new OverrideStore(_db).All(),
            o => o.Kind == TargetKind.Title && o.TargetId == heat && o.DisplayName == "Heat (director's cut)");
    }

    /// <summary>Scans and runs offline, so every file lands on one placeholder title; returns its id.</summary>
    private async Task<int> OfflineFirst()
    {
        _tmdb.Unavailable = UnavailableReason.Offline;
        _networkUp = false;
        await Scan();
        await Run();
        return Scalar<int>("SELECT id FROM titles WHERE tmdb_id IS NULL");
    }

    private async Task OnlineAgain(Action know)
    {
        _tmdb.Unavailable = null;
        _networkUp = true;
        know();
        await WaitUntil(() => _connectivity.IsOnline);
        await Run();
    }

    /// <summary>Runs <c>onFirstCall</c> once, before the first search reaches the inner provider.</summary>
    private sealed class HookedProvider(IMetadataProvider inner, Action onFirstCall) : IMetadataProvider
    {
        private int _called;

        private void Hook()
        {
            if (Interlocked.Exchange(ref _called, 1) == 0) onFirstCall();
        }

        public Task<IReadOnlyList<SearchCandidate>> SearchAsync(string title, int? year, TitleKind kind, CancellationToken ct)
        {
            Hook();
            return inner.SearchAsync(title, year, kind, ct);
        }

        public Task<IReadOnlyList<SearchCandidate>> SearchAnyAsync(string query, CancellationToken ct)
        {
            Hook();
            return inner.SearchAnyAsync(query, ct);
        }

        public Task<(int TmdbId, TitleKind Kind)?> FindByImdbIdAsync(string imdbId, CancellationToken ct)
        {
            Hook();
            return inner.FindByImdbIdAsync(imdbId, ct);
        }

        public Task<TitleDetails> GetTitleAsync(int tmdbId, TitleKind kind, CancellationToken ct) => inner.GetTitleAsync(tmdbId, kind, ct);

        public Task<SeasonDetails> GetSeasonAsync(int showTmdbId, int season, CancellationToken ct) => inner.GetSeasonAsync(showTmdbId, season, ct);
    }

    private sealed class StubProbe : IDurationProbe
    {
        private readonly object _gate = new();
        private readonly List<string> _paths = [];

        public double? Result { get; set; } = 5400;

        public bool Throw { get; set; }

        public Task? Gate { get; set; }

        public List<string> Paths
        {
            get { lock (_gate) return [.. _paths]; }
        }

        public async Task<double?> GetDurationSecondsAsync(string path, CancellationToken ct)
        {
            lock (_gate) _paths.Add(path);
            if (Throw) throw new InvalidOperationException("probe broke");
            if (Gate is { } gate) await gate;
            return Result;
        }
    }
}
