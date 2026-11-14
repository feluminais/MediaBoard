using CozyCouch.Core.Activity;
using CozyCouch.Core.Metadata;
using CozyCouch.Core.Storage;
using CozyCouch.Desktop.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace CozyCouch.Desktop.Tests;

/// <summary>
/// The cancel-and-restart side of <see cref="FolderActions"/>: scans caught part-way by a remove, a disable or a
/// re-point, and identification that is still running. Plain xunit tests: the actions never touch the UI thread.
/// </summary>
public sealed class FolderActionsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Holds the next scan once it has started. The scanner announces itself on the hub from its own thread, so a
    /// handler that blocks there stops that scan at a known point without any change to the scanner.
    /// </summary>
    private sealed class ScanGate
    {
        private readonly ManualResetEventSlim _release = new();
        private int _armed;

        public ScanGate(ActivityHub hub) => hub.StatusChanged += status =>
        {
            if (status.ScanFolder is null || Interlocked.CompareExchange(ref _armed, 0, 1) != 1) return;
            Reached.Set();
            _release.Wait(Wait);
        };

        public ManualResetEventSlim Reached { get; } = new();

        public void Arm()
        {
            Reached.Reset();
            _release.Reset();
            Interlocked.Exchange(ref _armed, 1);
        }

        public void Release() => _release.Set();
    }

    private static int OnlyFolderId(ShellStack stack) => Assert.Single(stack.Library.Folders()).Id;

    private static int RowsOf(ShellStack stack, int folderId) => stack.Services.GetRequiredService<FileStore>().ByFolder(folderId).Count;

    private static void AssertNoErrors(ShellStack stack) =>
        Assert.DoesNotContain(stack.Hub.Recent, e => e.Level == ActivityLevel.Error);

    [Fact]
    public async Task Remove_DuringAScan_WaitsForTheScanToStop_AndLeavesNoRows()
    {
        using var stack = new ShellStack();
        var gate = new ScanGate(stack.Hub);
        gate.Arm();
        var root = stack.MakeLibrary("Movies", "Heat (1995).mkv", "Alien (1979).mkv");
        Assert.True((await stack.Actions.AddAsync(root)).Ok);
        Assert.True(gate.Reached.Wait(Wait, Ct), "the scan never started");
        var id = OnlyFolderId(stack);

        var remove = stack.Actions.RemoveAsync(id);
        await Task.Delay(300, Ct); // long enough for it to cancel the scan and start waiting for it
        Assert.False(remove.IsCompleted, "removing must wait for the scan, which is still stopping");
        gate.Release();
        var outcome = await remove.WaitAsync(Wait, Ct);
        await stack.Actions.WhenIdleAsync();

        Assert.True(outcome.Ok);
        Assert.Empty(stack.Library.Folders());
        Assert.Equal(0, RowsOf(stack, id));
        Assert.True(File.Exists(Path.Combine(root, "Heat (1995).mkv")));
        AssertNoErrors(stack); // no rescan of a folder that was being deleted
    }

    [Fact]
    public async Task Disable_DuringAScan_StopsIt_AndEnableScansAgain()
    {
        using var stack = new ShellStack();
        var gate = new ScanGate(stack.Hub);
        gate.Arm();
        var root = stack.MakeLibrary("Movies", "Heat (1995).mkv", "Alien (1979).mkv");
        await stack.Actions.AddAsync(root);
        Assert.True(gate.Reached.Wait(Wait, Ct), "the scan never started");
        var id = OnlyFolderId(stack);

        var disable = stack.Actions.SetEnabledAsync(id, false);
        await Task.Delay(300, Ct);
        gate.Release();
        Assert.True((await disable.WaitAsync(Wait, Ct)).Ok);
        await stack.Actions.WhenIdleAsync();

        var row = Assert.Single(stack.Library.Folders());
        Assert.False(row.Enabled);
        Assert.Equal(0, row.FileCount); // the stopped scan wrote nothing
        AssertNoErrors(stack);

        Assert.True((await stack.Actions.SetEnabledAsync(id, true)).Ok);
        await stack.Actions.WhenIdleAsync();
        Assert.Equal(2, Assert.Single(stack.Library.Folders()).FileCount);
    }

    [Fact]
    public async Task EnableThenAtOnceDisable_LeavesItDisabled_AndTheOldRowsAlone()
    {
        using var stack = new ShellStack();
        var gate = new ScanGate(stack.Hub);
        var root = stack.MakeLibrary("Movies", "Heat (1995).mkv", "Alien (1979).mkv");
        await stack.Actions.AddAsync(root);
        await stack.Actions.WhenIdleAsync();
        var id = OnlyFolderId(stack);
        await stack.Actions.SetEnabledAsync(id, false);

        gate.Arm();
        await stack.Actions.SetEnabledAsync(id, true); // starts a scan, which the gate holds
        Assert.True(gate.Reached.Wait(Wait, Ct), "the scan never started");
        var disable = stack.Actions.SetEnabledAsync(id, false);
        await Task.Delay(300, Ct);
        gate.Release();
        Assert.True((await disable.WaitAsync(Wait, Ct)).Ok);
        await stack.Actions.WhenIdleAsync();

        var row = Assert.Single(stack.Library.Folders());
        Assert.False(row.Enabled);
        Assert.Equal(2, row.FileCount);
        AssertNoErrors(stack);
    }

    [Fact]
    public async Task Repoint_DuringAScan_ScansTheNewLocation_AfterTheOldScanStopped()
    {
        using var stack = new ShellStack();
        var gate = new ScanGate(stack.Hub);
        gate.Arm();
        var first = stack.MakeLibrary("Old", "Heat (1995).mkv");
        var second = stack.MakeLibrary("New", "Alien (1979).mkv", "Lost (2004).mkv");
        await stack.Actions.AddAsync(first);
        Assert.True(gate.Reached.Wait(Wait, Ct), "the scan never started");
        var id = OnlyFolderId(stack);

        var repoint = stack.Actions.RepointAsync(id, second);
        await Task.Delay(300, Ct);
        gate.Release();
        Assert.True((await repoint.WaitAsync(Wait, Ct)).Ok);
        await stack.Actions.WhenIdleAsync();

        var row = Assert.Single(stack.Library.Folders());
        Assert.Equal(second, row.Path);
        Assert.Equal(2, row.FileCount);
        AssertNoErrors(stack);
    }

    // ---- Identification that is still running ----

    private sealed class GatedProvider : IMetadataProvider
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set when identification asked the provider something, which then waits for <see cref="Open"/>.</summary>
        public ManualResetEventSlim Asked { get; } = new();

        public void Open() => _open.TrySetResult();

        public async Task<IReadOnlyList<SearchCandidate>> SearchAsync(string title, int? year, TitleKind kind, CancellationToken ct)
        {
            Asked.Set();
            await _open.Task.WaitAsync(ct);
            return [];
        }

        public Task<IReadOnlyList<SearchCandidate>> SearchAnyAsync(string query, CancellationToken ct) => throw new NotSupportedException();

        public Task<(int TmdbId, TitleKind Kind)?> FindByImdbIdAsync(string imdbId, CancellationToken ct) =>
            Task.FromResult<(int TmdbId, TitleKind Kind)?>(null);

        public Task<TitleDetails> GetTitleAsync(int tmdbId, TitleKind kind, CancellationToken ct) => throw new NotSupportedException();

        public Task<SeasonDetails> GetSeasonAsync(int showTmdbId, int season, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task NewFolder_IsWatched_AndRemoveAndDisableReturn_WhileIdentificationIsStillRunning()
    {
        var provider = new GatedProvider();
        using var stack = new ShellStack(services => services.AddSingleton<IMetadataProvider>(provider));
        try
        {
            stack.Watcher.Start();
            var movies = stack.MakeLibrary("Movies", "Heat (1995).mkv");
            var shows = stack.MakeLibrary("Shows", "Lost (2004).mkv");

            Assert.True((await stack.Actions.AddAsync(movies)).Ok);
            Assert.True(provider.Asked.Wait(Wait, Ct), "identification never started");

            // 1. Identification is stuck, yet the new folder is already watched: a new file there gets rescanned.
            var rescanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stack.Watcher.Rescanned += _ => rescanned.TrySetResult();
            // The watcher waits for a quiet spell before it rescans, so add one file per try and give it time.
            for (var n = 0; !rescanned.Task.IsCompleted && n < 4; n++)
            {
                File.WriteAllBytes(Path.Combine(movies, $"New Film ({1990 + n}).mkv"), []);
                await Task.WhenAny(rescanned.Task, Task.Delay(6000, Ct));
            }

            Assert.True(rescanned.Task.IsCompleted, "the folder was not watched while identification ran: " + string.Join(" | ", stack.Hub.Recent.Select(e => e.Level + " " + e.Message + " " + e.Hint)));

            // 2. Another folder, then disable and remove, all while the same identification run is stuck.
            Assert.True((await stack.Actions.AddAsync(shows)).Ok);
            await WaitFor(() => stack.Library.Folders().Any(f => f.Path == shows && f.FileCount == 1));
            var showsId = stack.Library.Folders().Single(f => f.Path == shows).Id;
            var moviesId = stack.Library.Folders().Single(f => f.Path == movies).Id;

            Assert.True((await stack.Actions.SetEnabledAsync(showsId, false).WaitAsync(Wait, Ct)).Ok);
            Assert.True((await stack.Actions.RemoveAsync(moviesId).WaitAsync(Wait, Ct)).Ok);
            Assert.Equal([showsId], stack.Library.Folders().Select(f => f.Id));
        }
        finally
        {
            provider.Open();
        }
    }

    [Fact]
    public async Task RescanAll_ScansTheEnabledFoldersAgain_AndNotTheDisabledOnes()
    {
        using var stack = new ShellStack();
        var movies = stack.MakeLibrary("Movies", "Heat (1995).mkv");
        var shows = stack.MakeLibrary("Shows", "Dark S01E01.mkv");
        await stack.Actions.AddAsync(movies);
        await stack.Actions.AddAsync(shows);
        await stack.Actions.WhenIdleAsync();
        var showsId = stack.Library.Folders().Single(f => f.Path == shows).Id;
        await stack.Actions.SetEnabledAsync(showsId, false);
        var moviesId = stack.Library.Folders().Single(f => f.Path == movies).Id;
        File.WriteAllBytes(Path.Combine(movies, "Alien (1979).mkv"), []);
        File.WriteAllBytes(Path.Combine(shows, "Dark S01E02.mkv"), []);

        var outcome = await stack.Services.GetRequiredService<FolderActions>().RescanAllAsync();
        await stack.Actions.WhenIdleAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(2, RowsOf(stack, moviesId));
        Assert.Equal(1, RowsOf(stack, showsId)); // switched off: left alone
        AssertNoErrors(stack);
    }

    [Fact]
    public async Task Add_MarksTheFolderWaitingOrScanning_BeforeItReturns_AndIdleOnceDone()
    {
        using var stack = new ShellStack();
        var gate = new ScanGate(stack.Hub);
        gate.Arm(); // the scan can't end before the check below
        var root = stack.MakeLibrary("Movies", "Heat (1995).mkv");

        Assert.True((await stack.Actions.AddAsync(root)).Ok);
        var id = OnlyFolderId(stack);

        Assert.NotNull(stack.Hub.Current.ScanOf(id)); // never idle with no files before its first scan
        gate.Release();
        await stack.Actions.WhenIdleAsync();
        Assert.Null(stack.Hub.Current.ScanOf(id));
        Assert.Empty(stack.Hub.Current.FolderScans);
    }

    [Fact]
    public async Task Remove_OfAFolderWithAScanStillQueued_LeavesItIdle()
    {
        using var stack = new ShellStack();
        var gate = new ScanGate(stack.Hub);
        gate.Arm();
        var root = stack.MakeLibrary("Movies", "Heat (1995).mkv");
        Assert.True((await stack.Actions.AddAsync(root)).Ok);
        Assert.True(gate.Reached.Wait(Wait, Ct), "the scan never started");
        var id = OnlyFolderId(stack);
        // A second scan queues behind the held one; removing cancels it before it ever runs.
        await stack.Services.GetRequiredService<FolderActions>().RescanAllAsync();

        var remove = stack.Actions.RemoveAsync(id);
        gate.Release();
        await remove.WaitAsync(Wait, Ct);
        await stack.Actions.WhenIdleAsync();

        Assert.Empty(stack.Hub.Current.FolderScans);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition never held.");
            await Task.Delay(20, Ct);
        }
    }
}
