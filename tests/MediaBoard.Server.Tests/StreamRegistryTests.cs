using MediaBoard.Core.Accounts;
using MediaBoard.Core.Library;
using MediaBoard.Core.Remote;
using Microsoft.Extensions.Time.Testing;

namespace MediaBoard.Server.Tests;

/// <summary>Sessions as the Sharing page sees them: one row per playback, whatever the player does.</summary>
public sealed class StreamRegistryTests
{
    private static readonly TimeSpan JustUnderQuiet = TimeSpan.FromSeconds(29);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider _time = new();
    private readonly SleepRecorder _sleep = new();
    private readonly StreamRegistry _streams;

    public StreamRegistryTests() => _streams = new StreamRegistry(_time, _sleep);

    private static RequestViewer Who(int userId = 2, int deviceId = 10)
    {
        var account = new Account(userId, $"User {userId}", AccountRole.Viewer, false, true, null, DateTime.UnixEpoch, null);
        var device = new Device(deviceId, userId, $"Device {deviceId}", DateTime.UnixEpoch, null, null);
        return new RequestViewer(new Viewer(userId, null), account, device);
    }

    private static ReachableFile File(int fileId = 1, string? episode = null, double? durationS = 100) =>
        new(fileId, 7, $@"C:\library\{fileId}.mkv", $"{fileId}.mkv", "Heat", episode, durationS, false);

    private StreamLease Open(RequestViewer who, ReachableFile file) => _streams.Open(who, file, CancellationToken.None, out _);

    [Fact]
    public void Open_DescribesTheRow()
    {
        Open(Who(), File(episode: "S01E02"));

        var row = Assert.Single(_streams.Current);
        Assert.Equal(2, row.UserId);
        Assert.Equal("User 2", row.UserName);
        Assert.Equal(10, row.DeviceId);
        Assert.Equal("Device 10", row.DeviceName);
        Assert.Equal(1, row.FileId);
        Assert.Equal("Heat S01E02", row.Title);
        Assert.Equal(100, row.DurationS);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, row.StartedAt);
    }

    [Fact]
    public void OverlappingRanges_AreOneSession()
    {
        var changes = 0;
        _streams.Changed += () => changes++;

        var first = Open(Who(), File());
        var second = Open(Who(), File());
        first.Dispose();
        var third = Open(Who(), File());

        Assert.Single(_streams.Current);
        Assert.Equal(first.Session, second.Session);
        Assert.Equal(first.Session, third.Session);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Session_EndsAfter30sQuiet()
    {
        var changes = 0;
        using var lease = Open(Who(), File());
        _streams.Changed += () => changes++;

        lease.Dispose();
        _time.Advance(JustUnderQuiet);
        Assert.Single(_streams.Current);

        _time.Advance(Second);
        Assert.Empty(_streams.Current);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Session_ThatOutlivesAnotherAndKeepsActive_IsKept()
    {
        var quiet = Open(Who(deviceId: 10), File());
        _time.Advance(TimeSpan.FromSeconds(20));
        var active = Open(Who(deviceId: 11), File());

        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(active.Session, Assert.Single(_streams.Current).Id);
        Assert.NotEqual(quiet.Session, active.Session);

        _streams.AddBytes(active.Session, 1);
        _time.Advance(JustUnderQuiet);
        Assert.Single(_streams.Current);

        _time.Advance(Second);
        Assert.Empty(_streams.Current);
    }

    [Fact]
    public void Quiet_CancelsARequestStillOpen()
    {
        // A stalled client could hold its socket forever, keeping the PC awake for nobody.
        _streams.Open(Who(), File(), CancellationToken.None, out var stop);

        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.True(stop.IsCancellationRequested);
    }

    [Fact]
    public void Heartbeat_KeepsTheSessionAndMovesThePosition()
    {
        Open(Who(), File(durationS: null));

        _time.Advance(JustUnderQuiet);
        _streams.Heartbeat(10, 1, 42, 5400);
        _time.Advance(JustUnderQuiet);

        var row = Assert.Single(_streams.Current);
        Assert.Equal(42, row.PositionS);
        Assert.Equal(5400, row.DurationS);
    }

    [Fact]
    public void Heartbeat_WithNoDuration_KeepsTheOneKnown()
    {
        Open(Who(), File(durationS: 100));

        _streams.Heartbeat(10, 1, 42, 0);

        Assert.Equal(100, Assert.Single(_streams.Current).DurationS);
    }

    [Fact]
    public void Heartbeat_ForNoSession_IsIgnored()
    {
        _streams.Heartbeat(10, 1, 42, 5400);

        Assert.Empty(_streams.Current);
    }

    [Fact]
    public void Limit_8PerAccount_CountsPlaybacks()
    {
        for (var file = 1; file <= 8; file++)
        {
            Open(Who(), File(file));
            Open(Who(), File(file));
        }

        var refused = Assert.Throws<StreamRefusedException>(() => Open(Who(), File(9)));

        Assert.Equal(ApiErrorCode.TooManyStreams, refused.Code);
        Assert.Equal(8, _streams.Current.Count);

        // A playback already counted can reconnect, and the limit is per account, not per server.
        Open(Who(), File(1));
        Open(Who(userId: 3, deviceId: 20), File(9));
    }

    [Fact]
    public void Limit_CountsEveryDeviceOfTheAccount()
    {
        for (var device = 1; device <= 8; device++) Open(Who(deviceId: device), File());

        var refused = Assert.Throws<StreamRefusedException>(() => Open(Who(deviceId: 9), File()));

        Assert.Equal(ApiErrorCode.TooManyStreams, refused.Code);
    }

    [Fact]
    public void Limit_FreesASlotOnceASessionEnds()
    {
        for (var file = 1; file <= 8; file++) Open(Who(), File(file));

        _time.Advance(TimeSpan.FromSeconds(30));

        Open(Who(), File(9));
        Assert.Single(_streams.Current);
    }

    [Fact]
    public void Stop_CancelsAndBlocksFor1Minute()
    {
        var lease = _streams.Open(Who(), File(), CancellationToken.None, out var stop);

        _streams.Stop(lease.Session);

        Assert.True(stop.IsCancellationRequested);
        Assert.Empty(_streams.Current);
        Assert.Equal(ApiErrorCode.StreamStopped, Assert.Throws<StreamRefusedException>(() => Open(Who(), File())).Code);

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Throws<StreamRefusedException>(() => Open(Who(), File()));

        _time.Advance(Second);
        Open(Who(), File());
        Assert.Single(_streams.Current);
    }

    [Fact]
    public void Stop_BlocksOnlyThatDeviceAndFile()
    {
        _streams.Stop(Open(Who(), File()).Session);

        Open(Who(), File(2));
        Open(Who(deviceId: 11), File());

        Assert.Equal(2, _streams.Current.Count);
    }

    [Fact]
    public void Stop_UnknownSession_DoesNothing() => _streams.Stop(Guid.NewGuid());

    [Fact]
    public void Open_RequestAborted_EndsThatRequestOnly()
    {
        using var client = new CancellationTokenSource();
        _streams.Open(Who(), File(), client.Token, out var aborted);
        _streams.Open(Who(), File(), CancellationToken.None, out var other);

        client.Cancel();

        Assert.True(aborted.IsCancellationRequested);
        Assert.False(other.IsCancellationRequested);
        Assert.Single(_streams.Current);
    }

    [Fact]
    public void CloseAccount_CancelsAll()
    {
        _streams.Open(Who(deviceId: 10), File(1), CancellationToken.None, out var first);
        _streams.Open(Who(deviceId: 11), File(2), CancellationToken.None, out var second);
        _streams.Open(Who(userId: 3, deviceId: 20), File(1), CancellationToken.None, out var other);

        _streams.CloseAccount(2);

        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
        Assert.False(other.IsCancellationRequested);
        Assert.Equal(3, Assert.Single(_streams.Current).UserId);
    }

    [Fact]
    public void CloseDevice_CancelsOnlyThatDevice()
    {
        _streams.Open(Who(deviceId: 10), File(), CancellationToken.None, out var closed);
        _streams.Open(Who(deviceId: 11), File(), CancellationToken.None, out var other);

        _streams.CloseDevice(10);

        Assert.True(closed.IsCancellationRequested);
        Assert.False(other.IsCancellationRequested);
        Assert.Equal(11, Assert.Single(_streams.Current).DeviceId);
    }

    [Fact]
    public void CloseAll_CancelsEverySession()
    {
        _streams.Open(Who(userId: 2, deviceId: 10), File(), CancellationToken.None, out var first);
        _streams.Open(Who(userId: 3, deviceId: 20), File(), CancellationToken.None, out var second);

        _streams.CloseAll();

        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
        Assert.Empty(_streams.Current);
    }

    [Fact]
    public void SleepHeld_WhileAnySessionExists()
    {
        Assert.Equal(0, _sleep.Holds);

        Open(Who(deviceId: 10), File());
        Open(Who(deviceId: 11), File());
        Assert.Equal(1, _sleep.Holds);
        Assert.Equal(0, _sleep.Releases);

        _streams.CloseDevice(10);
        Assert.Equal(0, _sleep.Releases);

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, _sleep.Releases);

        Open(Who(), File());
        Assert.Equal(2, _sleep.Holds);
    }

    [Fact]
    public void Rate_ReportsMbitPerSecond()
    {
        var session = Open(Who(), File()).Session;

        for (var i = 0; i < 5; i++)
        {
            _time.Advance(Second);
            _streams.AddBytes(session, 1_250_000);
        }

        Assert.Equal(10, Assert.Single(_streams.Current).MbitPerSecond, precision: 3);

        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(0, Assert.Single(_streams.Current).MbitPerSecond);
    }

    [Fact]
    public void AddBytes_KeepsTheSessionAlive()
    {
        var session = Open(Who(), File()).Session;

        _time.Advance(JustUnderQuiet);
        _streams.AddBytes(session, 1);
        _time.Advance(JustUnderQuiet);

        Assert.Single(_streams.Current);
    }

    [Fact]
    public void AddBytes_AfterTheSessionEnded_IsIgnored()
    {
        var session = Open(Who(), File()).Session;
        _streams.CloseDevice(10);

        _streams.AddBytes(session, 1);

        Assert.Empty(_streams.Current);
    }

    [Fact]
    public void AccessChange_CancelsStreamNoLongerReachable()
    {
        _streams.Open(Who(), File(1), CancellationToken.None, out var gone);
        _streams.Open(Who(), File(2), CancellationToken.None, out var kept);

        _streams.CloseUnreachable((userId, fileId) => userId != 2 || fileId != 1);

        Assert.True(gone.IsCancellationRequested);
        Assert.False(kept.IsCancellationRequested);
        Assert.Equal(2, Assert.Single(_streams.Current).FileId);

        // Losing access isn't a stop: given back, the file plays at once.
        Open(Who(), File(1));
    }

    [Fact]
    public void Blocker_HoldsAndClearsOnOneThreadOfItsOwn()
    {
        const uint Continuous = 0x80000000;
        const uint SystemRequired = 0x00000001;
        var calls = new System.Collections.Concurrent.ConcurrentQueue<(int Thread, uint Flags)>();
        using var cleared = new ManualResetEventSlim();
        var blocker = new SleepBlocker(flags =>
        {
            calls.Enqueue((Environment.CurrentManagedThreadId, flags));
            if (flags == Continuous) cleared.Set();
            return 0;
        });

        blocker.Hold();
        blocker.Hold();
        blocker.Release();
        blocker.Release();

        Assert.True(cleared.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal([Continuous | SystemRequired, Continuous], calls.Select(call => call.Flags));

        // The state belongs to the calling thread, so a release from any other thread would clear nothing.
        var thread = Assert.Single(calls.Select(call => call.Thread).Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, thread);
    }
}
