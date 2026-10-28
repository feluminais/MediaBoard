using MediaBoard.Core.Activity;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Scanning;

public sealed class LibraryWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private readonly TempLibrary _lib = new();
    private readonly string _dbDir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly ActivityHub _hub = new();
    private readonly FolderStore _folders;
    private readonly FileStore _files;
    private readonly LibraryWatcher _watcher;
    private readonly List<int> _rescans = [];
    private TaskCompletionSource<int> _next = NewSignal();
    private int _throwOnce;

    public LibraryWatcherTests()
    {
        Directory.CreateDirectory(_dbDir);
        _db = new LibraryDatabase(Path.Combine(_dbDir, "library.db"));
        _folders = new FolderStore(_db);
        _files = new FileStore(_db);
        _watcher = new LibraryWatcher(new LibraryScanner(_db, _hub), _folders, _hub, Debounce);
        _watcher.Rescanned += OnRescanned;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        // Pooled connections keep the file locked on Windows.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, recursive: true);
        _lib.Dispose();
    }

    private static TaskCompletionSource<int> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void OnRescanned(int folderId)
    {
        TaskCompletionSource<int> signal;
        lock (_rescans)
        {
            _rescans.Add(folderId);
            signal = _next;
            _next = NewSignal();
        }

        signal.TrySetResult(folderId);
        if (Interlocked.Exchange(ref _throwOnce, 0) == 1) throw new InvalidOperationException("subscriber failed");
    }

    private Task<int> NextRescan()
    {
        lock (_rescans) return _next.Task;
    }

    private int RescanCount()
    {
        lock (_rescans) return _rescans.Count;
    }

    private static async Task<T> Within<T>(Task<T> task, TimeSpan limit)
    {
        var finished = await Task.WhenAny(task, Task.Delay(limit, TestContext.Current.CancellationToken));
        Assert.True(finished == task, $"Timed out after {limit.TotalSeconds:0.#} s.");
        return await task;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var stop = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < stop, "Condition not met before the deadline.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task NewFile_TriggersRescan_WithinFiveSeconds()
    {
        var folder = _folders.Add(_lib.Root);
        _watcher.Start();
        var rescan = NextRescan();

        _lib.File(@"Movies\Heat.1995.1080p.mkv");

        Assert.Equal(folder.Id, await Within(rescan, Deadline));
        Assert.Equal(@"Movies\Heat.1995.1080p.mkv", _files.ByFolder(folder.Id).Single().RelPath);
    }

    [Fact]
    public async Task PartialDownloadsHiddenAndNonVideoFiles_DoNotTriggerRescans()
    {
        _folders.Add(_lib.Root);
        _lib.Folder("Movies");
        _lib.Folder(".sync");
        _watcher.Start();

        _lib.File(@"Movies\Heat.1995.mkv.!qB", 4096);
        _lib.File(@"Movies\Alien.1979.mkv.part", 4096);
        _lib.File(@".sync\Heat.1995.mkv", 4096);
        _lib.File(@"Movies\readme.txt", 10);
        _lib.Resize(@"Movies\Heat.1995.mkv.!qB", 8192);
        await Task.Delay(Debounce * 3, TestContext.Current.CancellationToken);

        Assert.Equal(0, RescanCount());
    }

    [Fact]
    public async Task DeletedFilesInHiddenFoldersPartialDownloadsAndTextFiles_DoNotTriggerRescans()
    {
        _folders.Add(_lib.Root);
        _lib.File(@"Movies\Heat.1995.mkv.!qB", 4096);
        _lib.File(@"Movies\Alien.1979.mkv.parts", 4096);
        _lib.File(@".sync\Heat.1995.mkv", 4096);
        _lib.File(@"Movies\readme.txt", 10);
        _watcher.Start();

        _lib.Delete(@"Movies\Heat.1995.mkv.!qB");
        _lib.Delete(@"Movies\Alien.1979.mkv.parts");
        _lib.Delete(@".sync\Heat.1995.mkv");
        _lib.Delete(@"Movies\readme.txt");
        await Task.Delay(Debounce * 3, TestContext.Current.CancellationToken);

        Assert.Equal(0, RescanCount());
    }

    [Fact]
    public async Task DottedFolderMovedOutOfTheLibrary_TriggersRescan_AndItsRowsGoMissing()
    {
        const string name = "Heat.1995.1080p.BluRay.x264-GRP";
        var folder = _folders.Add(_lib.Root);
        _lib.File($@"{name}\Heat.1995.1080p.mkv");
        await new LibraryScanner(_db, _hub).ScanAsync(folder, TestContext.Current.CancellationToken);
        _watcher.Start();
        var rescan = NextRescan();

        // One Deleted event for the folder itself, named like a file with a ".x264-GRP" extension.
        var elsewhere = Path.Combine(Path.GetDirectoryName(_lib.Root)!, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.Move(_lib.PathOf(name), Path.Combine(elsewhere, name));

        Assert.Equal(folder.Id, await Within(rescan, Deadline));
        Assert.True(_files.ByFolder(folder.Id).Single().Missing);
    }

    [Fact]
    public async Task DottedFolderRenamedToAHiddenOne_TriggersRescan_AndItsRowsGoMissing()
    {
        const string name = "Mr. Robot";
        var folder = _folders.Add(_lib.Root);
        _lib.File($@"{name}\Mr.Robot.S01E01.mkv");
        await new LibraryScanner(_db, _hub).ScanAsync(folder, TestContext.Current.CancellationToken);
        _watcher.Start();
        var rescan = NextRescan();

        // The new path is hidden, so only the old one can tell the watcher something changed.
        Directory.Move(_lib.PathOf(name), _lib.PathOf(".old"));

        Assert.Equal(folder.Id, await Within(rescan, Deadline));
        Assert.True(_files.ByFolder(folder.Id).Single().Missing);
    }

    [Fact]
    public async Task FinishedDownload_TriggersRescan_WhenRenamedToItsVideoName()
    {
        var folder = _folders.Add(_lib.Root);
        _lib.Folder("Movies");
        _lib.File(@"Movies\Heat.1995.mkv.!qB", 4096);
        _watcher.Start();
        var rescan = NextRescan();

        File.Move(_lib.PathOf(@"Movies\Heat.1995.mkv.!qB"), _lib.PathOf(@"Movies\Heat.1995.mkv"));

        Assert.Equal(folder.Id, await Within(rescan, Deadline));
    }

    [Fact]
    public async Task NewFolder_TriggersRescan()
    {
        var folder = _folders.Add(_lib.Root);
        _watcher.Start();
        var rescan = NextRescan();

        _lib.Folder("Shows");

        Assert.Equal(folder.Id, await Within(rescan, Deadline));
    }

    [Fact]
    public async Task BurstOfChanges_TriggersOneRescan()
    {
        var folder = _folders.Add(_lib.Root);
        _watcher.Start();
        var rescan = NextRescan();

        for (var i = 0; i < 20; i++)
        {
            _lib.File($@"Movies\Film.{i:00}.2000.mkv");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await Within(rescan, Deadline);
        // Give a wrongly split burst the time to show up as a second rescan.
        await Task.Delay(Debounce * 3, TestContext.Current.CancellationToken);

        Assert.Equal(1, RescanCount());
        Assert.Equal(20, _files.ByFolder(folder.Id).Count);
    }

    [Fact]
    public async Task ChangeAfterRescan_TriggersAnotherRescan()
    {
        _folders.Add(_lib.Root);
        _watcher.Start();
        var first = NextRescan();
        _lib.File(@"Movies\Heat.1995.mkv");
        await Within(first, Deadline);

        var second = NextRescan();
        _lib.File(@"Movies\Alien.1979.mkv");

        await Within(second, Deadline);
        Assert.Equal(2, RescanCount());
    }

    [Fact]
    public async Task FolderAddedLater_IsWatchedAfterRefresh()
    {
        _watcher.Start();
        var folder = _folders.Add(_lib.Root);
        _watcher.Refresh();
        var rescan = NextRescan();

        _lib.File(@"Movies\Heat.1995.mkv");

        Assert.Equal(folder.Id, await Within(rescan, Deadline));
    }

    [Fact]
    public async Task DisabledFolder_IsNotWatched_UntilEnabledAndRefreshed()
    {
        var folder = _folders.Add(_lib.Root);
        _folders.SetEnabled(folder.Id, false);
        _watcher.Start();

        _lib.File(@"Movies\Heat.1995.mkv");
        await Task.Delay(Debounce * 3, TestContext.Current.CancellationToken);
        Assert.Equal(0, RescanCount());

        _folders.SetEnabled(folder.Id, true);
        _watcher.Refresh();
        var rescan = NextRescan();
        _lib.File(@"Movies\Alien.1979.mkv");

        Assert.Equal(folder.Id, await Within(rescan, Deadline));
    }

    [Fact]
    public async Task RemovedFolder_StopsBeingWatchedAfterRefresh()
    {
        var folder = _folders.Add(_lib.Root);
        _watcher.Start();
        _folders.Remove(folder.Id);
        _watcher.Refresh();

        _lib.File(@"Movies\Heat.1995.mkv");
        await Task.Delay(Debounce * 3, TestContext.Current.CancellationToken);

        Assert.Equal(0, RescanCount());
    }

    [Fact]
    public async Task FolderThatVanishedAndCameBack_IsWatchedAgainAfterRefresh()
    {
        var folder = _folders.Add(_lib.Root);
        _watcher.Start();
        var lost = NextRescan();
        Directory.Delete(_lib.Root, recursive: true); // the watcher reports an error and rescans
        await Within(lost, Deadline);

        Directory.CreateDirectory(_lib.Root);
        _watcher.Refresh();
        var rescan = NextRescan();
        _lib.File(@"Movies\Heat.1995.mkv");

        Assert.Equal(folder.Id, await Within(rescan, Deadline));
    }

    [Fact]
    public async Task BrokenWatchReplacedByRefresh_RescansWhatTheOldOneMissed()
    {
        var folder = _folders.Add(_lib.Root);
        _watcher.Start();
        Directory.Delete(_lib.Root, recursive: true); // the watcher reports an error and plans a rescan
        await WaitUntil(() => _hub.Recent.Any(e => e.Level == ActivityLevel.Warning && e.Message.StartsWith("Lost track")));

        // The folder is back and holds a file before the planned rescan runs; the replacement doesn't see
        // that file be created, so only a rescan scheduled by Refresh can find it.
        _lib.File(@"Movies\Heat.1995.mkv");
        _watcher.Refresh();

        await WaitUntil(() => _files.ByFolder(folder.Id).Any(f => f.RelPath == @"Movies\Heat.1995.mkv"));
    }

    [Fact]
    public void MissingFolder_IsSkipped()
    {
        var gone = Path.Combine(Path.GetTempPath(), "mediaboard-gone-" + Guid.NewGuid().ToString("N"));
        _folders.Add(gone);

        _watcher.Start();
        _watcher.Refresh();

        Assert.Equal(0, RescanCount());
    }

    [Fact]
    public async Task Stop_EndsRescans_AndStartResumesThem()
    {
        var folder = _folders.Add(_lib.Root);
        _watcher.Start();
        _watcher.Stop();

        _lib.File(@"Movies\Heat.1995.mkv");
        await Task.Delay(Debounce * 3, TestContext.Current.CancellationToken);
        Assert.Equal(0, RescanCount());

        _watcher.Start();
        var rescan = NextRescan();
        _lib.File(@"Movies\Alien.1979.mkv");

        Assert.Equal(folder.Id, await Within(rescan, Deadline));
    }

    [Fact]
    public async Task FailingSubscriber_IsLogged_AndWatchingContinues()
    {
        _folders.Add(_lib.Root);
        _watcher.Start();
        _throwOnce = 1;
        var rescan = NextRescan();
        _lib.File(@"Movies\Heat.1995.mkv");
        await Within(rescan, Deadline);
        await WaitUntil(() => _hub.Recent.Any(e => e.Level == ActivityLevel.Error));

        var next = NextRescan();
        _lib.File(@"Movies\Alien.1979.mkv");

        await Within(next, Deadline);
    }
}
