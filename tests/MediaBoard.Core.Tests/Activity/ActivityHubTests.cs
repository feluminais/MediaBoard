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

        hub.SetScan("Movies", 3, 10);
        hub.SetPendingIdentification(7);
        hub.SetOnline(true);
        hub.SetKeyState(KeyState.Ok);
        hub.SetPlayer("MPC-HC");
        hub.SetUnavailableFolders(["G:\\Shows"]);

        Assert.Equal(6, raised.Count);
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
        hub.SetScan(null, 0, 0);

        Assert.Equal(0, count);
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
