using System.ComponentModel;
using System.Diagnostics;
using MediaBoard.Core.Players;
using MediaBoard.Core.Players.MpcHc;

namespace MediaBoard.Core.Tests.Players;

public class MpcHcSessionTests
{
    private const string Exe = @"C:\Program Files\MPC-HC\mpc-hc64.exe";
    private const nint PlayerHwnd = 4660;
    private static readonly PlayerInfo Player = new("mpc-hc", "MPC-HC", Exe, TracksProgress: true);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeTransport _transport = new();
    private readonly FakePlayerProcess _process = new();
    private readonly PlaybackLog _events = new();
    private ProcessStartInfo? _startInfo;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MpcHcSession Create(TimeSpan? pollInterval = null, TimeSpan? closeTimeout = null, Func<ProcessStartInfo, IPlayerProcess>? start = null)
    {
        var session = new MpcHcSession(
            Player,
            () => _transport,
            start ?? (psi =>
            {
                _startInfo = psi;
                return _process;
            }),
            pollInterval,
            closeTimeout);
        session.Event += _events.Add;
        return session;
    }

    private async Task<MpcHcSession> StartConnectedAsync(TimeSpan? startAt = null, TimeSpan? pollInterval = null)
    {
        var session = Create(pollInterval);
        await session.StartAsync(@"C:\Videos\test.mp4", startAt, Ct);
        _transport.Receive(MpcApi.CMD_CONNECT, "4660");
        return session;
    }

    private async Task<MpcHcSession> LoadedAsync()
    {
        var session = await StartConnectedAsync();
        _transport.Receive(MpcApi.CMD_STATE, "2");
        _transport.Receive(MpcApi.CMD_NOWPLAYING, @"test|||C:\Videos\test.mp4|1500.000");
        return session;
    }

    [Fact]
    public async Task Session_StartsSlaveWithArgumentList()
    {
        const string file = @"C:\Відео\Bob's Burgers (2011)\S01E01.mkv";
        await using var session = Create();

        await session.StartAsync(file, null, Ct);

        Assert.NotNull(_startInfo);
        Assert.Equal(Exe, _startInfo.FileName);
        Assert.False(_startInfo.UseShellExecute);
        Assert.Equal(["/slave", "4096", "/new", file], _startInfo.ArgumentList);
    }

    [Fact]
    public async Task Session_SetsPositionAfterLoaded()
    {
        await using var session = await StartConnectedAsync(startAt: TimeSpan.FromSeconds(754.5));
        var sentBeforeLoaded = -1;
        session.Event += e =>
        {
            if (e is PlaybackEvent.Loaded) sentBeforeLoaded = _transport.SentTo(PlayerHwnd, MpcApi.CMD_SETPOSITION).Count;
        };

        _transport.Receive(MpcApi.CMD_STATE, "1");
        Assert.Empty(_transport.SentTo(PlayerHwnd, MpcApi.CMD_SETPOSITION));

        _transport.Receive(MpcApi.CMD_STATE, "2");
        Assert.Equal(["754.5"], _transport.SentTo(PlayerHwnd, MpcApi.CMD_SETPOSITION));

        _transport.Receive(MpcApi.CMD_NOWPLAYING, @"test|||C:\Videos\test.mp4|1500.000");
        Assert.Equal(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1500)), Assert.Single(_events.OfType<PlaybackEvent.Loaded>()));
        Assert.Equal(1, sentBeforeLoaded);
    }

    [Fact]
    public async Task Session_NoStartAt_DoesNotSeek()
    {
        await using var session = await StartConnectedAsync();

        _transport.Receive(MpcApi.CMD_STATE, "2");
        _transport.Receive(MpcApi.CMD_NOWPLAYING, @"test|||C:\Videos\test.mp4|30.000");

        Assert.Single(_events.OfType<PlaybackEvent.Loaded>());
        Assert.Empty(_transport.SentTo(PlayerHwnd, MpcApi.CMD_SETPOSITION));
    }

    [Fact]
    public async Task Session_ResumeOverriddenByPlayer_SeeksAgain()
    {
        _transport.Reply = (cmd, _) => cmd == MpcApi.CMD_GETCURRENTPOSITION ? (MpcApi.CMD_CURRENTPOSITION, "31.200") : null;
        await using var session = await StartConnectedAsync(startAt: TimeSpan.FromSeconds(600));

        _transport.Receive(MpcApi.CMD_STATE, "2");
        _transport.Receive(MpcApi.CMD_NOWPLAYING, @"test|||C:\Videos\test.mp4|1500.000");

        await Waiting.Until(() => _transport.SentTo(PlayerHwnd, MpcApi.CMD_SETPOSITION).Count == 2);
        Assert.Equal(["600", "600"], _transport.SentTo(PlayerHwnd, MpcApi.CMD_SETPOSITION));
    }

    [Fact]
    public async Task Session_ResumeKept_SeeksOnce()
    {
        _transport.Reply = (cmd, _) => cmd == MpcApi.CMD_GETCURRENTPOSITION ? (MpcApi.CMD_CURRENTPOSITION, "600.480") : null;
        await using var session = await StartConnectedAsync(startAt: TimeSpan.FromSeconds(600));

        _transport.Receive(MpcApi.CMD_STATE, "2");
        _transport.Receive(MpcApi.CMD_NOWPLAYING, @"test|||C:\Videos\test.mp4|1500.000");

        await Waiting.Until(() => _transport.SentTo(PlayerHwnd, MpcApi.CMD_GETCURRENTPOSITION).Count == 1);
        await Task.Delay(100, Ct);
        Assert.Single(_transport.SentTo(PlayerHwnd, MpcApi.CMD_SETPOSITION));
    }

    [Fact]
    public async Task Session_EarlyExit_RaisesFailed()
    {
        await using var session = Create();

        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);
        _process.Exit();

        var failed = await _events.WaitFor<PlaybackEvent.Failed>();
        Assert.Equal("MPC-HC closed right after starting", failed.Message);

        _transport.Receive(MpcApi.CMD_CURRENTPOSITION, "12.000");
        _transport.Receive(MpcApi.CMD_DISCONNECT, "");
        Assert.Equal([failed], _events.All);
    }

    [Fact]
    public async Task Session_MissingExecutable_RaisesFailed()
    {
        await using var session = Create(start: _ => throw new Win32Exception(2, "The system cannot find the file specified."));

        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);

        var failed = Assert.IsType<PlaybackEvent.Failed>(Assert.Single(_events.All));
        Assert.Equal($"MPC-HC not found: {Exe}", failed.Message);
    }

    [Fact]
    public async Task Session_ProcessExitAfterConnect_RaisesClosed()
    {
        await using var session = await StartConnectedAsync();

        _process.Exit(exitCode: 0);

        await _events.WaitFor<PlaybackEvent.Closed>();
        Assert.Empty(_events.OfType<PlaybackEvent.Failed>());
    }

    [Fact]
    public async Task Session_CrashAfterConnect_RaisesFailed()
    {
        await using var session = await StartConnectedAsync();

        _process.Exit(exitCode: unchecked((int)0xC0000005));

        var failed = await _events.WaitFor<PlaybackEvent.Failed>();
        Assert.Equal("MPC-HC stopped unexpectedly", failed.Message);
        Assert.Equal([failed], _events.All);
    }

    [Fact]
    public async Task Session_DisconnectThenNonZeroExit_StaysClosed()
    {
        await using var session = await StartConnectedAsync();

        _transport.Receive(MpcApi.CMD_DISCONNECT, "");
        _process.Exit(exitCode: 1);
        await Task.Delay(50, Ct);

        Assert.Equal([new PlaybackEvent.Closed()], _events.All);
    }

    [Fact]
    public async Task Session_OtherFileOpened_RaisesFileChangedAndKeepsReporting()
    {
        await using var session = await LoadedAsync();

        _transport.Receive(MpcApi.CMD_NOWPLAYING, @"next|||C:\Videos\test2.mp4|1300.000");
        _transport.Receive(MpcApi.CMD_NOTIFYSEEK, "3.000");
        _transport.Receive(MpcApi.CMD_NOTIFYENDOFSTREAM, "");

        Assert.Equal(
        [
            new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1500)),
            new PlaybackEvent.FileChanged(@"C:\Videos\test2.mp4"),
            new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1300)),
            new PlaybackEvent.Position(TimeSpan.FromSeconds(3)),
            new PlaybackEvent.Ended(),
        ], _events.All);
    }

    [Fact]
    public async Task Session_SameFileReported_KeepsSession()
    {
        await using var session = await LoadedAsync();

        _transport.Receive(MpcApi.CMD_NOWPLAYING, @"test|||c:\videos\.\TEST.mp4|1500.000");
        _transport.Receive(MpcApi.CMD_NOTIFYSEEK, "3.000");

        Assert.Equal([new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1500)), new PlaybackEvent.Position(TimeSpan.FromSeconds(3))], _events.All);
    }

    [Theory]
    [InlineData("3")] // MLS_CLOSING
    [InlineData("0")] // MLS_CLOSED
    public async Task Session_FileClosedAfterLoaded_RaisesFileChangedToNothing(string state)
    {
        await using var session = await LoadedAsync();

        _transport.Receive(MpcApi.CMD_STATE, state);

        Assert.Equal([new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1500)), new PlaybackEvent.FileChanged(null)], _events.All);
    }

    [Fact]
    public async Task Session_FileClosedThenReopened_RaisesFileChangedForTheNewFile()
    {
        await using var session = await LoadedAsync();

        _transport.Receive(MpcApi.CMD_STATE, "3"); // MLS_CLOSING
        _transport.Receive(MpcApi.CMD_STATE, "0"); // MLS_CLOSED
        _transport.Receive(MpcApi.CMD_NOWPLAYING, @"next|||C:\Videos\test2.mp4|1300.000");
        _transport.Receive(MpcApi.CMD_STATE, "2"); // MLS_LOADED

        Assert.Equal(
        [
            new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1500)),
            new PlaybackEvent.FileChanged(null),
            new PlaybackEvent.FileChanged(@"C:\Videos\test2.mp4"),
            new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1300)),
        ], _events.All);
    }

    [Fact]
    public async Task Session_ResumesOnlyTheLaunchedFile()
    {
        await using var session = Create();
        await session.StartAsync(@"C:\Videos\test.mp4", TimeSpan.FromSeconds(600), Ct);
        _transport.Receive(MpcApi.CMD_CONNECT, "4660");
        _transport.Receive(MpcApi.CMD_STATE, "2");
        _transport.Receive(MpcApi.CMD_NOWPLAYING, @"test|||C:\Videos\test.mp4|1500.000");
        _transport.Receive(MpcApi.CMD_STATE, "3");
        var seeks = _transport.SentTo(PlayerHwnd, MpcApi.CMD_SETPOSITION).Count;

        _transport.Receive(MpcApi.CMD_NOWPLAYING, @"next|||C:\Videos\test2.mp4|1300.000");
        _transport.Receive(MpcApi.CMD_STATE, "2");

        Assert.Equal(1, seeks);
        Assert.Equal(seeks, _transport.SentTo(PlayerHwnd, MpcApi.CMD_SETPOSITION).Count);
    }

    [Fact]
    public async Task Session_ClosedStateBeforeLoaded_IsIgnored()
    {
        await using var session = await StartConnectedAsync();

        _transport.Receive(MpcApi.CMD_STATE, "0");

        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task Handler_ThrowingOnPosition_StillGetsFinalEvent()
    {
        await using var session = await StartConnectedAsync();
        session.Event += e =>
        {
            if (e is not PlaybackEvent.Position) return;
            // The process exits while this thread is still delivering, so the exit's Closed is queued behind us.
            _process.Exit();
            Thread.Sleep(200);
            throw new InvalidOperationException("handler bug");
        };

        try
        {
            _transport.Receive(MpcApi.CMD_CURRENTPOSITION, "12.000");
        }
        catch (InvalidOperationException)
        {
            // Before the fix the handler's exception escaped here; either way the final event must arrive.
        }

        await _events.WaitFor<PlaybackEvent.Closed>();
        Assert.Equal([new PlaybackEvent.Position(TimeSpan.FromSeconds(12)), new PlaybackEvent.Closed()], _events.All);
    }

    [Fact]
    public async Task Handler_ThrowingOnFailed_StartAsyncDoesNotThrow()
    {
        await using var session = Create(start: _ => throw new Win32Exception(2, "The system cannot find the file specified."));
        session.Event += e => throw new InvalidOperationException($"handler bug on {e}");

        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);

        Assert.IsType<PlaybackEvent.Failed>(Assert.Single(_events.All));
    }

    [Fact]
    public async Task Handler_BlockingUntilDispose_DoesNotDeadlock()
    {
        await using var session = await StartConnectedAsync();
        using var handlerEntered = new ManualResetEventSlim();
        using var disposeCalled = new ManualResetEventSlim();
        var handlerSawDispose = false;
        session.Event += e =>
        {
            if (e is not PlaybackEvent.Playing) return;
            handlerEntered.Set();
            // Like a synchronous Dispatcher.Invoke onto a UI thread that is busy in DisposeAsync.
            handlerSawDispose = disposeCalled.Wait(TimeSpan.FromSeconds(2));
        };

        var receive = Task.Run(() => _transport.Receive(MpcApi.CMD_PLAYMODE, "0"), Ct);
        Assert.True(handlerEntered.Wait(Timeout, Ct));
        var dispose = session.DisposeAsync().AsTask();
        disposeCalled.Set();

        await dispose.WaitAsync(Timeout, Ct);
        await receive.WaitAsync(Timeout, Ct);
        Assert.True(handlerSawDispose, "DisposeAsync blocked while a handler was running");
    }

    [Fact]
    public async Task Session_LoadFailure_RaisesFailed()
    {
        await using var session = await StartConnectedAsync();

        _transport.Receive(MpcApi.CMD_STATE, "4");
        _transport.Receive(MpcApi.CMD_STATE, "0");

        var failed = Assert.IsType<PlaybackEvent.Failed>(Assert.Single(_events.All));
        Assert.Equal("MPC-HC could not open the file", failed.Message);
    }

    [Fact]
    public async Task Session_PlayModes_MapToPlayingAndPaused()
    {
        await using var session = await StartConnectedAsync();

        _transport.Receive(MpcApi.CMD_PLAYMODE, "0");
        _transport.Receive(MpcApi.CMD_PLAYMODE, "0");
        _transport.Receive(MpcApi.CMD_PLAYMODE, "1");
        _transport.Receive(MpcApi.CMD_PLAYMODE, "0");

        Assert.Equal([new PlaybackEvent.Playing(), new PlaybackEvent.Paused(), new PlaybackEvent.Playing()], _events.All);
    }

    [Fact]
    public async Task Session_PollsPositionOnlyWhilePlaying()
    {
        _transport.Reply = (cmd, _) => cmd == MpcApi.CMD_GETCURRENTPOSITION ? (MpcApi.CMD_CURRENTPOSITION, "12.500") : null;
        await using var session = await StartConnectedAsync(pollInterval: TimeSpan.FromMilliseconds(30));

        await Task.Delay(150, Ct);
        Assert.Empty(_transport.SentTo(PlayerHwnd, MpcApi.CMD_GETCURRENTPOSITION));

        _transport.Receive(MpcApi.CMD_PLAYMODE, "0");
        await Waiting.Until(() => _events.OfType<PlaybackEvent.Position>().Count >= 2);
        Assert.All(_events.OfType<PlaybackEvent.Position>(), p => Assert.Equal(TimeSpan.FromSeconds(12.5), p.At));

        _transport.Receive(MpcApi.CMD_PLAYMODE, "1");
        await Task.Delay(60, Ct);
        var polls = _transport.SentTo(PlayerHwnd, MpcApi.CMD_GETCURRENTPOSITION).Count;
        await Task.Delay(150, Ct);
        Assert.Equal(polls, _transport.SentTo(PlayerHwnd, MpcApi.CMD_GETCURRENTPOSITION).Count);
    }

    [Fact]
    public async Task GetPosition_AsksPlayer()
    {
        _transport.Reply = (cmd, _) => cmd == MpcApi.CMD_GETCURRENTPOSITION ? (MpcApi.CMD_CURRENTPOSITION, "42.000") : null;
        await using var session = await StartConnectedAsync();

        Assert.Equal(TimeSpan.FromSeconds(42), await session.GetPositionAsync(Ct));
    }

    [Fact]
    public async Task GetPosition_BeforeConnect_IsNull()
    {
        await using var session = Create();
        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);

        Assert.Null(await session.GetPositionAsync(Ct));
        Assert.Empty(_transport.Sent);
    }

    [Fact]
    public async Task Commands_GoToPlayerWindow()
    {
        await using var session = await StartConnectedAsync();

        session.Seek(TimeSpan.FromSeconds(10));
        session.Pause();
        session.Close();

        Assert.Equal(
            [(PlayerHwnd, MpcApi.CMD_SETPOSITION, "10"), (PlayerHwnd, MpcApi.CMD_PAUSE, ""), (PlayerHwnd, MpcApi.CMD_CLOSEAPP, "")],
            _transport.Sent);
    }

    [Fact]
    public async Task Dispose_ClosesPlayerItStarted()
    {
        var session = await StartConnectedAsync();

        await session.DisposeAsync();

        Assert.True(_process.CloseRequested);
        Assert.False(_process.Killed);
        Assert.True(_transport.Disposed);
        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task OtherFileOpened_ThenDispose_ClosesPlayer()
    {
        var session = await LoadedAsync();
        _transport.Receive(MpcApi.CMD_NOWPLAYING, @"next|||C:\Videos\test2.mp4|1500.000");

        await session.DisposeAsync();

        Assert.True(_process.CloseRequested);
        Assert.True(_process.Disposed);
        Assert.True(_transport.Disposed);
    }

    [Theory]
    [InlineData("3")] // MLS_CLOSING
    [InlineData("0")] // MLS_CLOSED
    public async Task FileClosed_ThenDispose_ClosesPlayer(string state)
    {
        var session = await LoadedAsync();
        _transport.Receive(MpcApi.CMD_STATE, state);

        await session.DisposeAsync();

        Assert.True(_process.CloseRequested);
        Assert.True(_process.Disposed);
    }

    [Fact]
    public async Task Detach_ThenDispose_LeavesPlayerRunning()
    {
        var session = await LoadedAsync();

        session.Detach();
        await session.DisposeAsync();

        Assert.False(_process.CloseRequested);
        Assert.False(_process.Killed);
        Assert.True(_process.Disposed);
        Assert.True(_transport.Disposed);
    }

    [Fact]
    public async Task Dispose_KillsPlayerThatIgnoresClose()
    {
        _process.ExitOnClose = false;
        var session = Create(closeTimeout: TimeSpan.FromMilliseconds(50));
        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);

        await session.DisposeAsync();

        Assert.True(_process.CloseRequested);
        Assert.True(_process.Killed);
        Assert.True(_process.Disposed);
    }

    [Fact]
    public async Task Dispose_LeavesExitedPlayerAlone()
    {
        var session = await StartConnectedAsync();
        _process.Exit();
        await _events.WaitFor<PlaybackEvent.Closed>();

        await session.DisposeAsync();

        Assert.False(_process.CloseRequested);
        Assert.False(_process.Killed);
        Assert.True(_transport.Disposed);
    }
}
