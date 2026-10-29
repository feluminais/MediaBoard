using MediaBoard.Core.Activity;

namespace MediaBoard.Core.Tests.Activity;

public class ActivityHubTests
{
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public void ActivityHub_RaisesStatusChanged_OnEachSetter()
    {
        var hub = new ActivityHub();
        var raised = new List<StatusSnapshot>();
        hub.StatusChanged += raised.Add;

        hub.BeginScan(1, "Movies");
        hub.ReportScan(1, 3, 10);
        hub.SetPendingIdentification(7);
        hub.SetOnline(true);
        hub.SetKeyState(KeyState.Ok);
        hub.SetPlayer("MPC-HC");
        hub.SetUnavailableFolders(["G:\\Shows"]);

        Assert.Equal(7, raised.Count);
        var current = hub.Current;
        Assert.Equal("Movies", current.ScanFolder);
        Assert.Equal(3, current.ScanDone);
        Assert.Equal(10, current.ScanTotal);
        Assert.Equal(7, current.PendingIdentification);
        Assert.True(current.Online);
        Assert.Equal(KeyState.Ok, current.KeyState);
        Assert.Equal("MPC-HC", current.PlayerName);
        Assert.Equal(["G:\\Shows"], current.UnavailableFolders);
        Assert.Same(current, raised[^1]);
    }

    [Fact]
    public void ActivityHub_DoesNotRaise_WhenValueIsUnchanged()
    {
        var hub = new ActivityHub();
        hub.SetOnline(true);
        hub.SetUnavailableFolders(["A"]);
        var count = 0;
        hub.StatusChanged += _ => count++;

        hub.SetOnline(true);
        hub.SetUnavailableFolders(["A"]);
        hub.EndScan(1); // nothing was scanning

        Assert.Equal(0, count);
    }

    [Fact]
    public void FolderScan_IsTrackedByFolderId_NotByName()
    {
        var hub = new ActivityHub();

        hub.BeginScan(1, "Movies"); // D:\Movies
        hub.ReportScan(1, 120, 810);
        using var waiting = hub.QueueScan(2); // E:\Movies, behind it

        Assert.Equal(new FolderScan(FolderScanState.Scanning, 120, 810), hub.Current.ScanOf(1));
        Assert.Equal(new FolderScan(FolderScanState.Waiting, 0, 0), hub.Current.ScanOf(2));
        Assert.Null(hub.Current.ScanOf(3));
        Assert.Equal("Movies", hub.Current.ScanFolder);
        Assert.Equal(120, hub.Current.ScanDone);
        Assert.Equal(810, hub.Current.ScanTotal);
    }

    [Fact]
    public void FolderScan_StartsWithAnUnknownTotal_AndEndsIdle()
    {
        var hub = new ActivityHub();

        hub.BeginScan(1, "Movies");
        Assert.Equal(new FolderScan(FolderScanState.Scanning, 0, 0), hub.Current.ScanOf(1));
        Assert.True(hub.Current.IsScanning);

        hub.EndScan(1);
        Assert.Null(hub.Current.ScanOf(1));
        Assert.Empty(hub.Current.FolderScans);
        Assert.False(hub.Current.IsScanning);
        Assert.Null(hub.Current.ScanFolder);
        Assert.Equal(0, hub.Current.ScanDone);
        Assert.Equal(0, hub.Current.ScanTotal);
    }

    [Fact]
    public void QueuedScan_WaitsUntilItsScanStarts_AndStaysQueuedUntilDisposed()
    {
        var hub = new ActivityHub();

        var queued = hub.QueueScan(1);
        Assert.Equal(FolderScanState.Waiting, hub.Current.ScanOf(1)?.State);
        Assert.False(hub.Current.IsScanning); // waiting is not scanning

        hub.BeginScan(1, "Movies");
        Assert.Equal(FolderScanState.Scanning, hub.Current.ScanOf(1)?.State); // running wins over queued
        hub.EndScan(1);
        Assert.Equal(FolderScanState.Waiting, hub.Current.ScanOf(1)?.State); // its caller has not let go yet

        queued.Dispose();
        Assert.Null(hub.Current.ScanOf(1));
        queued.Dispose(); // twice is fine
        Assert.Null(hub.Current.ScanOf(1));
    }

    [Fact]
    public void QueuedScan_ThatNeverRan_IsDroppedWhenDisposed()
    {
        var hub = new ActivityHub();
        var first = hub.QueueScan(1);
        var second = hub.QueueScan(1); // queued again, as a rescan of a folder that has not started would be

        first.Dispose();
        Assert.Equal(FolderScanState.Waiting, hub.Current.ScanOf(1)?.State);
        second.Dispose();
        Assert.Null(hub.Current.ScanOf(1));
    }

    [Fact]
    public void StatusBarFields_FollowTheLatestRunningScan_WhenScansOverlap()
    {
        var hub = new ActivityHub();

        hub.BeginScan(1, "Movies");
        hub.ReportScan(1, 25, 100);
        hub.BeginScan(2, "Shows");
        hub.ReportScan(2, 5, 40);
        Assert.Equal(("Shows", 5, 40), (hub.Current.ScanFolder, hub.Current.ScanDone, hub.Current.ScanTotal));

        hub.EndScan(2);
        Assert.Equal(("Movies", 25, 100), (hub.Current.ScanFolder, hub.Current.ScanDone, hub.Current.ScanTotal));
        hub.ReportScan(1, 50, 100);
        Assert.Equal(("Movies", 50, 100), (hub.Current.ScanFolder, hub.Current.ScanDone, hub.Current.ScanTotal));
    }

    [Fact]
    public void ReportScan_ForAFolderThatIsNotScanning_ChangesNothing()
    {
        var hub = new ActivityHub();
        var count = 0;
        hub.StatusChanged += _ => count++;

        hub.ReportScan(1, 3, 10);
        hub.BeginScan(2, "Movies");
        hub.ReportScan(2, 3, 10);
        hub.ReportScan(2, 3, 10); // the same numbers again

        Assert.Null(hub.Current.ScanOf(1));
        Assert.Equal(2, count);
    }

    [Fact]
    public void ActivityHub_SnapshotsAreImmutable()
    {
        var hub = new ActivityHub();
        var before = hub.Current;

        hub.SetOnline(true);

        Assert.False(before.Online);
        Assert.True(hub.Current.Online);
    }

    [Fact]
    public void ActivityHub_KeepsLast200()
    {
        var hub = new ActivityHub();

        for (var i = 0; i < 250; i++) hub.Log(ActivityLevel.Info, $"msg {i}");

        var recent = hub.Recent;
        Assert.Equal(200, recent.Count);
        Assert.Equal("msg 50", recent[0].Message);
        Assert.Equal("msg 249", recent[^1].Message);
    }

    [Fact]
    public void ActivityHub_Log_UsesTimeProviderAndKeepsHint()
    {
        var utc = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var hub = new ActivityHub(new FixedTimeProvider(utc));

        hub.Log(ActivityLevel.Warning, "TMDB is unreachable", "Check your connection");

        var entry = Assert.Single(hub.Recent);
        Assert.Equal(ActivityLevel.Warning, entry.Level);
        Assert.Equal("TMDB is unreachable", entry.Message);
        Assert.Equal("Check your connection", entry.Hint);
        Assert.Equal(utc.DateTime, entry.At);
    }

    [Fact]
    public void ActivityHub_TracksLibraryErrorAndFailedScans()
    {
        var hub = new ActivityHub();
        var raised = 0;
        hub.StatusChanged += _ => raised++;

        hub.SetLibraryError("Close other programs using library.db");
        hub.SetFailedScans(["Movies"]);
        hub.SetLibraryError("Close other programs using library.db");
        hub.SetFailedScans(["Movies"]);

        Assert.Equal(2, raised);
        Assert.Equal("Close other programs using library.db", hub.Current.LibraryError);
        Assert.Equal(["Movies"], hub.Current.FailedScans);

        hub.SetLibraryError(null);
        hub.SetFailedScans([]);
        Assert.Null(hub.Current.LibraryError);
        Assert.Empty(hub.Current.FailedScans);
    }

    [Fact]
    public void ActivityHub_RaisesEntryLogged_OutsideItsLock()
    {
        var hub = new ActivityHub();
        var raised = new List<ActivityEntry>();
        IReadOnlyList<ActivityEntry>? seenInHandler = null;
        hub.EntryLogged += entry =>
        {
            raised.Add(entry);
            // Reading the hub from another thread inside the handler would deadlock if the lock were still held.
            seenInHandler = Task.Run(() => hub.Recent).Wait(TimeSpan.FromSeconds(5)) ? hub.Recent : null;
        };

        hub.Log(ActivityLevel.Error, "Folder unavailable: Shows", "Connect the drive");

        var entry = Assert.Single(raised);
        Assert.Equal("Folder unavailable: Shows", entry.Message);
        Assert.Equal("Connect the drive", entry.Hint);
        Assert.NotNull(seenInHandler);
        Assert.Same(entry, Assert.Single(seenInHandler));
    }

    [Fact]
    public async Task ActivityHub_IsThreadSafe()
    {
        var hub = new ActivityHub();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                hub.Log(ActivityLevel.Info, $"{t}-{i}");
                hub.SetPendingIdentification(t * 1000 + i);
                _ = hub.Recent;
            }
        })));

        Assert.Equal(200, hub.Recent.Count);
    }
}
