using System.Diagnostics;
using System.Globalization;
using Dapper;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;
using MediaBoard.Core.Probing;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Identification;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Library;

public sealed class LocalLibraryTests : IDisposable
{
    private readonly TempLibrary _lib = new();
    private readonly List<TempLibrary> _extraLibs = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-library-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly FakeMetadataProvider _tmdb = new();
    private readonly LibraryDatabase _db;
    private readonly FileStore _files;
    private readonly FolderStore _folders;
    private readonly TitleStore _titles;
    private readonly MatchStore _matches;
    private readonly LibraryScanner _scanner;
    private readonly LibraryFolder _folder;
    private readonly List<string> _recycled = [];
    private DriveType _driveType = DriveType.Fixed;
    private readonly LocalLibrary _library;
    private int _changed;

    public LocalLibraryTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _files = new FileStore(_db);
        _folders = new FolderStore(_db, _clock);
        _titles = new TitleStore(_db, _clock);
        _matches = new MatchStore(_db);
        _scanner = new LibraryScanner(_db, new ActivityHub(_clock), _clock);
        _folder = _folders.Add(_lib.Root);
        _library = new LocalLibrary(_db, _tmdb, Recycle, _clock, _ => _driveType);
        _library.Changed += () => _changed++;
    }

    public void Dispose()
    {
        // Pooled connections keep the file locked on Windows.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        foreach (var lib in _extraLibs) lib.Dispose();
        _lib.Dispose();
    }

    /// <summary>The injected Recycle Bin: records the call and does not touch the file.</summary>
    private RecycleResult Recycle(string path)
    {
        _recycled.Add(path);
        return RecycleResult.Recycled;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Seeding ----

    private Task<ScanResult> Scan() => _scanner.ScanAsync(_folder, Ct);

    private MediaFileRow Row(string relPath) => _files.ByFolder(_folder.Id).Single(r => r.RelPath == relPath);

    private int ShowTitle(string name, int tmdbId, params int[] episodesPerSeason)
    {
        var id = _titles.Upsert(new TitleDetails(
            tmdbId, TitleKind.Show, name, 2015, null, $"About {name}.", 8.5, $"/{tmdbId}.jpg", 45,
            episodesPerSeason.Select((_, i) => i + 1).ToList()));
        for (var s = 0; s < episodesPerSeason.Length; s++)
        {
            _titles.UpsertEpisodes(id, new SeasonDetails(
                s + 1,
                Enumerable.Range(1, episodesPerSeason[s])
                    .Select(n => new EpisodeDetails(s + 1, n, $"{name} episode {n}", 45, null)).ToList()));
        }

        return id;
    }

    private int MovieTitle(string name, int tmdbId, int year = 2020) =>
        _titles.Upsert(new TitleDetails(tmdbId, TitleKind.Movie, name, year, null, $"About {name}.", 7.0, $"/{tmdbId}.jpg", 100, []));

    /// <summary>Creates the file, scans, and links it to the title and the given (season, number) episodes.</summary>
    private async Task<int> Linked(string relPath, int titleId, params (int Season, int Number)[] episodes)
    {
        if (!File.Exists(_lib.PathOf(relPath)))
        {
            _lib.File(relPath, 4096);
            await Scan();
        }

        var file = Row(relPath);
        Assert.True(_matches.SetMatch(file.Id, titleId, _titles.EpisodeIds(titleId, episodes), 1.0, "auto"));
        return file.Id;
    }

    private void Sql(string sql, object? args = null)
    {
        using var conn = _db.Open();
        conn.Execute(sql, args);
    }

    private T Scalar<T>(string sql, object? args = null)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<T>(sql, args)!;
    }

    private static string Utc(int hour, int minute = 0) =>
        new DateTime(2026, 10, 27, hour, minute, 0, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private void Played(int fileId, double position, double duration, bool watched, int hour, int minute = 0) =>
        Sql(
            """
            INSERT INTO progress(file_id, position_s, duration_s, watched, last_played)
            VALUES (@fileId, @position, @duration, @watched, @at)
            ON CONFLICT(file_id) DO UPDATE SET position_s = excluded.position_s, duration_s = excluded.duration_s,
                watched = excluded.watched, last_played = excluded.last_played
            """,
            new { fileId, position, duration, watched = watched ? 1 : 0, at = Utc(hour, minute) });

    private void SeenAt(int fileId, int hour) =>
        Sql("UPDATE files SET first_seen = @at WHERE id = @fileId", new { fileId, at = Utc(hour) });

    // ---- Continue watching ----

    [Fact]
    public async Task ContinueWatching_InProgressAndNextEpisodes_ByLastPlayed()
    {
        var saul = ShowTitle("Better Call Saul", 60059, 4);
        var s1e1 = await Linked(@"Saul\Season 1\Saul.S01E01.mkv", saul, (1, 1));
        var s1e2 = await Linked(@"Saul\Season 1\Saul.S01E02.mkv", saul, (1, 2));
        var s1e3 = await Linked(@"Saul\Season 1\Saul.S01E03.mkv", saul, (1, 3));
        var s1e4 = await Linked(@"Saul\Season 1\Saul.S01E04.mkv", saul, (1, 4));
        Played(s1e1, 0, 2700, true, 8);
        Played(s1e2, 0, 2700, true, 9);
        Played(s1e3, 0, 2700, true, 10);

        var heat = await Linked(@"Heat (1995).mkv", MovieTitle("Heat", 949, 1995));
        Played(heat, 600, 7200, false, 11);

        var lost = ShowTitle("Lost", 4607, 2);
        await Linked(@"Lost\Season 1\Lost.S01E01.mkv", lost, (1, 1));
        var lostE2 = await Linked(@"Lost\Season 1\Lost.S01E02.mkv", lost, (1, 2));
        Played(lostE2, 300, 2700, false, 9, 30);

        // A row left by a player that opened and closed at once is not "in progress".
        var ghost = await Linked(@"Ghost.2001.mkv", MovieTitle("Ghost", 251, 2001));
        Played(ghost, 0.5, 6000, false, 12);

        // A finished movie has nothing after it.
        var klaus = await Linked(@"Klaus (2019).mkv", MovieTitle("Klaus", 508439, 2019));
        Played(klaus, 5000, 5400, true, 12, 30);

        var items = _library.ContinueWatching(10);

        Assert.Equal([heat, s1e4, lostE2], items.Select(i => i.FileId));
        var next = items[1];
        Assert.True(next.IsNext);
        Assert.Equal("S01E04", next.EpisodeLabel);
        Assert.Equal("Better Call Saul episode 4", next.EpisodeName);
        Assert.Equal("Better Call Saul", next.TitleName);
        Assert.Equal("/60059.jpg", next.PosterPath);
        Assert.Equal(0, next.PositionS);
        Assert.Equal(saul, next.TitleId);

        Assert.False(items[0].IsNext);
        Assert.Null(items[0].EpisodeLabel);
        Assert.Equal(600, items[0].PositionS);
        Assert.Equal(7200, items[0].DurationS);
        Assert.Equal("S01E02", items[2].EpisodeLabel);
        Assert.Equal(300, items[2].PositionS);

        Assert.Equal([heat, s1e4], _library.ContinueWatching(2).Select(i => i.FileId));
    }

    [Fact]
    public async Task ContinueWatching_AfterMultiEpisodeFile_ShowsNextAfterLastSegment()
    {
        var show = ShowTitle("SpongeBob SquarePants", 387, 5);
        var both = await Linked(@"SpongeBob\SpongeBob.S01E01-E02.mkv", show, (1, 1), (1, 2));
        var e3 = await Linked(@"SpongeBob\SpongeBob.S01E03.mkv", show, (1, 3));
        await Linked(@"SpongeBob\SpongeBob.S01E04.mkv", show, (1, 4));
        Played(both, 0, 1320, true, 10);

        var item = Assert.Single(_library.ContinueWatching(10));

        Assert.Equal(e3, item.FileId);
        Assert.Equal("S01E03", item.EpisodeLabel);
        Assert.True(item.IsNext);
    }

    [Fact]
    public async Task ContinueWatching_MultiEpisodeFileInProgress_LabelsTheRange()
    {
        var show = ShowTitle("SpongeBob SquarePants", 387, 5);
        var both = await Linked(@"SpongeBob\SpongeBob.S01E01-E02.mkv", show, (1, 1), (1, 2));
        Played(both, 400, 1320, false, 10);

        var item = Assert.Single(_library.ContinueWatching(10));

        Assert.Equal("S01E01-E02", item.EpisodeLabel);
        Assert.False(item.IsNext);
    }

    [Fact]
    public async Task ContinueWatching_FinishedLastEpisode_ShowsNothing()
    {
        var show = ShowTitle("Short Show", 1, 2);
        await Linked(@"Short\Short.S01E01.mkv", show, (1, 1));
        var last = await Linked(@"Short\Short.S01E02.mkv", show, (1, 2));
        Played(last, 0, 2000, true, 10);

        Assert.Empty(_library.ContinueWatching(10));
    }

    [Fact]
    public async Task ContinueWatching_SkipsHiddenAndWatchedNextEpisodes()
    {
        var show = ShowTitle("Skippy", 5, 4);
        var e1 = await Linked(@"Skippy\Skippy.S01E01.mkv", show, (1, 1));
        var e2 = await Linked(@"Skippy\Skippy.S01E02.mkv", show, (1, 2));
        var e3 = await Linked(@"Skippy\Skippy.S01E03.mkv", show, (1, 3));
        var e4 = await Linked(@"Skippy\Skippy.S01E04.mkv", show, (1, 4));
        Played(e1, 0, 2000, true, 10);
        Played(e3, 0, 2000, true, 8);
        _library.SetHidden(TargetKind.File, e2, true);

        Assert.Equal(e4, Assert.Single(_library.ContinueWatching(10)).FileId);

        _library.SetHidden(TargetKind.Title, show, true);
        Assert.Empty(_library.ContinueWatching(10));
    }

    // ---- Recently added ----

    [Fact]
    public async Task RecentlyAdded_SortsByFirstSeen()
    {
        var old = await Linked(@"Old.Film.2001.mkv", MovieTitle("Old Film", 1, 2001));
        var mid = await Linked(@"Mid.Film.2010.mkv", MovieTitle("Mid Film", 2, 2010));
        var fresh = await Linked(@"Fresh.Film.2024.mkv", MovieTitle("Fresh Film", 3, 2024));
        SeenAt(old, 8);
        SeenAt(mid, 12);
        SeenAt(fresh, 20);

        Assert.Equal(["Fresh Film", "Mid Film", "Old Film"], _library.RecentlyAdded(int.MaxValue).Select(c => c.Name));
        Assert.Equal(["Fresh Film", "Mid Film"], _library.RecentlyAdded(2).Select(c => c.Name));
        Assert.Equal(["Fresh Film", "Mid Film", "Old Film"], _library.Titles(TitleKind.Movie, TitleSort.DateAdded, false).Select(c => c.Name));
    }

    [Fact]
    public async Task RecentlyAdded_ShowUsesItsNewestFile()
    {
        var show = ShowTitle("Growing Show", 9, 3);
        var e1 = await Linked(@"Growing\Growing.S01E01.mkv", show, (1, 1));
        var e2 = await Linked(@"Growing\Growing.S01E02.mkv", show, (1, 2));
        var film = await Linked(@"Middle.Film.mkv", MovieTitle("Middle Film", 10));
        SeenAt(e1, 6);
        SeenAt(film, 12);
        SeenAt(e2, 18);

        Assert.Equal(["Growing Show", "Middle Film"], _library.RecentlyAdded(10).Select(c => c.Name));
    }

    // ---- Titles and pages ----

    [Fact]
    public async Task Titles_FilterByKindAndSort()
    {
        await Linked(@"b.mkv", MovieTitle("The Beta", 1, 2005));
        await Linked(@"a.mkv", MovieTitle("Alpha", 2, 2015));
        await Linked(@"c.mkv", MovieTitle("Gamma", 3));
        Sql("UPDATE titles SET year = NULL WHERE name = 'Gamma'");
        var show = ShowTitle("Some Show", 4, 1);
        await Linked(@"Show\Show.S01E01.mkv", show, (1, 1));

        Assert.Equal(["Alpha", "The Beta", "Gamma"], _library.Titles(TitleKind.Movie, TitleSort.Name, false).Select(c => c.Name));
        Assert.Equal(["Alpha", "The Beta", "Gamma"], _library.Titles(TitleKind.Movie, TitleSort.Year, false).Select(c => c.Name));
        Assert.Equal(["Some Show"], _library.Titles(TitleKind.Show, TitleSort.Name, false).Select(c => c.Name));
    }

    [Fact]
    public async Task Title_ListsSeasonsEpisodesAndFiles()
    {
        var show = ShowTitle("Lost", 4607, 3, 2);
        var e2 = await Linked(@"Lost\Season 1\Lost.S01E02.mkv", show, (1, 2));
        var s2e1 = await Linked(@"Lost\Season 2\Lost.S02E01.mkv", show, (2, 1));
        await Linked(@"Lost\Extras\Lost.Unknown.mkv", show);
        Played(e2, 321, 2700, false, 10);
        Played(s2e1, 0, 2700, true, 11);

        var view = _library.Title(show)!;

        Assert.Equal("Lost", view.Name);
        Assert.Equal(4607, view.TmdbId);
        Assert.False(view.Offline);
        Assert.Equal([1, 2], view.Seasons.Select(s => s.Number));
        Assert.Equal("Season 1", view.Seasons[0].Name);
        Assert.Equal(LibraryIds.Season(show, 1), view.Seasons[0].Id);
        var season1 = view.Seasons[0].Episodes;
        Assert.Equal(3, season1.Count);
        Assert.Equal(e2, season1[1].FileId);
        Assert.Equal(321, season1[1].PositionS);
        Assert.False(season1[1].Watched);
        Assert.Null(season1[0].FileId);
        Assert.True(view.Seasons[1].Episodes[0].Watched);
        Assert.Equal("Lost.Unknown", Assert.Single(view.Files).Name);
        Assert.Null(_library.Title(987654));
    }

    [Fact]
    public async Task Title_MovieListsItsFile()
    {
        var movie = MovieTitle("Heat", 949, 1995);
        var file = await Linked(@"Heat (1995).mkv", movie);
        Played(file, 100, 7000, false, 10);

        var view = _library.Title(movie)!;

        Assert.Empty(view.Seasons);
        var shown = Assert.Single(view.Files);
        Assert.Equal(file, shown.FileId);
        Assert.Equal(100, shown.PositionS);
    }

    // ---- Overrides ----

    [Fact]
    public async Task Rename_ChangesDisplayOnly_FileUntouched()
    {
        var movie = MovieTitle("Heat", 949, 1995);
        var file = await Linked(@"Heat (1995).mkv", movie);
        var path = _lib.PathOf(@"Heat (1995).mkv");
        var before = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path), Directory.GetFiles(_lib.Root));

        _library.Rename(TargetKind.Title, movie, "  My Heat  ");

        Assert.Equal("My Heat", _library.Titles(TitleKind.Movie, TitleSort.Name, false).Single().Name);
        Assert.Equal("My Heat", _library.Title(movie)!.Name);
        Assert.Equal("Heat (1995).mkv", Row(@"Heat (1995).mkv").RelPath);
        Assert.Equal(before.Item1, File.ReadAllBytes(path));
        Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(path));
        Assert.Equal(before.Item3, Directory.GetFiles(_lib.Root));
        Assert.Equal(file, Row(@"Heat (1995).mkv").Id);

        _library.Rename(TargetKind.Title, movie, null);
        Assert.Equal("Heat", _library.Title(movie)!.Name);
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM overrides"));
    }

    [Fact]
    public async Task Rename_AppliesToSeasonEpisodeAndFile()
    {
        var show = ShowTitle("Lost", 4607, 2);
        await Linked(@"Lost\Lost.S01E01.mkv", show, (1, 1));
        var loose = await Linked(@"Lost\Lost.Unknown.mkv", show);
        var episodeId = _library.Title(show)!.Seasons[0].Episodes[0].EpisodeId;

        _library.Rename(TargetKind.Season, LibraryIds.Season(show, 1), "The Island");
        _library.Rename(TargetKind.Episode, episodeId, "Pilot");
        _library.Rename(TargetKind.File, loose, "Cut footage");

        var view = _library.Title(show)!;
        Assert.Equal("The Island", view.Seasons[0].Name);
        Assert.Equal("Pilot", view.Seasons[0].Episodes[0].Name);
        Assert.Equal("Lost episode 2", view.Seasons[0].Episodes[1].Name);
        Assert.Equal("Cut footage", Assert.Single(view.Files).Name);
        Assert.Throws<ArgumentException>(() => _library.Rename(TargetKind.Folder, _folder.Id, "Nope"));
    }

    [Fact]
    public async Task Hide_ExcludesUntilIncludeHidden()
    {
        var keep = MovieTitle("Keep", 1);
        var hide = MovieTitle("Hide", 2);
        await Linked(@"keep.mkv", keep);
        await Linked(@"hide.mkv", hide);

        _library.SetHidden(TargetKind.Title, hide, true);

        Assert.Equal(["Keep"], _library.Titles(TitleKind.Movie, TitleSort.Name, false).Select(c => c.Name));
        var all = _library.Titles(TitleKind.Movie, TitleSort.Name, true);
        Assert.Equal(["Hide", "Keep"], all.Select(c => c.Name));
        Assert.True(all[0].Hidden);
        Assert.False(all[1].Hidden);
        Assert.Equal(["Keep"], _library.RecentlyAdded(10).Select(c => c.Name));
        Assert.Empty(_library.SearchLocal("hide", 10));
        Assert.True(_library.Title(hide)!.Hidden);

        _library.SetHidden(TargetKind.Title, hide, false);

        Assert.Equal(2, _library.Titles(TitleKind.Movie, TitleSort.Name, false).Count);
        Assert.Single(_library.SearchLocal("hide", 10));
    }

    [Fact]
    public async Task HideFolder_HidesItsTitles()
    {
        var other = new TempLibrary();
        _extraLibs.Add(other);
        var folderB = _folders.Add(other.Root);
        other.File(@"OnlyB.2020.mkv", 4096);
        other.File(@"Mixed\Mixed.S01E02.mkv", 4096);
        _lib.File(@"OnlyA.2020.mkv", 4096);
        _lib.File(@"Mixed\Mixed.S01E01.mkv", 4096);
        await Scan();
        await _scanner.ScanAsync(folderB, Ct);

        var onlyA = MovieTitle("Only A", 1);
        var onlyB = MovieTitle("Only B", 2);
        var mixed = ShowTitle("Mixed", 3, 2);
        _matches.SetMatch(Row(@"OnlyA.2020.mkv").Id, onlyA, [], 1.0, "auto");
        _matches.SetMatch(_files.ByFolder(folderB.Id).Single(f => f.RelPath == "OnlyB.2020.mkv").Id, onlyB, [], 1.0, "auto");
        _matches.SetMatch(Row(@"Mixed\Mixed.S01E01.mkv").Id, mixed, _titles.EpisodeIds(mixed, [(1, 1)]), 1.0, "auto");
        _matches.SetMatch(
            _files.ByFolder(folderB.Id).Single(f => f.RelPath == @"Mixed\Mixed.S01E02.mkv").Id, mixed,
            _titles.EpisodeIds(mixed, [(1, 2)]), 1.0, "auto");

        _library.SetHidden(TargetKind.Folder, folderB.Id, true);

        Assert.Equal(["Mixed", "Only A"], _library.Titles(TitleKind.Movie, TitleSort.Name, false)
            .Concat(_library.Titles(TitleKind.Show, TitleSort.Name, false)).Select(c => c.Name).Order());
        var withHidden = _library.Titles(TitleKind.Movie, TitleSort.Name, true);
        Assert.True(withHidden.Single(c => c.Name == "Only B").Hidden);
        Assert.False(withHidden.Single(c => c.Name == "Only A").Hidden);
        Assert.True(_library.Folders().Single(f => f.Id == folderB.Id).Hidden);
        Assert.False(_library.Folders().Single(f => f.Id == _folder.Id).Hidden);

        _library.SetHidden(TargetKind.Folder, folderB.Id, false);
        Assert.Equal(2, _library.Titles(TitleKind.Movie, TitleSort.Name, false).Count);
    }

    // ---- Matching ----

    [Fact]
    public async Task FixMatch_UpdatesAndAddsRule()
    {
        _lib.File(@"Mystery\Season 1\Mystery.S01E02.mkv", 4096);
        _lib.File(@"Mystery\Season 1\Mystery.S01E03.mkv", 4096);
        await Scan();
        var file = Row(@"Mystery\Season 1\Mystery.S01E02.mkv");
        Assert.Equal(MediaKind.Episode, file.Parsed.Kind);
        var provisional = _titles.UpsertProvisional("Mystery", TitleKind.Show, null);
        _matches.SetReview(file.Id, [new SearchCandidate(1, TitleKind.Show, "Wrong", null, 2001, 1, 5, null, null)], provisional);
        _tmdb.OnTitle(new TitleDetails(777, TitleKind.Show, "The Real Show", 2012, null, "Real.", 8.1, "/real.jpg", 40, [1]))
            .OnSeason(777, new SeasonDetails(1, [
                new EpisodeDetails(1, 1, "First", 40, null),
                new EpisodeDetails(1, 2, "Second", 40, null),
                new EpisodeDetails(1, 3, "Third", 40, null)]));
        var changedBefore = _changed;

        await _library.FixMatchAsync(file.Id, 777, TitleKind.Show, Ct);

        Assert.Equal("matched", Row(@"Mystery\Season 1\Mystery.S01E02.mkv").Status);
        Assert.Equal("user", Scalar<string>("SELECT source FROM file_titles WHERE file_id = @id", new { id = file.Id }));
        var real = _titles.ByTmdb(777, TitleKind.Show)!;
        Assert.Equal(real.Id, Scalar<int>("SELECT title_id FROM file_titles WHERE file_id = @id", new { id = file.Id }));
        Assert.Equal(
            "Second",
            Scalar<string>(
                "SELECT e.name FROM file_episodes fe JOIN episodes e ON e.id = fe.episode_id WHERE fe.file_id = @id", new { id = file.Id }));
        Assert.Equal(real.Id, _matches.FindRule(_folder.Id, "Mystery", "Mystery"));
        Assert.Null(_library.Title(provisional));
        Assert.Equal("The Real Show", _library.Title(real.Id)!.Name);
        Assert.Empty(_library.NeedsReview());
        Assert.True(_changed > changedBefore);
    }

    [Fact]
    public async Task FixMatch_RuleReachesReviewSiblings_OnTheNextIdentificationRun()
    {
        var rels = new[]
        {
            @"Mystery\Season 1\Mystery.S01E01.mkv", @"Mystery\Season 1\Mystery.S01E02.mkv", @"Mystery\Season 1\Mystery.S01E03.mkv",
        };
        foreach (var rel in rels) _lib.File(rel, 4096);
        _lib.File(@"Other\Season 1\Mystery.S01E01.mkv", 4096);
        _lib.File(@"Mystery\Mystery (2001).mkv", 4096);
        await Scan();
        var guesses = new[] { new SearchCandidate(1, TitleKind.Show, "Wrong", null, 2001, 1, 5, null, null) };
        foreach (var rel in rels.Append(@"Other\Season 1\Mystery.S01E01.mkv").Append(@"Mystery\Mystery (2001).mkv"))
            _matches.SetReview(Row(rel).Id, guesses, _titles.UpsertProvisional("Mystery", TitleKind.Show, null));
        _tmdb.OnTitle(new TitleDetails(777, TitleKind.Show, "The Real Show", 2012, null, "Real.", 8.1, "/real.jpg", 40, [1]))
            .OnSeason(777, new SeasonDetails(1, [
                new EpisodeDetails(1, 1, "First", 40, null),
                new EpisodeDetails(1, 2, "Second", 40, null),
                new EpisodeDetails(1, 3, "Third", 40, null)]));

        await _library.FixMatchAsync(Row(rels[0]).Id, 777, TitleKind.Show, Ct);

        Assert.Equal("matched", Row(rels[0]).Status);
        Assert.Equal("pending", Row(rels[1]).Status);
        Assert.Equal("pending", Row(rels[2]).Status);
        // Another show folder, and a loose file with the same title, are not covered by the rule.
        Assert.Equal("review", Row(@"Other\Season 1\Mystery.S01E01.mkv").Status);
        Assert.Equal("review", Row(@"Mystery\Mystery (2001).mkv").Status);

        _tmdb.Searches.Clear();
        _tmdb.TitleFetches.Clear();
        _tmdb.SeasonFetches.Clear();
        var settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        settings.Save(new AppSettings { TmdbKey = "key" });
        var hub = new ActivityHub(_clock);
        using var connectivity = new Connectivity(_ => Task.FromResult(true), _clock, hub);
        using var identification = new IdentificationService(_db, _tmdb, new NoProbe(), connectivity, settings, hub, _clock);
        await identification.RunPendingAsync(Ct);

        var realId = _titles.ByTmdb(777, TitleKind.Show)!.Id;
        foreach (var (rel, name) in new[] { (rels[1], "Second"), (rels[2], "Third") })
        {
            var id = Row(rel).Id;
            Assert.Equal("matched", Row(rel).Status);
            Assert.Equal("rule", Scalar<string>("SELECT source FROM file_titles WHERE file_id = @id", new { id }));
            Assert.Equal(realId, Scalar<int>("SELECT title_id FROM file_titles WHERE file_id = @id", new { id }));
            Assert.Equal(
                name,
                Scalar<string>(
                    "SELECT e.name FROM file_episodes fe JOIN episodes e ON e.id = fe.episode_id WHERE fe.file_id = @id", new { id }));
        }

        // The rule answered for them: TMDB was never searched for the show.
        Assert.Empty(_tmdb.Searches);
        Assert.Equal(3, _library.Title(realId)!.Seasons.Single().Episodes.Count(e => e.FileId is not null));
    }

    private sealed class NoProbe : IDurationProbe
    {
        public Task<double?> GetDurationSecondsAsync(string path, CancellationToken ct) => Task.FromResult<double?>(null);
    }

    [Fact]
    public async Task FixMatch_Movie_AddsNoRule()
    {
        _lib.File(@"Pack\Season 1\Pack.S01E01.mkv", 4096);
        await Scan();
        var file = Row(@"Pack\Season 1\Pack.S01E01.mkv");
        _tmdb.OnTitle(new TitleDetails(949, TitleKind.Movie, "Heat", 1995, null, null, 8, null, 170, []));

        await _library.FixMatchAsync(file.Id, 949, TitleKind.Movie, Ct);

        Assert.Null(_matches.FindRule(_folder.Id, "Pack", "Pack"));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM file_episodes"));
        Assert.Equal("Heat", Assert.Single(_library.Titles(TitleKind.Movie, TitleSort.Name, false)).Name);
    }

    [Theory]
    [InlineData(UnavailableReason.Offline)]
    [InlineData(UnavailableReason.NoKey)]
    [InlineData(UnavailableReason.KeyRejected)]
    public async Task FixMatch_WhenTmdbCannotAnswer_ThrowsAndStoresNothing(UnavailableReason reason)
    {
        _lib.File(@"Mystery\Mystery.S01E01.mkv", 4096);
        await Scan();
        var file = Row(@"Mystery\Mystery.S01E01.mkv");
        var provisional = _titles.UpsertProvisional("Mystery", TitleKind.Show, null);
        _matches.SetProvisional(file.Id, provisional);
        _tmdb.Unavailable = reason;
        var titlesBefore = Scalar<int>("SELECT COUNT(*) FROM titles");

        var error = await Assert.ThrowsAsync<MetadataUnavailableException>(
            () => _library.FixMatchAsync(file.Id, 777, TitleKind.Show, Ct));

        Assert.Equal(reason, error.Reason);
        Assert.Equal("offline", Row(@"Mystery\Mystery.S01E01.mkv").Status);
        Assert.Equal(provisional, Scalar<int>("SELECT title_id FROM file_titles WHERE file_id = @id", new { id = file.Id }));
        Assert.Equal(titlesBefore, Scalar<int>("SELECT COUNT(*) FROM titles"));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM match_rules"));
    }

    [Fact]
    public async Task FixMatch_UnknownFile_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _library.FixMatchAsync(12345, 1, TitleKind.Show, Ct));
    }

    [Fact]
    public async Task NotMedia_RemovesFromTitles()
    {
        var film = MovieTitle("Real Film", 1);
        await Linked(@"film.mkv", film);
        _lib.File(@"Extras\bonus.mkv", 4096);
        await Scan();
        var bonus = Row(@"Extras\bonus.mkv");
        var provisional = _titles.UpsertProvisional("bonus", TitleKind.Movie, null);
        _matches.SetProvisional(bonus.Id, provisional);
        Assert.Equal(2, _library.Titles(TitleKind.Movie, TitleSort.Name, false).Count);

        _library.MarkNotMedia(bonus.Id);

        Assert.Equal(["Real Film"], _library.Titles(TitleKind.Movie, TitleSort.Name, false).Select(c => c.Name));
        Assert.Equal("not_media", Row(@"Extras\bonus.mkv").Status);
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM titles WHERE id = @provisional", new { provisional }));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM file_titles WHERE file_id = @id", new { id = bonus.Id }));
        Assert.Empty(_library.NeedsReview());
    }

    [Fact]
    public async Task NotMedia_SurvivesARescanThatReparsesTheFile()
    {
        _lib.File(@"Show\Show_01_[1080p].mp4", 4096);
        await Scan();
        var file = Row(@"Show\Show_01_[1080p].mp4");
        _matches.SetProvisional(file.Id, _titles.UpsertProvisional("Show", TitleKind.Movie, null));
        _library.MarkNotMedia(file.Id);

        // Siblings arrive, so the bare number now reads as an episode and the parse changes.
        _lib.File(@"Show\Show_02_[1080p].mp4", 4096);
        _lib.File(@"Show\Show_03_[1080p].mp4", 4096);
        await Scan();

        Assert.Equal(MediaKind.Episode, Row(@"Show\Show_01_[1080p].mp4").Parsed.Kind);
        Assert.Equal("not_media", Row(@"Show\Show_01_[1080p].mp4").Status);
        Assert.Empty(_library.Titles(TitleKind.Movie, TitleSort.Name, false));
        Assert.Empty(_library.Titles(TitleKind.Show, TitleSort.Name, false));
    }

    [Fact]
    public async Task NotMedia_SurvivesAChangedFileThatReparsesDifferently()
    {
        _lib.File(@"Extras\bonus.mkv", 4096);
        await Scan();
        var file = Row(@"Extras\bonus.mkv");
        _library.MarkNotMedia(file.Id);
        Sql("UPDATE files SET parsed_json = '{\"stale\":true}' WHERE id = @id", new { id = file.Id });

        _lib.Resize(@"Extras\bonus.mkv", 8192);
        await Scan();

        var after = Row(@"Extras\bonus.mkv");
        Assert.Equal(8192, after.Size);
        Assert.Equal("bonus", after.Parsed.Title);
        Assert.Equal("not_media", after.Status);
        Assert.Empty(_library.Titles(TitleKind.Movie, TitleSort.Name, false));
    }

    [Fact]
    public async Task NotMedia_KeepsTheShowWhileItHasOtherFiles()
    {
        var show = ShowTitle("Lost", 4607, 2);
        await Linked(@"Lost\Lost.S01E01.mkv", show, (1, 1));
        var extra = await Linked(@"Lost\Lost.S01E02.mkv", show, (1, 2));

        _library.MarkNotMedia(extra);

        Assert.Equal(["Lost"], _library.Titles(TitleKind.Show, TitleSort.Name, false).Select(c => c.Name));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM file_titles WHERE title_id = @show", new { show }));
    }

    [Fact]
    public async Task ProvisionalTitles_AppearWithOfflineFlag()
    {
        _lib.File(@"Strange Film 2019.mkv", 4096);
        await Scan();
        var file = Row(@"Strange Film 2019.mkv");
        _matches.SetProvisional(file.Id, _titles.UpsertProvisional("Strange Film", TitleKind.Movie, 2019));
        await Linked(@"Heat (1995).mkv", MovieTitle("Heat", 949, 1995));

        var cards = _library.Titles(TitleKind.Movie, TitleSort.Name, false);

        var strange = cards.Single(c => c.Name == "Strange Film");
        Assert.True(strange.Offline);
        Assert.Null(strange.PosterPath);
        Assert.Equal(2019, strange.Year);
        Assert.False(cards.Single(c => c.Name == "Heat").Offline);
        Assert.True(_library.Title(strange.TitleId)!.Offline);
        Assert.Contains(_library.RecentlyAdded(10), c => c.Offline);
    }

    [Fact]
    public async Task MissingFile_ShowsMissing_KeepsProgress()
    {
        var show = ShowTitle("Lost", 4607, 2);
        var e1 = await Linked(@"Lost\Lost.S01E01.mkv", show, (1, 1));
        Played(e1, 300, 2700, false, 10);
        _lib.Delete(@"Lost\Lost.S01E01.mkv");
        await Scan();

        var episode = _library.Title(show)!.Seasons[0].Episodes[0];

        Assert.True(episode.Missing);
        Assert.Equal(e1, episode.FileId);
        Assert.Equal(300, episode.PositionS);
        Assert.True(Assert.Single(_library.Titles(TitleKind.Show, TitleSort.Name, false)).Missing);
        var item = Assert.Single(_library.ContinueWatching(10));
        Assert.True(item.Missing);
        Assert.Equal(300, item.PositionS);
        Assert.Null(_library.NextEpisodeFileId(show));
    }

    [Fact]
    public async Task NeedsReview_ListsReviewFilesWithCandidates()
    {
        _lib.File(@"Fargo Season 1\Fargo.S01E01.mkv", 4096);
        _lib.File(@"Other\Other.2020.mkv", 4096);
        await Scan();
        var fargo = Row(@"Fargo Season 1\Fargo.S01E01.mkv");
        var other = Row(@"Other\Other.2020.mkv");
        _matches.SetReview(fargo.Id, [
            new SearchCandidate(60622, TitleKind.Show, "Fargo", null, 2014, 9, 8.9, null, null),
            new SearchCandidate(275, TitleKind.Movie, "Fargo", null, 1996, 8, 8.1, null, null)],
            _titles.UpsertProvisional("Fargo", TitleKind.Show, null));
        _matches.SetReview(other.Id, [], _titles.UpsertProvisional("Other", TitleKind.Movie, 2020));

        var items = _library.NeedsReview();

        Assert.Equal(2, items.Count);
        var item = items.Single(i => i.FileId == fargo.Id);
        Assert.Equal("Fargo.S01E01.mkv", item.FileName);
        Assert.Equal("Fargo", item.ParsedTitle);
        Assert.Equal(MediaKind.Episode, item.Kind);
        Assert.Equal([60622, 275], item.Candidates.Select(c => c.TmdbId));
        Assert.Equal(TitleKind.Movie, item.Candidates[1].Kind);
        Assert.Empty(items.Single(i => i.FileId == other.Id).Candidates);

        _library.SetHidden(TargetKind.File, other.Id, true);
        Assert.Equal([fargo.Id], _library.NeedsReview().Select(i => i.FileId));
    }

    [Fact]
    public async Task NeedsReviewCount_MatchesNeedsReview_SkippingHiddenFilesAndFolders()
    {
        _lib.File(@"Fargo Season 1\Fargo.S01E01.mkv", 4096);
        _lib.File(@"Other\Other.2020.mkv", 4096);
        _lib.File(@"Third\Third.2021.mkv", 4096);
        await Scan();
        Assert.Equal(0, _library.NeedsReviewCount());
        foreach (var rel in new[] { @"Fargo Season 1\Fargo.S01E01.mkv", @"Other\Other.2020.mkv", @"Third\Third.2021.mkv" })
            _matches.SetReview(Row(rel).Id, [], _titles.UpsertProvisional("X", TitleKind.Movie, null));

        Assert.Equal(3, _library.NeedsReviewCount());

        _library.SetHidden(TargetKind.File, Row(@"Other\Other.2020.mkv").Id, true);
        Assert.Equal(2, _library.NeedsReviewCount());
        Assert.Equal(_library.NeedsReview().Count, _library.NeedsReviewCount());

        _library.SetHidden(TargetKind.Folder, _folder.Id, true);
        Assert.Equal(0, _library.NeedsReviewCount());
        Assert.Empty(_library.NeedsReview());
    }

    // ---- Folders ----

    [Fact]
    public async Task Folders_IncludeIgnoredFoldersAndUnavailable()
    {
        _lib.File(@"Movie.2020.mkv", 4096);
        _lib.File(@"GTA IV\setup.bin", 4096);
        var result = await Scan();
        var gone = _folders.Add(Path.Combine(_dir, "not-connected"));
        _library.RecordScan(_folder.Id, result);

        var rows = _library.Folders();

        var live = rows.Single(f => f.Id == _folder.Id);
        Assert.Equal(_lib.Root, live.Path);
        Assert.Equal(["GTA IV"], live.IgnoredFolders);
        Assert.Equal(1, live.FileCount);
        Assert.False(live.Unavailable);
        Assert.True(live.Enabled);
        Assert.True(rows.Single(f => f.Id == gone.Id).Unavailable);
        Assert.Empty(rows.Single(f => f.Id == gone.Id).IgnoredFolders);
    }

    // ---- Watched and next episode ----

    [Fact]
    public async Task SetWatched_MarksTheEpisodeAndUnwatchedResetsPosition()
    {
        var show = ShowTitle("Lost", 4607, 2);
        var e1 = await Linked(@"Lost\Lost.S01E01.mkv", show, (1, 1));
        Played(e1, 900, 2700, false, 10);
        var changedBefore = _changed;

        _library.SetWatched(e1, true);

        Assert.True(_library.Title(show)!.Seasons[0].Episodes[0].Watched);
        Assert.Equal(changedBefore + 1, _changed);

        _library.SetWatched(e1, false);

        var episode = _library.Title(show)!.Seasons[0].Episodes[0];
        Assert.False(episode.Watched);
        Assert.Equal(0, episode.PositionS);
        Assert.Equal(changedBefore + 2, _changed);
    }

    [Fact]
    public async Task NextEpisodeFileId_ResumesThenAdvances()
    {
        var show = ShowTitle("Lost", 4607, 3);
        var e1 = await Linked(@"Lost\Lost.S01E01.mkv", show, (1, 1));
        var e2 = await Linked(@"Lost\Lost.S01E02.mkv", show, (1, 2));
        var e3 = await Linked(@"Lost\Lost.S01E03.mkv", show, (1, 3));

        Assert.Equal(e1, _library.NextEpisodeFileId(show));

        Played(e1, 0, 2000, true, 9);
        Assert.Equal(e2, _library.NextEpisodeFileId(show));

        Played(e2, 700, 2000, false, 10);
        Assert.Equal(e2, _library.NextEpisodeFileId(show));

        Played(e2, 0, 2000, true, 10);
        Played(e3, 0, 2000, true, 11);
        Assert.Equal(e1, _library.NextEpisodeFileId(show));
    }

    [Fact]
    public async Task NextEpisodeFileId_MovieAndEmptyTitle()
    {
        var movie = MovieTitle("Heat", 949);
        var file = await Linked(@"Heat.mkv", movie);

        Assert.Equal(file, _library.NextEpisodeFileId(movie));
        Assert.Null(_library.NextEpisodeFileId(424242));
    }

    // ---- Search ----

    [Fact]
    public async Task SearchLocal_FuzzyAndInstant()
    {
        await Linked(@"saul.mkv", MovieTitle("Better Call Saul", 60059));
        await Linked(@"The Sopranos.mkv", MovieTitle("The Sopranos", 1398));
        await Linked(@"Klaus.mkv", MovieTitle("Klaus", 508439));
        SeedManyTitles(5000);

        // Warm-up: builds the in-memory title list.
        _library.SearchLocal("warm", 20);

        var stopwatch = Stopwatch.StartNew();
        var breaking = _library.SearchLocal("breaking", 20);
        var breakingTime = stopwatch.Elapsed;
        stopwatch.Restart();
        var saul = _library.SearchLocal("saul", 20);
        var saulTime = stopwatch.Elapsed;
        stopwatch.Restart();
        var typo = _library.SearchLocal("bettr call sul", 20);
        var typoTime = stopwatch.Elapsed;

        Assert.Empty(breaking);
        Assert.Equal("Better Call Saul", Assert.Single(saul).Name);
        Assert.Equal("Better Call Saul", Assert.Single(typo).Name);
        Assert.True(breakingTime < TimeSpan.FromMilliseconds(50), $"no-hit search took {breakingTime.TotalMilliseconds:F1} ms");
        Assert.True(saulTime < TimeSpan.FromMilliseconds(50), $"search took {saulTime.TotalMilliseconds:F1} ms");
        Assert.True(typoTime < TimeSpan.FromMilliseconds(50), $"typo search took {typoTime.TotalMilliseconds:F1} ms");
        Assert.Equal(5003, _library.Titles(TitleKind.Movie, TitleSort.Name, false).Count);
    }

    [Fact]
    public async Task SearchLocal_MatchesTokensPrefixesAndOrderAndLimit()
    {
        await Linked(@"a.mkv", MovieTitle("The Sopranos", 1));
        await Linked(@"b.mkv", MovieTitle("Spider-Man: Across the Spider-Verse", 2));
        await Linked(@"c.mkv", MovieTitle("Spider-Man", 3));
        await Linked(@"d.mkv", MovieTitle("Cloudy with a Chance of Meatballs", 4));

        Assert.Equal(["The Sopranos"], _library.SearchLocal("sopranos", 10).Select(c => c.Name));
        Assert.Equal(["The Sopranos"], _library.SearchLocal("the sop", 10).Select(c => c.Name));
        Assert.Equal(["Spider-Man", "Spider-Man: Across the Spider-Verse"], _library.SearchLocal("spider man", 10).Select(c => c.Name));
        Assert.Equal(["Cloudy with a Chance of Meatballs"], _library.SearchLocal("meatball chance", 10).Select(c => c.Name));
        Assert.Single(_library.SearchLocal("spider", 1));
        Assert.Empty(_library.SearchLocal("   ", 10));
        Assert.Empty(_library.SearchLocal("breaking", 10));
    }

    [Fact]
    public async Task SearchLocal_SeesRenamesAndOutsideChanges()
    {
        var movie = MovieTitle("Heat", 949);
        await Linked(@"heat.mkv", movie);
        Assert.Single(_library.SearchLocal("heat", 10));
        Assert.Empty(_library.SearchLocal("ice cream", 10));

        // The new name is found; the TMDB name still is, for a user who remembers the old one.
        _library.Rename(TargetKind.Title, movie, "Ice Cream");
        Assert.Equal("Ice Cream", Assert.Single(_library.SearchLocal("ice cream", 10)).Name);
        Assert.Equal("Ice Cream", Assert.Single(_library.SearchLocal("heat", 10)).Name);

        // A scan or identification run changed the data behind the library's back.
        var other = MovieTitle("Collateral", 1);
        await Linked(@"collateral.mkv", other);
        Assert.Empty(_library.SearchLocal("collateral", 10));
        var raised = 0;
        _library.Changed += () => raised++;
        _library.NotifyChanged();
        Assert.Equal(1, raised);
        Assert.Single(_library.SearchLocal("collateral", 10));
    }

    private void SeedManyTitles(int count)
    {
        string[] first = ["Quiet", "Golden", "Hidden", "Broken", "Crimson", "Silent", "Distant", "Frozen", "Hollow", "Velvet"];
        string[] second = ["River", "Harbor", "Garden", "Empire", "Winter", "Signal", "Island", "Tower", "Canyon", "Orchard"];
        string[] third = ["Rising", "Letters", "Hours", "Tales", "Season", "Dreams", "Stories", "Echoes", "Roads", "Gates"];
        var json = "{\"Kind\":\"Movie\",\"Title\":\"x\",\"Year\":null,\"Season\":null,\"Episodes\":[],\"EpisodeTitles\":[],\"ShowFolder\":null,\"TmdbId\":null,\"ImdbId\":null}";

        using var conn = _db.Open();
        using var transaction = conn.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            var name = $"{first[i % 10]} {second[i / 10 % 10]} {third[i / 100 % 10]} {i / 1000}";
            var titleId = conn.ExecuteScalar<long>(
                "INSERT INTO titles(tmdb_id, kind, name, year, poster_path) VALUES (@tmdb, 'movie', @name, 2000, '/x.jpg') RETURNING id",
                new { tmdb = 100_000 + i, name }, transaction);
            var fileId = conn.ExecuteScalar<long>(
                """
                INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status)
                VALUES (@folder, @rel, 1000, 0, @seen, 0, @json, 'matched') RETURNING id
                """,
                new { folder = _folder.Id, rel = $"bulk\\{i}.mkv", seen = Utc(1), json }, transaction);
            conn.Execute(
                "INSERT INTO file_titles(file_id, title_id, confidence, source) VALUES (@fileId, @titleId, 1.0, 'auto')",
                new { fileId, titleId }, transaction);
        }

        transaction.Commit();
    }

    // ---- Delete ----

    [Fact]
    public async Task PreviewDelete_ListsFiles()
    {
        var show = ShowTitle("Lost", 4607, 2);
        _lib.File(@"Lost\Lost.S01E01.mkv", 1000);
        _lib.File(@"Lost\Lost.S01E02.mkv", 2500);
        _lib.File(@"Lost\Lost.S01E03.mkv", 700);
        await Scan();
        var e1 = Row(@"Lost\Lost.S01E01.mkv");
        var e2 = Row(@"Lost\Lost.S01E02.mkv");
        var e3 = Row(@"Lost\Lost.S01E03.mkv");
        _matches.SetMatch(e1.Id, show, [], 1.0, "auto");
        _matches.SetMatch(e2.Id, show, [], 1.0, "auto");
        _matches.SetMatch(e3.Id, show, [], 1.0, "auto");
        _lib.Delete(@"Lost\Lost.S01E03.mkv");
        await Scan();

        var preview = _library.PreviewDelete(show);

        Assert.Equal("Lost", preview.TitleName);
        Assert.Equal(show, preview.TitleId);
        Assert.Equal([_lib.PathOf(@"Lost\Lost.S01E01.mkv"), _lib.PathOf(@"Lost\Lost.S01E02.mkv")], preview.Files.Select(f => f.Path));
        Assert.Equal(3500, preview.TotalSize);
        Assert.Empty(_recycled);
        Assert.Empty(_library.PreviewDelete(31337).Files);
    }

    [Fact]
    public async Task DeleteFromDisk_UsesRecycleBin()
    {
        var show = ShowTitle("Lost", 4607, 2);
        var e1 = await Linked(@"Lost\Lost.S01E01.mkv", show, (1, 1));
        var e2 = await Linked(@"Lost\Lost.S01E02.mkv", show, (1, 2));
        Played(e1, 300, 2700, false, 10);
        var preview = _library.PreviewDelete(show);
        var changedBefore = _changed;

        var result = await _library.DeleteFromDiskAsync(preview);

        var expected = new[] { _lib.PathOf(@"Lost\Lost.S01E01.mkv"), _lib.PathOf(@"Lost\Lost.S01E02.mkv") };
        Assert.Equal(expected, _recycled.Order());
        Assert.Equal(expected, result.Deleted.Order());
        Assert.Empty(result.Failed);
        // The injected action does not delete, so the files are still there: only the library forgot them.
        Assert.True(File.Exists(expected[0]));
        Assert.Null(_library.Title(show));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM files WHERE id IN (@e1, @e2)", new { e1, e2 }));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM progress"));
        Assert.True(_changed > changedBefore);
    }

    [Fact]
    public async Task DeleteFromDisk_FailedFileStaysInLibrary()
    {
        var movie = MovieTitle("Heat", 949);
        var good = await Linked(@"good.mkv", movie);
        var locked = await Linked(@"locked.mkv", movie);
        var library = new LocalLibrary(
            _db, _tmdb, path => path.EndsWith("locked.mkv") ? throw new IOException("In use.") : RecycleResult.Recycled, _clock);

        var result = await library.DeleteFromDiskAsync(library.PreviewDelete(movie));

        Assert.Equal([_lib.PathOf("good.mkv")], result.Deleted);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(_lib.PathOf("locked.mkv"), failure.Path);
        Assert.Contains("In use", failure.Reason);
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM files WHERE id = @good", new { good }));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM files WHERE id = @locked", new { locked }));
        Assert.NotNull(library.Title(movie));
    }

    [Fact]
    public async Task DeleteFromDisk_DeclinedFile_StaysInLibraryAndIsReported()
    {
        var movie = MovieTitle("Heat", 949);
        var small = await Linked(@"small.mkv", movie);
        var huge = await Linked(@"huge.mkv", movie);
        Played(huge, 100, 7000, false, 10);
        // Windows would delete this one for good (too big for the bin) and the answer to its question was no.
        var library = new LocalLibrary(
            _db, _tmdb, path => path.EndsWith("huge.mkv") ? RecycleResult.Declined : RecycleResult.Recycled, _clock);

        var result = await library.DeleteFromDiskAsync(library.PreviewDelete(movie));

        Assert.Equal([_lib.PathOf("small.mkv")], result.Deleted);
        var failure = Assert.Single(result.Failed);
        Assert.True(failure.Declined);
        Assert.Equal(_lib.PathOf("huge.mkv"), failure.Path);
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM files WHERE id = @small", new { small }));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM files WHERE id = @huge", new { huge }));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM progress WHERE file_id = @huge", new { huge }));
        Assert.NotNull(library.Title(movie));
    }

    [Fact]
    public async Task DeleteFromDisk_FailedOutcome_StaysInLibraryWithTheReason()
    {
        var movie = MovieTitle("Heat", 949);
        var file = await Linked(@"heat.mkv", movie);
        var library = new LocalLibrary(
            _db, _tmdb, _ => new RecycleResult(RecycleOutcome.Failed, "Windows could not move it."), _clock);

        var result = await library.DeleteFromDiskAsync(library.PreviewDelete(movie));

        Assert.Empty(result.Deleted);
        var failure = Assert.Single(result.Failed);
        Assert.False(failure.Declined);
        Assert.Equal("Windows could not move it.", failure.Reason);
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM files WHERE id = @file", new { file }));
    }

    [Fact]
    public async Task PreviewDelete_FlagsFilesNotOnAFixedDrive()
    {
        var movie = MovieTitle("Heat", 949);
        await Linked(@"heat.mkv", movie);

        var local = _library.PreviewDelete(movie);
        Assert.False(Assert.Single(local.Files).MayDeletePermanently);
        Assert.False(local.AnyMayDeletePermanently);

        _driveType = DriveType.Network;
        var network = _library.PreviewDelete(movie);
        Assert.True(Assert.Single(network.Files).MayDeletePermanently);
        Assert.True(network.AnyMayDeletePermanently);

        _driveType = DriveType.Removable;
        Assert.True(_library.PreviewDelete(movie).AnyMayDeletePermanently);
    }

    [Fact]
    public async Task DeleteFromDisk_NeverTouchesAFileOutsideTheLibraryFolders()
    {
        var movie = MovieTitle("Heat", 949);
        var inside = await Linked(@"inside.mkv", movie);
        var outsidePath = Path.Combine(Path.GetDirectoryName(_lib.Root)!, "outside.mkv");
        File.WriteAllText(outsidePath, "not part of the library");
        var escaping = await Linked(@"escape.mkv", movie);
        Sql("UPDATE files SET rel_path = @rel WHERE id = @escaping", new { rel = @"..\outside.mkv", escaping });

        var preview = _library.PreviewDelete(movie);
        Assert.Equal([inside], preview.Files.Select(f => f.FileId));

        // A hand-made preview that names the escaping file is refused as well.
        var forged = new DeletePreview(movie, "Heat", [new DeleteFile(escaping, outsidePath, 1), new DeleteFile(99999, outsidePath, 1)]);
        var result = await _library.DeleteFromDiskAsync(forged);

        Assert.Empty(_recycled);
        Assert.Empty(result.Deleted);
        Assert.Equal(2, result.Failed.Count);
        Assert.True(File.Exists(outsidePath));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM files WHERE id = @escaping", new { escaping }));
    }

    // ---- No writes into library folders ----

    [Fact]
    public async Task NoMethodWritesIntoLibraryFolder()
    {
        var show = ShowTitle("Lost", 4607, 2);
        var e1 = await Linked(@"Lost\Lost.S01E01.mkv", show, (1, 1));
        await Linked(@"Lost\Lost.S01E02.mkv", show, (1, 2));
        var movie = MovieTitle("Heat", 949, 1995);
        await Linked(@"Heat (1995).mkv", movie);
        _lib.File(@"Mystery\Mystery.S01E01.mkv", 4096);
        _lib.File(@"Junk\junk.mkv", 4096);
        await Scan();
        var mystery = Row(@"Mystery\Mystery.S01E01.mkv");
        var junk = Row(@"Junk\junk.mkv");
        _matches.SetReview(mystery.Id, [], _titles.UpsertProvisional("Mystery", TitleKind.Show, null));
        _matches.SetProvisional(junk.Id, _titles.UpsertProvisional("junk", TitleKind.Movie, null));
        _tmdb.OnTitle(new TitleDetails(777, TitleKind.Show, "Real", 2012, null, null, 8, null, 40, [1]))
            .OnSeason(777, new SeasonDetails(1, [new EpisodeDetails(1, 1, "First", 40, null)]));
        var before = Snapshot(_lib.Root);

        _ = _library.ContinueWatching(10);
        _ = _library.RecentlyAdded(10);
        _ = _library.Titles(TitleKind.Show, TitleSort.Name, true);
        _ = _library.Title(show);
        _ = _library.NeedsReview();
        _ = _library.Folders();
        _ = _library.SearchLocal("lost", 10);
        _ = _library.NextEpisodeFileId(show);
        _library.Rename(TargetKind.Title, show, "Renamed");
        _library.Rename(TargetKind.Season, LibraryIds.Season(show, 1), "Season Renamed");
        _library.Rename(TargetKind.Episode, _library.Title(show)!.Seasons[0].Episodes[0].EpisodeId, "Pilot");
        _library.Rename(TargetKind.File, e1, "Named file");
        _library.SetHidden(TargetKind.Title, movie, true);
        _library.SetHidden(TargetKind.Season, LibraryIds.Season(show, 1), true);
        _library.SetHidden(TargetKind.Episode, _library.Title(show)!.Seasons[0].Episodes[1].EpisodeId, true);
        _library.SetHidden(TargetKind.File, e1, true);
        _library.SetHidden(TargetKind.Folder, _folder.Id, true);
        _library.SetWatched(e1, true);
        _library.SetWatched(e1, false);
        await _library.FixMatchAsync(mystery.Id, 777, TitleKind.Show, Ct);
        _library.MarkNotMedia(junk.Id);
        _ = _library.PreviewDelete(show);
        _library.NotifyChanged();

        Assert.Equal(before, Snapshot(_lib.Root));
        Assert.Empty(_recycled);
    }

    private static List<string> Snapshot(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p =>
            {
                var info = new FileInfo(p);
                var isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
                return $"{Path.GetRelativePath(root, p)}|{info.Attributes}|{info.LastWriteTimeUtc.Ticks}|{(isDirectory ? 0 : info.Length)}";
            })
            .ToList();

    // ---- Changed event ----

    [Fact]
    public async Task Changed_FiresAfterEveryEdit()
    {
        var movie = MovieTitle("Heat", 949);
        var file = await Linked(@"heat.mkv", movie);
        _tmdb.OnTitle(new TitleDetails(950, TitleKind.Movie, "Heat 2", 2030, null, null, 5, null, 100, []));
        var start = _changed;

        _library.Rename(TargetKind.Title, movie, "A");
        _library.SetHidden(TargetKind.Title, movie, true);
        _library.SetWatched(file, true);
        await _library.FixMatchAsync(file, 950, TitleKind.Movie, Ct);
        _library.MarkNotMedia(file);

        Assert.Equal(start + 5, _changed);
    }
}
