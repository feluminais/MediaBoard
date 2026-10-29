using Dapper;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Parsing;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Scanning;

public sealed class LibraryScannerTests : IDisposable
{
    private readonly TempLibrary _lib = new();
    private readonly string _dbDir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly ClockStub _clock = new(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
    private readonly LibraryDatabase _db;
    private readonly ActivityHub _hub;
    private readonly FolderStore _folders;
    private readonly FileStore _files;
    private readonly LibraryScanner _scanner;
    private readonly LibraryFolder _folder;

    public LibraryScannerTests()
    {
        Directory.CreateDirectory(_dbDir);
        _db = new LibraryDatabase(Path.Combine(_dbDir, "library.db"));
        _hub = new ActivityHub(_clock);
        _folders = new FolderStore(_db, _clock);
        _files = new FileStore(_db);
        _scanner = new LibraryScanner(_db, _hub, _clock);
        _folder = _folders.Add(_lib.Root);
    }

    public void Dispose()
    {
        // Pooled connections keep the file locked on Windows.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, recursive: true);
        _lib.Dispose();
    }

    private Task<ScanResult> Scan() => _scanner.ScanAsync(_folder, CancellationToken.None);

    private MediaFileRow Row(string relPath) =>
        _files.ByFolder(_folder.Id).Single(r => r.RelPath == relPath);

    private void AddProgress(int fileId)
    {
        using var conn = _db.Open();
        conn.Execute(
            "INSERT INTO progress(file_id, position_s, duration_s, watched) VALUES (@fileId, 120, 600, 0)",
            new { fileId });
    }

    private double ProgressPosition(int fileId)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<double>("SELECT position_s FROM progress WHERE file_id = @fileId", new { fileId });
    }

    private void SetStatus(int id, string status)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE files SET status = @status WHERE id = @id", new { id, status });
    }

    [Fact]
    public async Task NewFiles_AreAdded_WithFirstSeen()
    {
        _lib.File(@"Movies\Heat.1995.1080p.mkv");
        _lib.File(@"Shows\Lost\Season 1\Lost.S01E01.mkv");

        var result = await Scan();

        Assert.Equal(2, result.Added);
        Assert.False(result.FolderUnavailable);
        var rows = _files.ByFolder(_folder.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(_clock.GetUtcNow().UtcDateTime, r.FirstSeen);
            Assert.Equal("pending", r.Status);
            Assert.False(r.Missing);
            Assert.Equal(TempLibrary.DefaultSize, r.Size);
        });
        var movie = Row(@"Movies\Heat.1995.1080p.mkv");
        Assert.Equal(MediaKind.Movie, movie.Parsed.Kind);
        Assert.Equal("Heat", movie.Parsed.Title);
        Assert.Equal(1995, movie.Parsed.Year);
        Assert.Equal(MediaKind.Episode, Row(@"Shows\Lost\Season 1\Lost.S01E01.mkv").Parsed.Kind);
        Assert.Equal(_lib.PathOf(@"Movies\Heat.1995.1080p.mkv"), _files.FullPath(movie));
        Assert.Equal(movie.Id, _files.Get(movie.Id)!.Id);
        Assert.Equal(2, _files.ByStatus("pending").Count);
        Assert.Empty(_files.ByStatus("matched"));
    }

    [Fact]
    public async Task UnchangedFiles_AreSkipped()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        await Scan();
        var before = Row(@"Movies\Heat.1995.mkv");

        var second = await Scan();

        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Changed);
        Assert.Equal(0, second.MarkedMissing);
        Assert.Equal(before.Id, Row(@"Movies\Heat.1995.mkv").Id);
    }

    [Fact]
    public async Task ChangedSize_IsReparsed()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        await Scan();
        var before = Row(@"Movies\Heat.1995.mkv");
        using (var conn = _db.Open())
            conn.Execute("UPDATE files SET status = 'matched', parsed_json = @Json WHERE id = @Id",
                new { before.Id, Json = "{\"stale\":true}" });

        _lib.Resize(@"Movies\Heat.1995.mkv", TempLibrary.DefaultSize + 1024);
        var result = await Scan();

        Assert.Equal(1, result.Changed);
        Assert.Equal(0, result.Added);
        var after = Row(@"Movies\Heat.1995.mkv");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(TempLibrary.DefaultSize + 1024, after.Size);
        Assert.Equal("Heat", after.Parsed.Title);
        // The parsed result changed (from the stale json), so the match is checked again.
        Assert.Equal("pending", after.Status);
    }

    [Fact]
    public async Task ChangedSize_WithSameParse_KeepsStatus()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        await Scan();
        SetStatus(Row(@"Movies\Heat.1995.mkv").Id, "matched");

        _lib.Resize(@"Movies\Heat.1995.mkv", TempLibrary.DefaultSize + 1024);
        var result = await Scan();

        Assert.Equal(1, result.Changed);
        Assert.Equal("matched", Row(@"Movies\Heat.1995.mkv").Status);
    }

    [Fact]
    public async Task DeletedFile_IsMarkedMissing_NotDeleted()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        await Scan();
        var id = Row(@"Movies\Heat.1995.mkv").Id;
        AddProgress(id);

        _lib.Delete(@"Movies\Heat.1995.mkv");
        var result = await Scan();

        Assert.Equal(1, result.MarkedMissing);
        Assert.True(Row(@"Movies\Heat.1995.mkv").Missing);
        Assert.Equal(120d, ProgressPosition(id));

        // A file that is still missing is not counted again.
        Assert.Equal(0, (await Scan()).MarkedMissing);
    }

    [Fact]
    public async Task ReturnedFile_IsRestored()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        await Scan();
        var id = Row(@"Movies\Heat.1995.mkv").Id;
        _lib.Delete(@"Movies\Heat.1995.mkv");
        await Scan();

        _lib.File(@"Movies\Heat.1995.mkv");
        var result = await Scan();

        Assert.Equal(1, result.Restored);
        Assert.Equal(0, result.Added);
        var row = Row(@"Movies\Heat.1995.mkv");
        Assert.Equal(id, row.Id);
        Assert.False(row.Missing);
    }

    [Fact]
    public async Task MovedFile_SameNameAndSize_IsRelinked()
    {
        _lib.File(@"Inbox\Heat.1995.mkv");
        await Scan();
        var id = Row(@"Inbox\Heat.1995.mkv").Id;
        AddProgress(id);

        _lib.Move(@"Inbox\Heat.1995.mkv", @"Movies\Heat.1995.mkv");
        var result = await Scan();

        Assert.Equal(1, result.Relinked);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.MarkedMissing);
        var row = Row(@"Movies\Heat.1995.mkv");
        Assert.Equal(id, row.Id);
        Assert.False(row.Missing);
        Assert.Single(_files.ByFolder(_folder.Id));
        Assert.Equal(120d, ProgressPosition(id));
        var entry = Assert.Single(_hub.Recent, e => e.Message.Contains("Relinked"));
        Assert.Equal(ActivityLevel.Info, entry.Level);
        Assert.Contains(@"Inbox\Heat.1995.mkv", entry.Message);
        Assert.Contains(@"Movies\Heat.1995.mkv", entry.Message);
    }

    [Fact]
    public async Task MovedFile_WithAmbiguousCandidates_IsNotRelinked()
    {
        _lib.File(@"Inbox\Heat.1995.mkv");
        await Scan();

        _lib.Delete(@"Inbox\Heat.1995.mkv");
        _lib.File(@"A\Heat.1995.mkv");
        _lib.File(@"B\Heat.1995.mkv");
        var result = await Scan();

        Assert.Equal(0, result.Relinked);
        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.MarkedMissing);
    }

    [Fact]
    public async Task BareNumberEpisodes_ArrivingOneByOne_BecomeEpisodes()
    {
        _lib.File(@"Show\Show_01_[1080p].mp4");
        await Scan();
        Assert.Equal(MediaKind.Movie, Row(@"Show\Show_01_[1080p].mp4").Parsed.Kind);

        _lib.File(@"Show\Show_02_[1080p].mp4");
        _lib.File(@"Show\Show_03_[1080p].mp4");
        var result = await Scan();

        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Changed);
        var rows = _files.ByFolder(_folder.Id);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(MediaKind.Episode, r.Parsed.Kind);
            Assert.Equal("pending", r.Status);
        });
        Assert.Equal([1, 2, 3], rows.Select(r => r.Parsed.Episodes.Single()).Order());
    }

    [Fact]
    public async Task DirectoryChange_KeepsStatusOfUserMatchedFile()
    {
        _lib.File(@"Show\Show_01_[1080p].mp4");
        await Scan();
        var id = Row(@"Show\Show_01_[1080p].mp4").Id;
        using (var conn = _db.Open())
        {
            conn.Execute("INSERT INTO titles(kind, name) VALUES ('movie', 'Show')");
            conn.Execute(
                "INSERT INTO file_titles(file_id, title_id, confidence, source) VALUES (@id, last_insert_rowid(), 1, 'user')",
                new { id });
        }
        SetStatus(id, "matched");

        _lib.File(@"Show\Show_02_[1080p].mp4");
        _lib.File(@"Show\Show_03_[1080p].mp4");
        await Scan();

        var matched = Row(@"Show\Show_01_[1080p].mp4");
        Assert.Equal(MediaKind.Episode, matched.Parsed.Kind);
        Assert.Equal("matched", matched.Status);
        Assert.Equal("pending", Row(@"Show\Show_02_[1080p].mp4").Status);
    }

    [Fact]
    public async Task DirectoryChange_KeepsNotMediaFileNotMedia()
    {
        _lib.File(@"Show\Show_01_[1080p].mp4");
        await Scan();
        var id = Row(@"Show\Show_01_[1080p].mp4").Id;
        SetStatus(id, "not_media");

        _lib.File(@"Show\Show_02_[1080p].mp4");
        _lib.File(@"Show\Show_03_[1080p].mp4");
        await Scan();

        var kept = Row(@"Show\Show_01_[1080p].mp4");
        Assert.Equal(MediaKind.Episode, kept.Parsed.Kind);
        Assert.Equal("not_media", kept.Status);
        Assert.Equal("pending", Row(@"Show\Show_02_[1080p].mp4").Status);
    }

    [Fact]
    public async Task ChangedSize_KeepsNotMediaFileNotMedia()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        await Scan();
        var before = Row(@"Movies\Heat.1995.mkv");
        using (var conn = _db.Open())
            conn.Execute("UPDATE files SET status = 'not_media', parsed_json = @Json WHERE id = @Id",
                new { before.Id, Json = "{\"stale\":true}" });

        _lib.Resize(@"Movies\Heat.1995.mkv", TempLibrary.DefaultSize + 1024);
        await Scan();

        var after = Row(@"Movies\Heat.1995.mkv");
        Assert.Equal("Heat", after.Parsed.Title);
        Assert.Equal("not_media", after.Status);
    }

    [Fact]
    public async Task UnchangedDirectories_AreNotReparsed()
    {
        _lib.File(@"Show\Show_01_[1080p].mp4");
        _lib.File(@"Other\Heat.1995.mkv");
        await Scan();
        var id = Row(@"Other\Heat.1995.mkv").Id;
        SetStatus(id, "matched");
        using (var conn = _db.Open())
            conn.Execute("UPDATE files SET parsed_json = '{}' WHERE id = @id", new { id });

        _lib.File(@"Show\Show_02_[1080p].mp4");
        await Scan();

        // Only the Show directory changed; the stale row in Other is left alone.
        using var check = _db.Open();
        Assert.Equal("{}", check.ExecuteScalar<string>("SELECT parsed_json FROM files WHERE id = @id", new { id }));
        Assert.Equal("matched", Row(@"Other\Heat.1995.mkv").Status);
    }

    [Fact]
    public async Task LargeBareNumberDirectory_ScansQuickly()
    {
        for (var i = 1; i <= 300; i++) _lib.File($@"Show\Show_{i:000}_.mp4");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var first = await Scan();
        var firstTime = watch.Elapsed;
        Assert.Equal(300, first.Added);

        _lib.File(@"Show\Show_301_.mp4");
        watch.Restart();
        var second = await Scan();
        var secondTime = watch.Elapsed;

        Assert.Equal(1, second.Added);
        Assert.True(firstTime < TimeSpan.FromSeconds(2), $"first scan took {firstTime}");
        Assert.True(secondTime < TimeSpan.FromSeconds(2), $"rescan took {secondTime}");
        Assert.All(_files.ByFolder(_folder.Id), r => Assert.Equal(MediaKind.Episode, r.Parsed.Kind));
    }

    [Fact]
    public async Task NonMediaFolder_IsReportedIgnored()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        _lib.File(@"Tools\setup.exe", 1024);
        _lib.File(@"Tools\data.bin", 1024);
        _lib.Folder("Empty");

        var result = await Scan();

        Assert.Equal(["Empty", "Tools"], result.IgnoredFolders);
        Assert.Equal(1, result.Added);
    }

    [Fact]
    public async Task PartialAndHiddenFiles_AreIgnored()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        _lib.File(@"Movies\Alien.1979.mkv.part");
        _lib.File(@"Movies\Heat.1995.sample.mkv", 10 * 1024 * 1024);
        _lib.File(@".cache\Ghost.2001.mkv");
        _lib.File(@"Movies\Hidden.2002.mkv");
        _lib.Hide(@"Movies\Hidden.2002.mkv");

        var result = await Scan();

        Assert.Equal(1, result.Added);
        Assert.Equal([@"Movies\Heat.1995.mkv"], _files.ByFolder(_folder.Id).Select(r => r.RelPath));
        Assert.DoesNotContain(".cache", result.IgnoredFolders);
    }

    [Fact]
    public async Task LockedFile_IsSkipped()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        var lockedPath = _lib.PathOf(@"Movies\Downloading.2020.mkv");
        using var held = new FileStream(lockedPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        held.SetLength(TempLibrary.DefaultSize);

        var result = await Scan();

        Assert.Equal([@"Movies\Downloading.2020.mkv"], result.SkippedLocked);
        Assert.Equal(1, result.Added);
        Assert.DoesNotContain(_files.ByFolder(_folder.Id), r => r.RelPath.Contains("Downloading"));

        held.Dispose();
        Assert.Equal(1, (await Scan()).Added);
    }

    [Fact]
    public async Task UnavailableFolder_MarksAllMissing()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        _lib.File(@"Movies\Alien.1979.mkv");
        await Scan();

        var gone = new LibraryFolder(_folder.Id, _lib.PathOf("Gone"), true);
        var result = await _scanner.ScanAsync(gone, CancellationToken.None);

        Assert.True(result.FolderUnavailable);
        Assert.Equal(2, result.MarkedMissing);
        var rows = _files.ByFolder(_folder.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Missing));
        Assert.Contains("Gone", _hub.Current.UnavailableFolders);
        Assert.Null(_hub.Current.ScanFolder);
    }

    [Fact]
    public async Task UnavailableFolder_IsClearedWhenBackOnline()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        await Scan();
        await _scanner.ScanAsync(new LibraryFolder(_folder.Id, _lib.PathOf("Gone"), true), CancellationToken.None);
        var other = _folders.Add(_lib.Folder("Other"));
        await _scanner.ScanAsync(new LibraryFolder(other.Id, _lib.PathOf("Away"), true), CancellationToken.None);
        Assert.Equal(["Away", "Gone"], _hub.Current.UnavailableFolders.Order());

        var back = await Scan();

        Assert.False(back.FolderUnavailable);
        Assert.Equal(1, back.Restored);
        Assert.Equal(["Away"], _hub.Current.UnavailableFolders);
        Assert.False(Row(@"Movies\Heat.1995.mkv").Missing);
    }

    [Fact]
    public async Task Forget_DropsAFolderFromTheUnavailableList()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        await Scan();
        await _scanner.ScanAsync(new LibraryFolder(_folder.Id, _lib.PathOf("Gone"), true), CancellationToken.None);
        Assert.Equal(["Gone"], _hub.Current.UnavailableFolders);

        _scanner.Forget(_folder.Id); // the folder was removed or switched off

        Assert.Empty(_hub.Current.UnavailableFolders);
        _scanner.Forget(_folder.Id); // forgetting twice is fine
    }

    [Fact]
    public async Task Scan_ReportsProgressToHub()
    {
        for (var i = 1; i <= 60; i++) _lib.File($@"Movies\Movie.{2000 + i}.mkv");
        var seen = new List<(string? Folder, int Done, int Total)>();
        _hub.StatusChanged += s => { lock (seen) seen.Add((s.ScanFolder, s.ScanDone, s.ScanTotal)); };

        await Scan();

        var name = Path.GetFileName(_lib.Root);
        var during = seen.Where(s => s.Folder is not null).ToList();
        Assert.All(during, s => Assert.Equal(name, s.Folder));
        var dones = during.Select(s => s.Done).ToList();
        Assert.Equal(dones.Order(), dones);
        Assert.Equal(60, dones[^1]);
        Assert.Equal(60, during[^1].Total);
        // Throttled: start, 25, 50 and the final count, not one update per file.
        Assert.True(dones.Count <= 5, $"too many updates: {dones.Count}");
        Assert.Null(seen[^1].Folder);
        Assert.Equal(0, seen[^1].Done);
    }

    [Fact]
    public async Task Scan_ReportsItsStateUnderTheFolderId()
    {
        for (var i = 1; i <= 30; i++) _lib.File($@"Movies\Movie.{2000 + i}.mkv");
        var seen = new List<FolderScan?>();
        _hub.StatusChanged += s => { lock (seen) seen.Add(s.ScanOf(_folder.Id)); };

        await Scan();

        Assert.Equal(new FolderScan(FolderScanState.Scanning, 0, 0), seen[0]); // listing: the total is not known yet
        Assert.Contains(new FolderScan(FolderScanState.Scanning, 25, 30), seen);
        Assert.Contains(new FolderScan(FolderScanState.Scanning, 30, 30), seen);
        Assert.Null(seen[^1]);
        Assert.Empty(_hub.Current.FolderScans);
    }

    [Fact]
    public async Task Scan_OfAnUnavailableFolder_EndsIdle()
    {
        await _scanner.ScanAsync(new LibraryFolder(_folder.Id, _lib.PathOf("Gone"), true), CancellationToken.None);

        Assert.Null(_hub.Current.ScanOf(_folder.Id));
    }

    [Fact]
    public async Task Remove_DeletesRowsNotFiles()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        await Scan();
        Assert.Single(_folders.All());

        _folders.Remove(_folder.Id);

        Assert.Empty(_folders.All());
        using var conn = _db.Open();
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM files"));
        Assert.True(File.Exists(_lib.PathOf(@"Movies\Heat.1995.mkv")));
    }

    [Fact]
    public async Task Repoint_KeepsFileIds()
    {
        _lib.File(@"Movies\Heat.1995.mkv");
        await Scan();
        var id = Row(@"Movies\Heat.1995.mkv").Id;

        using var moved = new TempLibrary();
        moved.File(@"Movies\Heat.1995.mkv");
        _folders.Repoint(_folder.Id, moved.Root);

        var repointed = Assert.Single(_folders.All());
        Assert.Equal(moved.Root, repointed.Path);
        Assert.Equal(id, Assert.Single(_files.ByFolder(_folder.Id)).Id);

        var result = await _scanner.ScanAsync(repointed, CancellationToken.None);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.MarkedMissing);
        Assert.Equal(moved.PathOf(@"Movies\Heat.1995.mkv"), _files.FullPath(_files.Get(id)!));
    }

    [Fact]
    public void SetEnabled_TogglesFolder()
    {
        _folders.SetEnabled(_folder.Id, false);
        Assert.False(Assert.Single(_folders.All()).Enabled);
        _folders.SetEnabled(_folder.Id, true);
        Assert.True(Assert.Single(_folders.All()).Enabled);
    }

    private sealed class ClockStub(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
