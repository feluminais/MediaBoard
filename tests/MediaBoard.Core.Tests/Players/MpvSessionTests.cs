using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using MediaBoard.Core.Players;
using MediaBoard.Core.Players.Mpv;

namespace MediaBoard.Core.Tests.Players;

public class MpvSessionTests
{
    private const string Exe = @"C:\Program Files\mpv\mpv.exe";
    private const string PipePrefix = @"\\.\pipe\";
    private static readonly PlayerInfo Player = new("mpv", "mpv", Exe, TracksProgress: true);

    private readonly FakePipe _pipe = new();
    private readonly FakePlayerProcess _process = new();
    private readonly PlaybackLog _events = new();
    private readonly ManualClock _clock = new();
    private readonly List<string> _connectNames = [];
    private int _connectFailures;
    private ProcessStartInfo? _startInfo;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MpvSession Create(
        TimeSpan? connectTimeout = null,
        TimeSpan? closeTimeout = null,
        Func<ProcessStartInfo, IPlayerProcess>? start = null)
    {
        var session = new MpvSession(
            Player,
            (name, _) =>
            {
                lock (_connectNames) _connectNames.Add(name);
                return Task.FromResult<IMpvPipe?>(_connectFailures-- > 0 ? null : _pipe);
            },
            start ?? (psi =>
            {
                _startInfo = psi;
                return _process;
            }),
            _clock,
            connectTimeout,
            retryDelay: TimeSpan.FromMilliseconds(5),
            closeTimeout,
            exitDrain: TimeSpan.FromMilliseconds(30));
        session.Event += _events.Add;
        return session;
    }

    private async Task<MpvSession> StartConnectedAsync(TimeSpan? startAt = null)
    {
        var session = Create();
        await session.StartAsync(@"C:\Videos\test.mp4", startAt, Ct);
        await _pipe.WaitUntilReading();
        return session;
    }

    [Fact]
    public async Task Session_StartsMpvWithArgumentList()
    {
        const string file = @"C:\Відео\Bob's Burgers (2011)\S01E01.mkv";
        await using var session = Create();

        await session.StartAsync(file, TimeSpan.FromSeconds(754.5), Ct);

        Assert.NotNull(_startInfo);
        Assert.Equal(Exe, _startInfo.FileName);
        Assert.False(_startInfo.UseShellExecute);
        var args = _startInfo.ArgumentList;
        Assert.Equal(4, args.Count);
        Assert.Matches(@"^--input-ipc-server=\\\\\.\\pipe\\mediaboard-[0-9a-f]{32}$", args[0]);
        Assert.Equal(["--force-window=yes", "--start=754.5", file], args.Skip(1));
    }

    [Fact]
    public async Task Session_NoStartAt_OmitsStart()
    {
        await using var session = Create();

        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);

        Assert.NotNull(_startInfo);
        Assert.DoesNotContain(_startInfo.ArgumentList, a => a.StartsWith("--start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Session_StartAtIsInvariantCulture()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("uk-UA");
        try
        {
            await using var session = Create();
            await session.StartAsync(@"C:\Videos\test.mp4", TimeSpan.FromSeconds(90.25), Ct);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }

        Assert.Contains("--start=90.25", _startInfo!.ArgumentList);
    }

    [Fact]
    public async Task Session_ConnectsToThePipeItNamedAndObservesProperties()
    {
        await using var session = await StartConnectedAsync();

        var serverArg = _startInfo!.ArgumentList[0]["--input-ipc-server=".Length..];
        Assert.StartsWith(PipePrefix, serverArg);
        Assert.Equal(serverArg[PipePrefix.Length..], Assert.Single(_connectNames));
        Assert.Equal(
        [
            """{"command":["observe_property",1,"time-pos"]}""",
            """{"command":["observe_property",2,"duration"]}""",
            """{"command":["observe_property",3,"pause"]}""",
            """{"command":["observe_property",4,"eof-reached"]}""",
        ], _pipe.Written);
    }

    [Fact]
    public async Task Mpv_ParsesPropertyChangeLines()
    {
        await using var session = await StartConnectedAsync();

        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"pause","data":false}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":2,"name":"duration","data":1500.5}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos","data":12.25}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"pause","data":true}""");

        Assert.Equal(
        [
            new PlaybackEvent.Playing(),
            new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1500.5)),
            new PlaybackEvent.Position(TimeSpan.FromSeconds(12.25)),
            new PlaybackEvent.Paused(),
        ], _events.All);
    }

    [Fact]
    public async Task Mpv_EofRaisesEnded()
    {
        await using var session = await StartConnectedAsync();

        await _pipe.FeedAsync("""{"event":"property-change","id":4,"name":"eof-reached","data":true}""");

        Assert.Equal([new PlaybackEvent.Ended()], _events.All);
    }

    [Fact]
    public async Task Mpv_EndFileEofRaisesEnded_OncePerEnd()
    {
        await using var session = await StartConnectedAsync();

        await _pipe.FeedAsync("""{"event":"property-change","id":4,"name":"eof-reached","data":true}""");
        await _pipe.FeedAsync("""{"event":"end-file","reason":"eof","playlist_entry_id":1}""");
        Assert.Single(_events.OfType<PlaybackEvent.Ended>());

        // Playing again after a seek back, then reaching the end again, is a second end.
        await _pipe.FeedAsync("""{"event":"property-change","id":4,"name":"eof-reached","data":false}""");
        await _pipe.FeedAsync("""{"event":"end-file","reason":"eof","playlist_entry_id":1}""");
        Assert.Equal(2, _events.OfType<PlaybackEvent.Ended>().Count);
    }

    [Fact]
    public async Task Mpv_EndFileForOtherReasons_IsNotAnEnd()
    {
        await using var session = await StartConnectedAsync();

        await _pipe.FeedAsync("""{"event":"end-file","reason":"quit","playlist_entry_id":1}""");
        await _pipe.FeedAsync("""{"event":"end-file","reason":"stop","playlist_entry_id":1}""");

        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task Mpv_EndFileError_RaisesFailed()
    {
        await using var session = await StartConnectedAsync();

        await _pipe.FeedAsync("""{"event":"end-file","reason":"error","playlist_entry_id":1,"file_error":"loading failed"}""");

        Assert.Equal([new PlaybackEvent.Failed("mpv could not open the file")], _events.All);
    }

    [Fact]
    public async Task Mpv_UnusableLines_AreIgnored()
    {
        await using var session = await StartConnectedAsync();

        await _pipe.FeedAsync("");
        await _pipe.FeedAsync("not json");
        await _pipe.FeedAsync("""{"error":"success","request_id":0}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos"}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos","data":null}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos","data":-1}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":2,"name":"duration","data":"soon"}""");
        await _pipe.FeedAsync("""{"event":"file-loaded"}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":9,"name":"volume","data":50}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"pause","data":true}""");

        Assert.Equal([new PlaybackEvent.Paused()], _events.All);
    }

    [Fact]
    public async Task Mpv_RepeatedPauseState_RaisesOnce()
    {
        await using var session = await StartConnectedAsync();

        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"pause","data":false}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"pause","data":false}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"pause","data":true}""");

        Assert.Equal([new PlaybackEvent.Playing(), new PlaybackEvent.Paused()], _events.All);
    }

    [Fact]
    public async Task Mpv_DurationReportedAgain_LoadsOnce()
    {
        await using var session = await StartConnectedAsync();

        await _pipe.FeedAsync("""{"event":"property-change","id":2,"name":"duration","data":1500}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":2,"name":"duration","data":1500.04}""");

        Assert.Single(_events.OfType<PlaybackEvent.Loaded>());
    }

    [Fact]
    public async Task Mpv_Positions_AreThrottledToOnePerSecond()
    {
        await using var session = await StartConnectedAsync();

        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos","data":10.0}""");
        _clock.Advance(TimeSpan.FromMilliseconds(250));
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos","data":10.25}""");
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos","data":10.75}""");
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos","data":11.05}""");
        _clock.Advance(TimeSpan.FromMilliseconds(950));
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos","data":12.0}""");
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos","data":12.05}""");

        Assert.Equal(
            [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11.05), TimeSpan.FromSeconds(12.05)],
            _events.OfType<PlaybackEvent.Position>().Select(p => p.At));
    }

    [Fact]
    public async Task Connect_RetriesUntilMpvCreatesThePipe()
    {
        _connectFailures = 3;
        await using var session = Create();

        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);
        await _pipe.WaitUntilReading();

        Assert.Equal(4, _connectNames.Count);
        Assert.Single(_connectNames.Distinct());
        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task Connect_NeverSucceeds_RaisesFailed()
    {
        _connectFailures = int.MaxValue;
        await using var session = Create(connectTimeout: TimeSpan.FromMilliseconds(80));

        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);

        var failed = await _events.WaitFor<PlaybackEvent.Failed>();
        Assert.Equal("Could not connect to mpv", failed.Message);
    }

    [Fact]
    public async Task Connect_ProcessExitsFirst_StopsRetrying()
    {
        _connectFailures = int.MaxValue;
        await using var session = Create(connectTimeout: TimeSpan.FromSeconds(30));
        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);

        _process.Exit(2);

        var failed = await _events.WaitFor<PlaybackEvent.Failed>();
        Assert.Equal("mpv closed right after starting", failed.Message);
        await Task.Delay(100, Ct);
        var attempts = _connectNames.Count;
        await Task.Delay(100, Ct);
        Assert.Equal(attempts, _connectNames.Count);
        Assert.Single(_events.All);
    }

    [Fact]
    public async Task Session_MissingExecutable_RaisesFailed()
    {
        await using var session = Create(start: _ => throw new Win32Exception(2));

        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);

        Assert.Equal([new PlaybackEvent.Failed($"mpv not found: {Exe}")], _events.All);
        Assert.Empty(_connectNames);
    }

    [Fact]
    public async Task Session_OtherLaunchError_RaisesFailed()
    {
        await using var session = Create(start: _ => throw new InvalidOperationException("boom"));

        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);

        Assert.Equal([new PlaybackEvent.Failed("mpv could not start: boom")], _events.All);
    }

    [Fact]
    public async Task Session_ProcessExitAfterConnect_RaisesClosed()
    {
        await using var session = await StartConnectedAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));

        _process.Exit();

        Assert.Equal(new PlaybackEvent.Closed(), await _events.WaitFor<PlaybackEvent.Closed>());
    }

    [Fact]
    public async Task Session_CrashAfterConnect_RaisesFailed()
    {
        await using var session = await StartConnectedAsync();
        _clock.Advance(TimeSpan.FromSeconds(10));

        _process.Exit(3);

        var failed = await _events.WaitFor<PlaybackEvent.Failed>();
        Assert.Equal("mpv stopped unexpectedly", failed.Message);
    }

    [Fact]
    public async Task Session_NothingFollowsAFinalEvent()
    {
        await using var session = await StartConnectedAsync();
        await _pipe.FeedAsync("""{"event":"end-file","reason":"error"}""");

        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"pause","data":true}""");
        _process.Exit(2);
        await Task.Delay(50, Ct);

        Assert.Equal([new PlaybackEvent.Failed("mpv could not open the file")], _events.All);
    }

    [Fact]
    public async Task Session_PipeClosing_LeavesTheFinalEventToTheProcess()
    {
        await using var session = await StartConnectedAsync();

        _pipe.EndOfStream();
        await Task.Delay(50, Ct);
        Assert.Empty(_events.All);

        _process.Exit();
        await _events.WaitFor<PlaybackEvent.Closed>();
    }

    [Fact]
    public async Task Session_EndFileJustBeforeExit_IsDeliveredBeforeClosed()
    {
        await using var session = await StartConnectedAsync();

        _pipe.Post("""{"event":"end-file","reason":"eof","playlist_entry_id":1}""");
        _pipe.EndOfStream();
        _process.Exit();

        await _events.WaitFor<PlaybackEvent.Closed>();
        Assert.Equal([new PlaybackEvent.Ended(), new PlaybackEvent.Closed()], _events.All);
    }

    [Fact]
    public async Task Handler_Throwing_StillGetsFinalEvent()
    {
        await using var session = Create();
        var first = true;
        session.Event += e =>
        {
            if (e is PlaybackEvent.Playing && first)
            {
                first = false;
                throw new InvalidOperationException("handler bug");
            }
        };
        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);
        await _pipe.WaitUntilReading();

        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"pause","data":false}""");
        _clock.Advance(TimeSpan.FromSeconds(5));
        _process.Exit();

        await _events.WaitFor<PlaybackEvent.Closed>();
        Assert.Equal([new PlaybackEvent.Playing(), new PlaybackEvent.Closed()], _events.All);
    }

    [Fact]
    public async Task GetPosition_IsTheLastTimePos_NotThrottled()
    {
        await using var session = await StartConnectedAsync();
        Assert.Null(await session.GetPositionAsync(Ct));

        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos","data":10.0}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos","data":10.4}""");

        Assert.Equal(TimeSpan.FromSeconds(10.4), await session.GetPositionAsync(Ct));
        Assert.Single(_events.OfType<PlaybackEvent.Position>());
    }

    [Fact]
    public async Task GetPosition_AfterTheSessionEnded_IsNull()
    {
        await using var session = await StartConnectedAsync();
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"time-pos","data":10.0}""");
        _clock.Advance(TimeSpan.FromSeconds(5));
        _process.Exit();
        await _events.WaitFor<PlaybackEvent.Closed>();

        Assert.Null(await session.GetPositionAsync(Ct));
    }

    [Fact]
    public async Task Dispose_ClosesProcessItStarted()
    {
        var session = await StartConnectedAsync();

        await session.DisposeAsync();

        Assert.True(_process.CloseRequested);
        Assert.False(_process.Killed);
        Assert.True(_process.Disposed);
        Assert.True(_pipe.Disposed);
        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task Detach_ThenDispose_LeavesPlayerRunning()
    {
        var session = await StartConnectedAsync();

        session.Detach();
        await session.DisposeAsync();

        Assert.False(_process.CloseRequested);
        Assert.False(_process.Killed);
        Assert.True(_process.Disposed);
        Assert.True(_pipe.Disposed);
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
        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task Dispose_BeforeStart_DoesNothing()
    {
        var session = Create();

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.False(_process.CloseRequested);
    }

    [Fact]
    public async Task Dispose_WhileConnecting_StopsRetrying()
    {
        _connectFailures = int.MaxValue;
        var session = Create(connectTimeout: TimeSpan.FromSeconds(30));
        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);

        await session.DisposeAsync();
        var attempts = _connectNames.Count;
        await Task.Delay(100, Ct);

        Assert.Equal(attempts, _connectNames.Count);
        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task Start_TwiceOrAfterDispose_Throws()
    {
        var session = Create();
        await session.StartAsync(@"C:\Videos\test.mp4", null, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(@"C:\Videos\other.mp4", null, Ct));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync(@"C:\Videos\other.mp4", null, Ct));
    }

    [Fact]
    public void Protocol_ParsesProperties()
    {
        Assert.Equal(new MpvMessage.TimePos(TimeSpan.FromSeconds(3.5)), MpvProtocol.Parse("""{"event":"property-change","id":1,"name":"time-pos","data":3.5}"""));
        Assert.Equal(new MpvMessage.Duration(TimeSpan.FromSeconds(60)), MpvProtocol.Parse("""{"event":"property-change","id":2,"name":"duration","data":60}"""));
        Assert.Equal(new MpvMessage.Pause(true), MpvProtocol.Parse("""{"event":"property-change","id":3,"name":"pause","data":true}"""));
        Assert.Equal(new MpvMessage.EofReached(false), MpvProtocol.Parse("""{"event":"property-change","id":4,"name":"eof-reached","data":false}"""));
        Assert.Equal(new MpvMessage.EndFile("eof"), MpvProtocol.Parse("""{"event":"end-file","reason":"eof"}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[1,2]")]
    [InlineData("42")]
    [InlineData("""{"event":"property-change","name":"time-pos","data":"3"}""")]
    [InlineData("""{"event":"property-change","name":"pause","data":1}""")]
    [InlineData("""{"event":"property-change","name":"duration","data":-5}""")]
    [InlineData("""{"event":"property-change","name":"duration","data":1e400}""")]
    [InlineData("""{"event":"end-file"}""")]
    [InlineData("""{"event":"seek"}""")]
    public void Protocol_IgnoresWhatItCannotUse(string line)
    {
        Assert.Null(MpvProtocol.Parse(line));
    }

    [Fact]
    public void Protocol_BuildsObserveCommands()
    {
        Assert.Equal("""{"command":["observe_property",7,"time-pos"]}""", MpvProtocol.Observe(7, "time-pos"));
    }

    private sealed class FakePipe : IMpvPipe
    {
        private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();
        private readonly List<string> _written = [];
        private int _reads;

        public bool Disposed { get; private set; }

        public List<string> Written
        {
            get { lock (_written) return [.. _written]; }
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            try
            {
                return await _lines.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public Task WriteLineAsync(string line, CancellationToken ct)
        {
            lock (_written) _written.Add(line);
            return Task.CompletedTask;
        }

        public Task WaitUntilReading() => Waiting.Until(() => Volatile.Read(ref _reads) >= 1);

        /// <summary>Hands the session a line and returns once it has been handled and the session asks for the next.</summary>
        public async Task FeedAsync(string line)
        {
            await WaitUntilReading();
            var before = Volatile.Read(ref _reads);
            _lines.Writer.TryWrite(line);
            await Waiting.Until(() => Volatile.Read(ref _reads) > before);
        }

        /// <summary>Queues a line without waiting for the session to handle it.</summary>
        public void Post(string line) => _lines.Writer.TryWrite(line);

        public void EndOfStream() => _lines.Writer.TryComplete();

        public void Dispose()
        {
            Disposed = true;
            _lines.Writer.TryComplete();
        }
    }
}
