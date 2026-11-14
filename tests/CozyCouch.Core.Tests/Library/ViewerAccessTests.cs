using Dapper;
using CozyCouch.Core.Activity;
using CozyCouch.Core.Library;
using CozyCouch.Core.Metadata;
using CozyCouch.Core.Scanning;
using CozyCouch.Core.Storage;
using CozyCouch.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace CozyCouch.Core.Tests.Library;

public sealed class ViewerAccessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cozycouch-access-" + Guid.NewGuid().ToString("N"));
    private readonly TempLibrary _rootA = new();
    private readonly TempLibrary _rootB = new();
    private readonly TempLibrary _outside = new();
    private readonly FakeClock _clock = new();
    private readonly LibraryDatabase _db;
    private readonly TitleStore _titles;
    private readonly MatchStore _matches;
    private readonly ProgressStore _progress;
    private readonly FolderStore _folders;
    private readonly LibraryScanner _scanner;
    private readonly LibraryFolder _folderA;
    private readonly LibraryFolder _folderB;
    private readonly LocalLibrary _owner;
    private readonly ViewerAccess _access;
    private readonly Viewer _sam;
    private int _tmdbId = 100;

    public ViewerAccessTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _titles = new TitleStore(_db, _clock);
        _matches = new MatchStore(_db);
        _progress = new ProgressStore(_db, _clock);
        _scanner = new LibraryScanner(_db, new ActivityHub(_clock), _clock);
        _folders = new FolderStore(_db, _clock);
        _folderA = _folders.Add(_rootA.Root);
        _folderB = _folders.Add(_rootB.Root);
        _owner = new LocalLibrary(_db, new FakeMetadataProvider(), _ => RecycleResult.Recycled, _clock, _ => DriveType.Fixed);
        _access = new ViewerAccess(_db);

        using var connection = _db.Open();
        connection.Execute("INSERT INTO users(name, role, created_at) VALUES ('Sam', 'viewer', '2026-01-01')");
        var samId = connection.ExecuteScalar<int>("SELECT id FROM users WHERE name = 'Sam'");
        _sam = new Viewer(samId, new HashSet<int> { _folderA.Id });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        _rootA.Dispose();
        _rootB.Dispose();
        _outside.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LocalLibrary As(Viewer viewer) => _owner.ForViewer(viewer);

    private int Movie(string name, int tmdbId = 0) => _titles.Upsert(new TitleDetails(
        tmdbId == 0 ? _tmdbId++ : tmdbId, TitleKind.Movie, name, 2020, null, $"About {name}.", 7.0, "/p.jpg", 100, []));

    private int Show(string name, params int[] episodesPerSeason)
    {
        var id = _titles.Upsert(new TitleDetails(
            _tmdbId++, TitleKind.Show, name, 2015, null, "About.", 8.0, "/p.jpg", 45, [.. Enumerable.Range(1, episodesPerSeason.Length)]));
        for (var season = 1; season <= episodesPerSeason.Length; season++)
        {
            _titles.UpsertEpisodes(id, new SeasonDetails(
                season,
                Enumerable.Range(1, episodesPerSeason[season - 1]).Select(n => new EpisodeDetails(season, n, $"{name} {season}x{n}", 45, null)).ToList()));
        }

        return id;
    }

    private int EpisodeId(int titleId, int season, int number) => _titles.EpisodeIds(titleId, [(season, number)]).Single();

    private async Task<int> FileIn(LibraryFolder folder, TempLibrary root, string relPath, int titleId, params (int Season, int Number)[] episodes)
    {
        root.File(relPath, 4096);
        await _scanner.ScanAsync(folder, Ct);
        var file = new FileStore(_db).ByFolder(folder.Id).Single(r => r.RelPath == relPath);
        Assert.True(_matches.SetMatch(file.Id, titleId, _titles.EpisodeIds(titleId, episodes), 1.0, "auto"));
        return file.Id;
    }

    [Fact]
    public async Task File_InAllowedFolder_ReturnsWhatThePlayerNeeds()
    {
        var show = Show("Skippy", 2);
        var file = await FileIn(_folderA, _rootA, "Skippy.S01E02.mkv", show, (1, 2));
        _progress.Save(_sam.UserId, file, 60, 2700);

        var reachable = _access.File(_sam, file);

        Assert.NotNull(reachable);
        Assert.Equal(file, reachable.FileId);
        Assert.Equal(show, reachable.TitleId);
        Assert.Equal(Path.Combine(_rootA.Root, "Skippy.S01E02.mkv"), reachable.FullPath);
        Assert.Equal("Skippy.S01E02.mkv", reachable.FileName);
        Assert.Equal("Skippy", reachable.TitleName);
        Assert.Equal("S01E02", reachable.EpisodeLabel);
        Assert.Equal(2700, reachable.DurationS);
        Assert.False(reachable.Missing);
    }

    [Fact]
    public async Task FileBehindALink_IsGoneOverTheNetwork_AndStaysAtThisPC()
    {
        var movie = Movie("Heat");
        var kept = Movie("Ronin");
        var plain = await FileIn(_folderA, _rootA, "Ronin (1998).mkv", kept);
        _outside.File(@"Private\Heat (1995).mkv", 4096);
        _rootA.Junction("Linked", _outside.PathOf("Private"));
        await _scanner.ScanAsync(_folderA, Ct);
        var linked = new FileStore(_db).ByFolder(_folderA.Id).Single(r => r.RelPath.EndsWith("Heat (1995).mkv", StringComparison.Ordinal));
        Assert.True(_matches.SetMatch(linked.Id, movie, [], 1.0, "auto"));
        var ownDevice = new Viewer(Viewer.Owner.UserId, null, OverNetwork: true);

        Assert.Null(_access.File(_sam, linked.Id));
        Assert.Null(_access.File(ownDevice, linked.Id));
        Assert.False(_access.CanReachTitle(_sam, movie));
        Assert.DoesNotContain(As(_sam).Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false), t => t.TitleId == movie);
        Assert.NotNull(_access.File(_sam, plain));
        Assert.NotNull(_access.File(Viewer.Owner, linked.Id));
        Assert.Contains(_owner.Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false), t => t.TitleId == movie);
    }

    [Fact]
    public async Task AFolderSwappedForALink_IsRefusedWhileTheListIsStillKept()
    {
        var movie = Movie("Heat");
        var file = await FileIn(_folderA, _rootA, @"Films\Heat (1995).mkv", movie);
        _outside.File(@"Films\Heat (1995).mkv", 4096);
        var owner = new LocalLibrary(_db, new FakeMetadataProvider(), _ => RecycleResult.Recycled, _clock, _ => DriveType.Fixed, shareSnapshots: true);
        var library = owner.ForViewer(_sam);
        var access = new ViewerAccess(_db, owner);
        Assert.Contains(library.Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false), t => t.TitleId == movie);
        Assert.NotNull(access.File(_sam, file));

        Directory.Delete(_rootA.PathOf("Films"), recursive: true);
        _rootA.Junction("Films", _outside.PathOf("Films"));

        Assert.Null(access.File(_sam, file));
    }

    [Fact]
    public async Task FilesBehindLinks_CountsThemForTheOwner()
    {
        await FileIn(_folderA, _rootA, "Ronin (1998).mkv", Movie("Ronin"));
        _outside.File(@"Private\One.mkv", 4096);
        _outside.File(@"Private\Two.mkv", 4096);
        Assert.Equal(0, _owner.FilesBehindLinks());

        _rootA.Junction("Linked", _outside.PathOf("Private"));
        await _scanner.ScanAsync(_folderA, Ct);

        Assert.Equal(2, _owner.FilesBehindLinks());
    }

    [Fact]
    public async Task File_InForbiddenFolder_IsNull()
    {
        var movie = Movie("Heat");
        var file = await FileIn(_folderB, _rootB, "Heat (1995).mkv", movie);

        Assert.Null(_access.File(_sam, file));
        Assert.NotNull(_access.File(Viewer.Owner, file));
        Assert.False(_access.CanReachTitle(_sam, movie));
        Assert.Null(_access.File(_sam, 99999));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("season")]
    [InlineData("episode")]
    [InlineData("file")]
    [InlineData("folder")]
    public async Task File_HiddenAtAnyLevel_IsNullForViewer(string level)
    {
        var (show, file) = await HiddenAt(level);

        Assert.Null(_access.File(_sam, file));
        Assert.False(_access.CanReachTitle(_sam, show));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("season")]
    [InlineData("episode")]
    [InlineData("file")]
    [InlineData("folder")]
    public async Task File_HiddenAtAnyLevel_StillReachableForOwner(string level)
    {
        var (show, file) = await HiddenAt(level);

        Assert.NotNull(_access.File(Viewer.Owner, file));
        Assert.True(_access.CanReachTitle(Viewer.Owner, show));
        Assert.Contains(As(Viewer.Owner).Titles(TitleKind.Show, TitleSort.Name, includeHidden: true), c => c.TitleId == show);
    }

    private async Task<(int Show, int File)> HiddenAt(string level)
    {
        var show = Show("Skippy", 2);
        var file = await FileIn(_folderA, _rootA, "Skippy.S01E01.mkv", show, (1, 1));
        switch (level)
        {
            case "title": _owner.SetHidden(TargetKind.Title, show, true); break;
            case "season": _owner.SetHidden(TargetKind.Season, LibraryIds.Season(show, 1), true); break;
            case "episode": _owner.SetHidden(TargetKind.Episode, EpisodeId(show, 1, 1), true); break;
            case "file": _owner.SetHidden(TargetKind.File, file, true); break;
            case "folder": _owner.SetHidden(TargetKind.Folder, _folderA.Id, true); break;
            default: throw new ArgumentException(level, nameof(level));
        }

        return (show, file);
    }

    [Fact]
    public async Task File_NotMedia_IsNull()
    {
        var movie = Movie("Heat");
        var file = await FileIn(_folderA, _rootA, "Heat (1995).mkv", movie);
        _matches.SetNotMedia(file);

        Assert.Null(_access.File(_sam, file));
        Assert.Null(_access.File(Viewer.Owner, file));
    }

    [Fact]
    public async Task File_DisabledFolder_IsNull()
    {
        var movie = Movie("Heat");
        var file = await FileIn(_folderA, _rootA, "Heat (1995).mkv", movie);
        _folders.SetEnabled(_folderA.Id, false);

        Assert.Null(_access.File(_sam, file));
        Assert.Null(_access.File(Viewer.Owner, file));
        Assert.False(_access.CanReachTitle(_sam, movie));
    }

    [Fact]
    public async Task Title_AllFilesHidden_IsNullForViewer()
    {
        var show = Show("Skippy", 2);
        var e1 = await FileIn(_folderA, _rootA, "Skippy.S01E01.mkv", show, (1, 1));
        var e2 = await FileIn(_folderA, _rootA, "Skippy.S01E02.mkv", show, (1, 2));
        _owner.SetHidden(TargetKind.File, e1, true);
        Assert.NotNull(As(_sam).Title(show));

        _owner.SetHidden(TargetKind.File, e2, true);

        Assert.Null(As(_sam).Title(show));
        Assert.False(_access.CanReachTitle(_sam, show));
        Assert.NotNull(As(Viewer.Owner).Title(show));
    }

    [Fact]
    public async Task Title_Query_ReturnsNullWhenHiddenForViewer()
    {
        var movie = Movie("Heat");
        await FileIn(_folderA, _rootA, "Heat (1995).mkv", movie);
        _owner.SetHidden(TargetKind.Title, movie, true);

        Assert.Null(As(_sam).Title(movie));
        Assert.NotNull(As(Viewer.Owner).Title(movie));
        // includeHidden is the owner's wish; a viewer who can't see hidden items still doesn't get them.
        Assert.Empty(As(_sam).Titles(TitleKind.Movie, TitleSort.Name, includeHidden: true));
        Assert.Empty(As(_sam).SearchLocal("heat", 10));
        Assert.Empty(As(_sam).RecentlyAdded(10));
    }

    [Fact]
    public async Task Title_HiddenEpisode_IsNotListedForViewer()
    {
        var show = Show("Skippy", 2);
        await FileIn(_folderA, _rootA, "Skippy.S01E01.mkv", show, (1, 1));
        await FileIn(_folderA, _rootA, "Skippy.S01E02.mkv", show, (1, 2));
        _owner.SetHidden(TargetKind.Episode, EpisodeId(show, 1, 2), true);

        Assert.Equal([1], As(_sam).Title(show)!.Seasons.Single().Episodes.Select(e => e.Number));
        Assert.Equal([1, 2], As(Viewer.Owner).Title(show)!.Seasons.Single().Episodes.Select(e => e.Number));
    }

    [Fact]
    public async Task NextEpisode_SkipsHiddenEpisodeForViewer()
    {
        var show = Show("Skippy", 3);
        var e1 = await FileIn(_folderA, _rootA, "Skippy.S01E01.mkv", show, (1, 1));
        var e2 = await FileIn(_folderA, _rootA, "Skippy.S01E02.mkv", show, (1, 2));
        var e3 = await FileIn(_folderA, _rootA, "Skippy.S01E03.mkv", show, (1, 3));
        _owner.SetHidden(TargetKind.Episode, EpisodeId(show, 1, 2), true);
        _progress.SetWatched(_sam.UserId, e1, true);
        _progress.SetWatched(Viewer.Owner.UserId, e1, true);

        Assert.Equal(e3, As(_sam).NextEpisodeFileId(show));
        Assert.Equal(e2, As(Viewer.Owner).NextEpisodeFileId(show));
    }

    [Fact]
    public async Task Continue_LeavesOutHiddenSeasonForViewer()
    {
        var show = Show("Skippy", 1, 1);
        var s1 = await FileIn(_folderA, _rootA, "Skippy.S01E01.mkv", show, (1, 1));
        await FileIn(_folderA, _rootA, "Skippy.S02E01.mkv", show, (2, 1));
        _progress.Save(_sam.UserId, s1, 600, 2700);
        _progress.Save(Viewer.Owner.UserId, s1, 600, 2700);
        _owner.SetHidden(TargetKind.Season, LibraryIds.Season(show, 1), true);

        Assert.Empty(As(_sam).ContinueWatching(10));
        Assert.Equal(s1, Assert.Single(As(Viewer.Owner).ContinueWatching(10)).FileId);
    }

    [Fact]
    public async Task Search_ClearsLibraryTitleId_WhenViewerCantReach()
    {
        const int tmdbId = 438631;
        var movie = Movie("Heat", tmdbId);
        await FileIn(_folderA, _rootA, "Heat (1995).mkv", movie);
        var provider = new FakeMetadataProvider().OnSearchAny(
            "heat", new SearchCandidate(tmdbId, TitleKind.Movie, "Heat", null, 1995, 50, 8.1, "/o.jpg", null));
        using var connectivity = new Connectivity(_ => Task.FromResult(false), _clock, new ActivityHub(_clock));

        SearchService Service(Viewer viewer) => new(As(viewer), provider, connectivity, _titles, _access, viewer);

        Assert.Equal(movie, (await Service(_sam).SearchAsync("heat", Ct)).Online.Single().LibraryTitleId);

        _owner.SetHidden(TargetKind.Title, movie, true);

        Assert.Null((await Service(_sam).SearchAsync("heat", Ct)).Online.Single().LibraryTitleId);
        Assert.Equal(movie, (await Service(Viewer.Owner).SearchAsync("heat", Ct)).Online.Single().LibraryTitleId);
    }
}
