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
    private readonly TempLibrary _rootB = new();
    private readonly FakeClock _clock = new();
    private readonly LibraryDatabase _db;
    private readonly TitleStore _titles;
    private readonly MatchStore _matches;
    private readonly ProgressStore _progress;
    private readonly FolderStore _folders;
    private readonly LibraryFolder _folder;
    private readonly LibraryFolder _folderB;
    private readonly LocalLibrary _owner;
    private readonly LocalLibrary _plain;
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
        _folders = new FolderStore(_db, _clock);
        _folder = _folders.Add(_root.Root);
        _folderB = _folders.Add(_rootB.Root);
        _owner = new LocalLibrary(_db, new FakeMetadataProvider(), _ => RecycleResult.Recycled, _clock, _ => DriveType.Fixed, shareSnapshots: true);
        _plain = new LocalLibrary(_db, new FakeMetadataProvider(), _ => RecycleResult.Recycled, _clock, _ => DriveType.Fixed);

        using var connection = _db.Open();
        connection.Execute("INSERT INTO users(name, role, created_at) VALUES ('Sam', 'viewer', '2026-01-01'), ('Ann', 'viewer', '2026-01-01')");
        // Sam may reach only the first folder; Ann reaches both.
        _sam = new Viewer(connection.ExecuteScalar<int>("SELECT id FROM users WHERE name = 'Sam'"), new HashSet<int> { _folder.Id });
        _ann = new Viewer(connection.ExecuteScalar<int>("SELECT id FROM users WHERE name = 'Ann'"), null);
    }

    public enum Hide { Title, Season, Episode, File, Folder, NotMedia, DisabledFolder }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        _root.Dispose();
        _rootB.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(int Title, int File)> MovieAsync(string name, bool inFolderB = false)
    {
        var title = _titles.Upsert(new TitleDetails(_tmdbId++, TitleKind.Movie, name, 2020, null, "About.", 7.0, "/p.jpg", 100, []));
        var (folder, root) = inFolderB ? (_folderB, _rootB) : (_folder, _root);
        root.File($"{name}.mkv", 4096);
        await new LibraryScanner(_db, new ActivityHub(_clock), _clock).ScanAsync(folder, Ct);
        var file = new FileStore(_db).ByFolder(folder.Id).Single(r => r.RelPath == $"{name}.mkv");
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
        Assert.All(_owner.SharedBase!.Get().Files, file => Assert.Null(file.Progress));
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
    public async Task AViewersProgress_LeavesTheBase_AndTellsOnlyThatAccount()
    {
        var (title, heat) = await MovieAsync("Heat");
        _progress.Save(_sam.UserId, heat, 600, 6000);
        var viewer = _owner.ForViewer(_sam);
        viewer.ContinueWatching(10);
        var kept = _owner.SharedBase!.Get();
        var changed = 0;
        var content = 0;
        var accounts = new List<int>();
        _owner.Changed += () => changed++;
        _owner.ContentChanged += () => content++;
        _owner.ProgressChanged += accounts.Add;

        viewer.DismissContinue(title);
        viewer.SetWatched(heat, watched: true);

        Assert.Same(kept, _owner.SharedBase.Get());
        Assert.Equal(0, changed);
        Assert.Equal(0, content);
        Assert.Equal([_sam.UserId, _sam.UserId], accounts);
    }

    [Fact]
    public async Task TheOwnersProgress_RefreshesThisPc_AndLeavesTheBase()
    {
        var (_, heat) = await MovieAsync("Heat");
        _owner.ForViewer(_sam).ContinueWatching(10);
        var kept = _owner.SharedBase!.Get();
        var changed = 0;
        var content = 0;
        _owner.Changed += () => changed++;
        _owner.ContentChanged += () => content++;

        _owner.SetWatched(heat, watched: true);

        Assert.Equal(1, changed);
        Assert.Equal(0, content);
        Assert.Same(kept, _owner.SharedBase.Get());
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
        var kept = _owner.SharedBase!.Get();
        await MovieAsync("Ronin");

        Assert.Same(kept, _owner.SharedBase.Get());
        Assert.Single(kept.Titles);
        Assert.Single(_owner.ForViewer(_sam).Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false));

        _clock.Advance(SharedLibraryBase.MaxAge + TimeSpan.FromSeconds(1));

        Assert.NotSame(kept, _owner.SharedBase.Get());
        Assert.Equal(2, _owner.SharedBase.Get().Titles.Count);
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
    public async Task ALoadThatOverlapsAWrite_IsNotKept()
    {
        await MovieAsync("Heat");
        SharedLibraryBase? shared = null;
        shared = new SharedLibraryBase(_db, _clock, db =>
        {
            var read = LibraryBase.Read(db);
            shared!.Invalidate(); // a Changed that lands while the read is running
            return read;
        });

        _ = shared.Get();

        Assert.False(shared.Holds);
    }

    [Fact]
    public async Task AnExpiredBase_IsLetGoOfOnTheNextLook()
    {
        await MovieAsync("Heat");
        _ = _owner.SharedBase!.Get();
        Assert.True(_owner.SharedBase.Holds);

        _clock.Advance(SharedLibraryBase.MaxAge + TimeSpan.FromSeconds(1));

        Assert.False(_owner.SharedBase.Holds);
    }

    [Fact]
    public async Task Forgetting_DropsTheBase()
    {
        await MovieAsync("Heat");
        _ = _owner.ForViewer(_sam).Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false);
        Assert.True(_owner.SharedBase!.Holds);

        _owner.ForgetSnapshot();

        Assert.False(_owner.SharedBase.Holds);
    }

    [Fact]
    public async Task TheSearchIndex_OfAViewer_ExpiresWithTheBase()
    {
        await MovieAsync("Heat");
        var viewer = _owner.ForViewer(_ann);
        Assert.Single(viewer.SearchLocal("heat", 10));

        await MovieAsync("Heat Two");
        Assert.Single(viewer.SearchLocal("heat", 10)); // inside the bound, and nothing reported the write
        _clock.Advance(SharedLibraryBase.MaxAge + TimeSpan.FromSeconds(1));

        Assert.Equal(2, viewer.SearchLocal("heat", 10).Count);
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
    public async Task AViewerLimitedToOneFolder_NeverSeesTheOther_OnTheSharedPath()
    {
        var (heatTitle, heat) = await MovieAsync("Heat");
        var (roninTitle, ronin) = await MovieAsync("Ronin", inFolderB: true);
        _progress.Save(_sam.UserId, heat, 600, 6000);
        _progress.Save(_ann.UserId, ronin, 600, 6000);
        var access = new ViewerAccess(_db, _owner);

        Assert.Equal(["Heat"], _owner.ForViewer(_sam).Titles(TitleKind.Movie, TitleSort.Name, false).Select(c => c.Name));
        Assert.Equal(["Heat", "Ronin"], _owner.ForViewer(_ann).Titles(TitleKind.Movie, TitleSort.Name, false).Select(c => c.Name).Order());
        Assert.NotNull(access.File(_sam, heat));
        Assert.Null(access.File(_sam, ronin));
        Assert.False(access.CanReachTitle(_sam, roninTitle));
        Assert.True(access.CanReachTitle(_ann, roninTitle));
        Assert.Null(_owner.ForViewer(_sam).Title(roninTitle));
        Assert.NotNull(_owner.ForViewer(_sam).Title(heatTitle));
        AssertSameAsUnshared([_sam, _ann], [heat, ronin]);
    }

    [Theory]
    [InlineData(Hide.Title)]
    [InlineData(Hide.Season)]
    [InlineData(Hide.Episode)]
    [InlineData(Hide.File)]
    [InlineData(Hide.Folder)]
    [InlineData(Hide.NotMedia)]
    [InlineData(Hide.DisabledFolder)]
    public async Task EveryKindOfHide_ReachesAViewerAtOnce_AsItDoesWithoutTheSharedBase(Hide hide)
    {
        var show = _titles.Upsert(new TitleDetails(_tmdbId++, TitleKind.Show, "Skippy", 2015, null, "About.", 8.0, "/p.jpg", 45, [1, 2]));
        foreach (var season in new[] { 1, 2 })
        {
            _titles.UpsertEpisodes(show, new SeasonDetails(
                season, [new EpisodeDetails(season, 1, "One", 45, null), new EpisodeDetails(season, 2, "Two", 45, null)]));
        }

        var files = new Dictionary<(int Season, int Number), int>();
        foreach (var key in new[] { (1, 1), (1, 2), (2, 1), (2, 2) })
        {
            var name = $"Skippy.S{key.Item1:D2}E{key.Item2:D2}.mkv";
            _root.File(name, 4096);
            await new LibraryScanner(_db, new ActivityHub(_clock), _clock).ScanAsync(_folder, Ct);
            var file = new FileStore(_db).ByFolder(_folder.Id).Single(r => r.RelPath == name).Id;
            Assert.True(_matches.SetMatch(file, show, _titles.EpisodeIds(show, [key]), 1.0, "auto"));
            files[key] = file;
        }

        var (_, movie) = await MovieAsync("Ronin");
        int[] all = [.. files.Values, movie];
        var viewers = new[] { _sam, _ann };
        AssertSameAsUnshared(viewers, all); // warms the base
        var access = new ViewerAccess(_db, _owner);
        Assert.All(all, file => Assert.NotNull(access.File(_sam, file)));

        int[] gone;
        switch (hide)
        {
            case Hide.Title:
                _owner.SetHidden(TargetKind.Title, show, true);
                gone = [.. files.Values];
                break;
            case Hide.Season:
                _owner.SetHidden(TargetKind.Season, LibraryIds.Season(show, 2), true);
                gone = [files[(2, 1)], files[(2, 2)]];
                break;
            case Hide.Episode:
                _owner.SetHidden(TargetKind.Episode, _titles.EpisodeIds(show, [(1, 2)]).Single(), true);
                gone = [files[(1, 2)]];
                break;
            case Hide.File:
                _owner.SetHidden(TargetKind.File, files[(1, 1)], true);
                gone = [files[(1, 1)]];
                break;
            case Hide.Folder:
                _owner.SetHidden(TargetKind.Folder, _folder.Id, true);
                gone = all;
                break;
            case Hide.NotMedia:
                _owner.MarkNotMedia(files[(1, 1)]);
                gone = [files[(1, 1)]];
                break;
            default:
                _folders.SetEnabled(_folder.Id, false);
                _owner.NotifyChanged();
                gone = all;
                break;
        }

        AssertSameAsUnshared(viewers, all);
        Assert.All(gone, file => Assert.Null(access.File(_sam, file)));
        Assert.All(gone, file => Assert.Null(access.File(_ann, file)));
        Assert.All(all.Except(gone), file => Assert.NotNull(access.File(_sam, file)));
    }

    private void AssertSameAsUnshared(Viewer[] viewers, IEnumerable<int> fileIds)
    {
        var sharedAccess = new ViewerAccess(_db, _owner);
        var plainAccess = new ViewerAccess(_db);
        foreach (var viewer in viewers)
        {
            var shared = _owner.ForViewer(viewer);
            var plain = _plain.ForViewer(viewer);
            foreach (var kind in new[] { TitleKind.Movie, TitleKind.Show })
            {
                var titles = plain.Titles(kind, TitleSort.Name, false);
                Assert.Equal(titles, shared.Titles(kind, TitleSort.Name, false));
                foreach (var card in titles)
                {
                    Assert.Equal(plain.Title(card.TitleId)?.Files.Count, shared.Title(card.TitleId)?.Files.Count);
                }
            }

            Assert.Equal(plain.ContinueWatching(10), shared.ContinueWatching(10));
            Assert.Equal(plain.RecentlyAdded(10), shared.RecentlyAdded(10));
            foreach (var file in fileIds)
            {
                Assert.Equal(plainAccess.File(viewer, file)?.FullPath, sharedAccess.File(viewer, file)?.FullPath);
            }
        }
    }
}
