using Dapper;
using CozyCouch.Core.Activity;
using CozyCouch.Core.Library;
using CozyCouch.Core.Metadata;
using CozyCouch.Core.Scanning;
using CozyCouch.Core.Storage;
using CozyCouch.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace CozyCouch.Core.Tests.Library;

public sealed class ViewerScopeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cozycouch-viewers-" + Guid.NewGuid().ToString("N"));
    private readonly TempLibrary _rootA = new();
    private readonly TempLibrary _rootB = new();
    private readonly FakeClock _clock = new();
    private readonly LibraryDatabase _db;
    private readonly TitleStore _titles;
    private readonly MatchStore _matches;
    private readonly ProgressStore _progress;
    private readonly LibraryScanner _scanner;
    private readonly LibraryFolder _folderA;
    private readonly LibraryFolder _folderB;
    private readonly LocalLibrary _owner;
    private readonly Viewer _sam;
    private readonly Viewer _samEverywhere;
    private int _tmdbId = 100;

    public ViewerScopeTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _titles = new TitleStore(_db, _clock);
        _matches = new MatchStore(_db);
        _progress = new ProgressStore(_db, _clock);
        _scanner = new LibraryScanner(_db, new ActivityHub(_clock), _clock);
        var folders = new FolderStore(_db, _clock);
        _folderA = folders.Add(_rootA.Root);
        _folderB = folders.Add(_rootB.Root);
        _owner = new LocalLibrary(_db, new FakeMetadataProvider(), _ => RecycleResult.Recycled, _clock, _ => DriveType.Fixed);

        var samId = AddUser("Sam");
        _sam = new Viewer(samId, new HashSet<int> { _folderA.Id });
        _samEverywhere = new Viewer(samId, null);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        _rootA.Dispose();
        _rootB.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int AddUser(string name)
    {
        using var connection = _db.Open();
        connection.Execute("INSERT INTO users(name, role, created_at) VALUES (@name, 'viewer', '2026-01-01')", new { name });
        return connection.ExecuteScalar<int>("SELECT id FROM users WHERE name = @name", new { name });
    }

    private int NextTmdbId() => _tmdbId++;

    private int Movie(string name) => _titles.Upsert(new TitleDetails(
        NextTmdbId(), TitleKind.Movie, name, 2020, null, $"About {name}.", 7.0, "/p.jpg", 100, []));

    private int Show(string name, int episodes)
    {
        var id = _titles.Upsert(new TitleDetails(NextTmdbId(), TitleKind.Show, name, 2015, null, "About.", 8.0, "/p.jpg", 45, [1]));
        _titles.UpsertEpisodes(id, new SeasonDetails(
            1, Enumerable.Range(1, episodes).Select(n => new EpisodeDetails(1, n, $"{name} episode {n}", 45, null)).ToList()));
        return id;
    }

    /// <summary>Creates the file in the folder's root, scans the folder and links the file to the title.</summary>
    private async Task<int> FileIn(LibraryFolder folder, TempLibrary root, string relPath, int titleId, params (int Season, int Number)[] episodes)
    {
        root.File(relPath, 4096);
        await _scanner.ScanAsync(folder, Ct);
        var file = new FileStore(_db).ByFolder(folder.Id).Single(r => r.RelPath == relPath);
        Assert.True(_matches.SetMatch(file.Id, titleId, _titles.EpisodeIds(titleId, episodes), 1.0, "auto"));
        return file.Id;
    }

    private LocalLibrary As(Viewer viewer) => _owner.ForViewer(viewer);

    [Fact]
    public async Task TwoViewers_HaveOwnContinueWatching()
    {
        var heat = Movie("Heat");
        var file = await FileIn(_folderA, _rootA, "Heat (1995).mkv", heat);
        var kim = new Viewer(AddUser("Kim"), null);
        _progress.Save(Viewer.Owner.UserId, file, 600, 7200);
        _progress.Save(_samEverywhere.UserId, file, 100, 7200);

        Assert.Equal(600, Assert.Single(_owner.ContinueWatching(10)).PositionS);
        Assert.Equal(100, Assert.Single(As(_samEverywhere).ContinueWatching(10)).PositionS);
        Assert.Empty(As(kim).ContinueWatching(10));
    }

    [Fact]
    public async Task TwoViewers_HaveOwnWatchedState()
    {
        var show = Show("Skippy", 2);
        var e1 = await FileIn(_folderA, _rootA, "Skippy.S01E01.mkv", show, (1, 1));
        await FileIn(_folderA, _rootA, "Skippy.S01E02.mkv", show, (1, 2));

        As(_samEverywhere).SetWatched(e1, true);

        Assert.True(WatchedOf(As(_samEverywhere), show, e1));
        Assert.False(WatchedOf(_owner, show, e1));
        Assert.Single(_progress.WatchedEpisodeIds(_samEverywhere.UserId));
        Assert.Empty(_progress.WatchedEpisodeIds(Viewer.Owner.UserId));
        // Marking it as the owner does not touch Sam's row.
        _owner.SetWatched(e1, true);
        _owner.SetWatched(e1, false);
        Assert.True(WatchedOf(As(_samEverywhere), show, e1));
    }

    private static bool WatchedOf(LocalLibrary library, int titleId, int fileId) =>
        library.Title(titleId)!.Seasons.Single().Episodes.Single(e => e.FileId == fileId).Watched;

    [Fact]
    public async Task Dismiss_OnlyForThatViewer()
    {
        var heat = Movie("Heat");
        var file = await FileIn(_folderA, _rootA, "Heat (1995).mkv", heat);
        _progress.Save(Viewer.Owner.UserId, file, 600, 7200);
        _progress.Save(_samEverywhere.UserId, file, 600, 7200);

        As(_samEverywhere).DismissContinue(heat);

        Assert.Empty(As(_samEverywhere).ContinueWatching(10));
        Assert.Single(_owner.ContinueWatching(10));

        _owner.DismissContinue(heat);
        Assert.Empty(_owner.ContinueWatching(10));
    }

    [Theory]
    [InlineData("ContinueWatching")]
    [InlineData("RecentlyAdded")]
    [InlineData("Titles")]
    [InlineData("Title")]
    [InlineData("SearchLocal")]
    [InlineData("NextEpisodeFileId")]
    public async Task AllowedFolders_LimitEveryQuery(string query)
    {
        var here = Movie("Reachable Film");
        var there = Movie("Elsewhere Film");
        var hereFile = await FileIn(_folderA, _rootA, "Reachable Film (2020).mkv", here);
        var thereFile = await FileIn(_folderB, _rootB, "Elsewhere Film (2020).mkv", there);
        foreach (var userId in new[] { Viewer.Owner.UserId, _sam.UserId })
        {
            _progress.Save(userId, hereFile, 600, 7200);
            _progress.Save(userId, thereFile, 600, 7200);
        }

        // The owner goes first, so a search index built for the owner can't leak into Sam's copy.
        Assert.True(Reaches(query, _owner, here, "Reachable Film"));
        Assert.True(Reaches(query, _owner, there, "Elsewhere Film"));

        var limited = As(_sam);
        Assert.True(Reaches(query, limited, here, "Reachable Film"));
        Assert.False(Reaches(query, limited, there, "Elsewhere Film"));
    }

    private static bool Reaches(string query, LocalLibrary library, int titleId, string name) => query switch
    {
        "ContinueWatching" => library.ContinueWatching(50).Any(i => i.TitleId == titleId),
        "RecentlyAdded" => library.RecentlyAdded(50).Any(c => c.TitleId == titleId),
        "Titles" => library.Titles(TitleKind.Movie, TitleSort.Name, includeHidden: true).Any(c => c.TitleId == titleId),
        "Title" => library.Title(titleId) is not null,
        "SearchLocal" => library.SearchLocal(name, 50).Any(c => c.TitleId == titleId),
        "NextEpisodeFileId" => library.NextEpisodeFileId(titleId) is not null,
        _ => throw new ArgumentOutOfRangeException(nameof(query), query, null),
    };

    [Fact]
    public async Task TitleAcrossFolders_ShowsReachableFilesOnly()
    {
        var show = Show("Split Show", 3);
        var e1 = await FileIn(_folderA, _rootA, "Split.S01E01.mkv", show, (1, 1));
        var e2 = await FileIn(_folderB, _rootB, "Split.S01E02.mkv", show, (1, 2));
        var e3 = await FileIn(_folderA, _rootA, "Split.S01E03.mkv", show, (1, 3));

        var episodes = As(_sam).Title(show)!.Seasons.Single().Episodes;

        Assert.Equal([e1, null, e3], episodes.Select(e => e.FileId));
        Assert.Equal([e1, e2, e3], _owner.Title(show)!.Seasons.Single().Episodes.Select(e => e.FileId));

        // E1 is done; the next episode Sam can play is E3, not the E2 in a folder he can't reach.
        As(_sam).SetWatched(e1, true);
        Assert.Equal(e3, As(_sam).NextEpisodeFileId(show));
        Assert.Equal(e3, Assert.Single(As(_sam).ContinueWatching(10)).FileId);
    }

    [Fact]
    public async Task Owner_SeesEverythingAsBefore()
    {
        var here = Movie("Reachable Film");
        var there = Movie("Elsewhere Film");
        await FileIn(_folderA, _rootA, "Reachable Film (2020).mkv", here);
        await FileIn(_folderB, _rootB, "Elsewhere Film (2020).mkv", there);

        foreach (var library in new[] { _owner, As(Viewer.Owner) })
        {
            Assert.Equal(2, library.Titles(TitleKind.Movie, TitleSort.Name, includeHidden: true).Count);
            Assert.Equal([_folderA.Id, _folderB.Id], library.Folders().Select(f => f.Id));
            Assert.All(library.Folders(), f => Assert.Equal(1, f.FileCount));
        }

        // A viewer's folder list holds the folders they can reach, counted by what they can reach.
        var folder = Assert.Single(As(_sam).Folders());
        Assert.Equal(_folderA.Id, folder.Id);
        Assert.Equal(1, folder.FileCount);
        Assert.Equal(2, As(_samEverywhere).Folders().Count);
    }

    [Fact]
    public async Task NeedsReviewCount_CountsReachableFoldersOnly()
    {
        var movie = Movie("Anything");
        await FileIn(_folderA, _rootA, "A (2020).mkv", movie);
        await FileIn(_folderB, _rootB, "B (2020).mkv", movie);
        using (var connection = _db.Open()) connection.Execute("UPDATE files SET status = 'review'");

        Assert.Equal(2, _owner.NeedsReviewCount());
        Assert.Equal(1, As(_sam).NeedsReviewCount());
        Assert.Equal(2, As(_samEverywhere).NeedsReviewCount());
    }

    [Fact]
    public async Task SetWatched_OnAFileTheViewerCannotReach_IsRefusedLikeAnUnknownOne()
    {
        var there = Movie("Elsewhere Film");
        var file = await FileIn(_folderB, _rootB, "Elsewhere Film (2020).mkv", there);

        Assert.Throws<ArgumentException>(() => As(_sam).SetWatched(file, true));
        Assert.Null(_progress.Get(_sam.UserId, file));
    }

    [Fact]
    public async Task Changed_ReachesListenersOfTheOriginalLibrary()
    {
        var heat = Movie("Heat");
        await FileIn(_folderA, _rootA, "Heat (1995).mkv", heat);
        var raised = 0;
        _owner.Changed += () => raised++;

        As(_samEverywhere).Rename(TargetKind.Title, heat, "The Heat");

        Assert.Equal(1, raised);
    }
}
