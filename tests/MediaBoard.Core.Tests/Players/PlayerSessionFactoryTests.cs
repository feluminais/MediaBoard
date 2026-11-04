using System.ComponentModel;
using System.Diagnostics;
using MediaBoard.Core.Players;
using MediaBoard.Core.Players.MpcHc;
using MediaBoard.Core.Players.Mpv;
using MediaBoard.Core.Players.Vlc;

namespace MediaBoard.Core.Tests.Players;

public class PlayerSessionFactoryTests
{
    private const string Exe = @"C:\Tools\Player\player.exe";
    private const string File = @"C:\Videos\test.mp4";
    private static readonly PlayerInfo Custom = new("custom", "My Player", Exe, TracksProgress: false);

    private readonly FakePlayerProcess _process = new();
    private readonly PlaybackLog _events = new();
    private readonly ManualClock _clock = new();
    private ProcessStartInfo? _startInfo;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LaunchOnlySession CreateLaunchOnly(
        TimeSpan? closeTimeout = null,
        Func<ProcessStartInfo, IPlayerProcess>? start = null)
    {
        var session = new LaunchOnlySession(
            Custom,
            start ?? (psi =>
            {
                _startInfo = psi;
                return _process;
            }),
            _clock,
            closeTimeout);
        session.Event += _events.Add;
        return session;
    }

    [Theory]
    [InlineData("mpc-hc", typeof(MpcHcSession), true)]
    [InlineData("mpc-be", typeof(MpcHcSession), true)]
    [InlineData("mpv", typeof(MpvSession), true)]
    [InlineData("vlc", typeof(VlcSession), true)]
    [InlineData("custom", typeof(LaunchOnlySession), false)]
    public async Task Factory_MapsIds(string id, Type expected, bool tracksProgress)
    {
        var player = new PlayerInfo(id, id, @"C:\Tools\" + id + ".exe", TracksProgress: true);

        await using var session = PlayerSessionFactory.Create(player);

        Assert.IsType(expected, session);
        Assert.Equal(tracksProgress, session.TracksProgress);
    }

    [Fact]
    public void Factory_UnknownId_Throws()
    {
        var thrown = Assert.Throws<ArgumentException>(() =>
            PlayerSessionFactory.Create(new PlayerInfo("potplayer", "PotPlayer", Exe, TracksProgress: true)));

        Assert.Contains("potplayer", thrown.Message);
    }

    [Fact]
    public async Task LaunchOnly_StartsTheProgramWithTheFileAsOneArgument()
    {
        const string file = @"C:\Відео\Bob's Burgers (2011)\S01E01.mkv";
        await using var session = CreateLaunchOnly();

        await session.StartAsync(file, TimeSpan.FromSeconds(300), Ct);

        Assert.False(session.TracksProgress);
        Assert.NotNull(_startInfo);
        Assert.Equal(Exe, _startInfo.FileName);
        Assert.False(_startInfo.UseShellExecute);
        Assert.Equal([file], _startInfo.ArgumentList);
        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task LaunchOnly_ExitRaisesClosed()
    {
        await using var session = CreateLaunchOnly();
        await session.StartAsync(File, null, Ct);

        _process.Exit();

        Assert.Equal(new PlaybackEvent.Closed(), await _events.WaitFor<PlaybackEvent.Closed>());
        Assert.Single(_events.All);
    }

    [Fact]
    public async Task LaunchOnly_QuickNonZeroExit_RaisesFailed()
    {
        await using var session = CreateLaunchOnly();
        await session.StartAsync(File, null, Ct);
        _clock.Advance(TimeSpan.FromSeconds(2.9));

        _process.Exit(1);

        var failed = await _events.WaitFor<PlaybackEvent.Failed>();
        Assert.Equal("My Player closed right after starting", failed.Message);
        Assert.Single(_events.All);
    }

    [Fact]
    public async Task LaunchOnly_LateNonZeroExit_RaisesClosed()
    {
        // Many programs return a non-zero code after a normal session; only a quick one is a launch failure.
        await using var session = CreateLaunchOnly();
        await session.StartAsync(File, null, Ct);
        _clock.Advance(TimeSpan.FromSeconds(3));

        _process.Exit(1);

        await _events.WaitFor<PlaybackEvent.Closed>();
        Assert.Single(_events.All);
    }

    [Fact]
    public async Task LaunchOnly_MissingExecutable_RaisesFailed()
    {
        await using var session = CreateLaunchOnly(start: _ => throw new Win32Exception(2));

        await session.StartAsync(File, null, Ct);

        Assert.Equal([new PlaybackEvent.Failed($"My Player not found: {Exe}")], _events.All);
    }

    [Fact]
    public async Task LaunchOnly_Dispose_ClosesProcessItStarted()
    {
        var session = CreateLaunchOnly();
        await session.StartAsync(File, null, Ct);

        await session.DisposeAsync();

        Assert.True(_process.CloseRequested);
        Assert.False(_process.Killed);
        Assert.True(_process.Disposed);
        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task LaunchOnly_Detach_ThenDispose_LeavesPlayerRunning()
    {
        var session = CreateLaunchOnly();
        await session.StartAsync(File, null, Ct);

        session.Detach();
        await session.DisposeAsync();

        Assert.False(_process.CloseRequested);
        Assert.False(_process.Killed);
        Assert.True(_process.Disposed);
    }

    [Fact]
    public async Task LaunchOnly_Dispose_BeforeStartOrTwice_DoesNothing()
    {
        var session = CreateLaunchOnly();

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.False(_process.CloseRequested);
    }

    [Fact]
    public async Task LaunchOnly_StartTwiceOrAfterDispose_Throws()
    {
        var session = CreateLaunchOnly();
        await session.StartAsync(File, null, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(File, null, Ct));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync(File, null, Ct));
    }
}
