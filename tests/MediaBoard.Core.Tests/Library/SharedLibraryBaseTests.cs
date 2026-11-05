using Dapper;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Library;

/// <summary>
/// The shared part of the snapshot, which the sharing server keeps between requests. A viewer's own data (progress,
/// the dismissed list) must never come from it, and it must never outlive a change.
/// </summary>
public sealed class SharedLibraryBaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-sharedbase-" + Guid.NewGuid().ToString("N"));
    private readonly TempLibrary _root = new();
    private readonly FakeClock _clock = new();
    private readonly LibraryDatabase _db;
    private readonly TitleStore _titles;
    private readonly MatchStore _matches;
    private readonly ProgressStore _progress;
    private readonly LibraryFolder _folder;
    private readonly LocalLibrary _owner;
    private readonly Viewer _sam;
    private readonly Viewer _ann;
    private int _tmdbId = 100;

    public SharedLibraryBaseTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _titles = new TitleStore(_db, _clock);
        _matches = new MatchStore(_db);
        _progress = new ProgressStore(_db, _clock);
        _folder = new FolderStore(_db, _clock).Add(_root.Root);
        _owner = new LocalLibrary(_db, new FakeMetadataProvider(), _ => RecycleResult.Recycled, _clock, _ => DriveType.Fixed, shareSnapshots: true);

        using var connection = _db.Open();
        connection.Execute("INSERT INTO users(name, role, created_at) VALUES ('Sam', 'viewer', '2026-01-01'), ('Ann', 'viewer', '2026-01-01')");
        _sam = new Viewer(connection.ExecuteScalar<int>("SELECT id FROM users WHERE name = 'Sam'"), null);
        _ann = new Viewer(connection.ExecuteScalar<int>("SELECT id FROM users WHERE name = 'Ann'"), null);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        _root.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(int Title, int File)> MovieAsync(string name)
    {
        var title = _titles.Upsert(new TitleDetails(_tmdbId++, TitleKind.Movie, name, 2020, null, "About.", 7.0, "/p.jpg", 100, []));
        _root.File($"{name}.mkv", 4096);
        await new LibraryScanner(_db, new ActivityHub(_clock), _clock).ScanAsync(_folder, Ct);
        var file = new FileStore(_db).ByFolder(_folder.Id).Single(r => r.RelPath == $"{name}.mkv");
        Assert.True(_matches.SetMatch(file.Id, title, [], 1.0, "auto"));
        return (title, file.Id);
    }

    [Fact]
    public async Task Progress_NeverComesFromTheSharedBase()
    {
        var (_, heat) = await MovieAsync("Heat");
        _ = _owner.ForViewer(_sam).ContinueWatching(10); // warms the shared base

        // No Changed is raised: only a base that held progress could show these now.
        _progress.Save(_sam.UserId, heat, 600, 6000);
        _progress.Save(_ann.UserId, heat, 3000, 6000);

        var sam = _owner.ForViewer(_sam).ContinueWatching(10).Single();
        var ann = _owner.ForViewer(_ann).ContinueWatching(10).Single();
        Assert.Equal(600, sam.PositionS);
        Assert.Equal(3000, ann.PositionS);
        Assert.Empty(_owner.ForViewer(new Viewer(_ann.UserId + 100, null)).ContinueWatching(10));
        Assert.All(LibraryBase.Read(_db).Files, file => Assert.Null(file.Progress));
    }

    [Fact]
    public async Task Dismissed_BelongsToTheViewerWhoDidIt()
    {
        var (title, heat) = await MovieAsync("Heat");
        _progress.Save(_sam.UserId, heat, 600, 6000);
        _progress.Save(_ann.UserId, heat, 600, 6000);

        _owner.ForViewer(_sam).DismissContinue(title);

        Assert.Empty(_owner.ForViewer(_sam).ContinueWatching(10));
        Assert.Single(_owner.ForViewer(_ann).ContinueWatching(10));
    }

    [Fact]
    public async Task AChangeTheLibraryReports_IsSeenAtOnce()
    {
        var (title, _) = await MovieAsync("Heat");
        Assert.Equal("Heat", Assert.Single(_owner.ForViewer(_sam).Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false)).Name);

        _owner.Rename(TargetKind.Title, title, "Heat (Director's Cut)");

        Assert.Equal("Heat (Director's Cut)", Assert.Single(_owner.ForViewer(_sam).Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false)).Name);
    }

    [Fact]
    public async Task AWriteNobodyReported_IsSeenOnceTheBaseIsTooOld()
    {
        await MovieAsync("Heat");
        Assert.Single(_owner.ForViewer(_sam).Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false));

        await MovieAsync("Ronin");
        Assert.Single(_owner.ForViewer(_sam).Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false));

        _clock.Advance(SharedLibraryBase.MaxAge + TimeSpan.FromSeconds(1));
        Assert.Equal(2, _owner.ForViewer(_sam).Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false).Count);
    }

    [Fact]
    public async Task TheOwnersLibrary_ReadsFreshEveryTime()
    {
        await MovieAsync("Heat");
        Assert.Single(_owner.Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false));

        await MovieAsync("Ronin");

        Assert.Equal(2, _owner.Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false).Count);
    }

    [Fact]
    public async Task ViewerAccess_ReadsTheSameBaseAsTheLists()
    {
        var (_, heat) = await MovieAsync("Heat");
        var access = new ViewerAccess(_db, _owner);
        Assert.NotNull(access.File(_sam, heat));

        var (hiddenTitle, hidden) = await MovieAsync("Ronin");
        Assert.NotNull(new ViewerAccess(_db).File(_sam, hidden));
        _owner.SetHidden(TargetKind.Title, hiddenTitle, true);

        Assert.Null(access.File(_sam, hidden));
        Assert.False(access.CanReachTitle(_sam, hiddenTitle));
    }

    [Fact]
    public async Task ASharedLibrary_AnswersAsAnUnsharedOneDoes_ForAViewerWithFewerFoldersAndHides()
    {
        var (heatTitle, heat) = await MovieAsync("Heat");
        var (ronin, roninFile) = await MovieAsync("Ronin");
        await MovieAsync("Collateral");
        _progress.Save(_sam.UserId, heat, 600, 6000);
        _progress.Save(_sam.UserId, roninFile, 100, 6000);
        _owner.SetHidden(TargetKind.Title, ronin, true);
        var unshared = new LocalLibrary(_db, new FakeMetadataProvider(), _ => RecycleResult.Recycled, _clock, _ => DriveType.Fixed);

        var shared = _owner.ForViewer(_sam);
        var plain = unshared.ForViewer(_sam);

        Assert.Equal(plain.Titles(TitleKind.Movie, TitleSort.Name, false), shared.Titles(TitleKind.Movie, TitleSort.Name, false));
        Assert.Equal(plain.ContinueWatching(10), shared.ContinueWatching(10));
        Assert.Equal(plain.RecentlyAdded(10), shared.RecentlyAdded(10));
        Assert.Equal(plain.Title(heatTitle)?.Name, shared.Title(heatTitle)?.Name);
        Assert.Null(shared.Title(ronin));
    }
}
