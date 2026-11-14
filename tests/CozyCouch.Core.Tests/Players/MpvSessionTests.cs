using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using CozyCouch.Core.Players;
using CozyCouch.Core.Players.Mpv;

namespace CozyCouch.Core.Tests.Players;

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
        Assert.Matches(@"^--input-ipc-server=\\\\\.\\pipe\\cozycouch-[0-9a-f]{32}$", args[0]);
        Assert.Equal(["--force-window=yes", "--start=754.5", file], args.Skip(1));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(90.25, "--start=90.25")] // the decimal point must not follow the machine's culture
    public async Task Session_StartAt_IsOmittedWhenNull_AndInvariantCultureOtherwise(double? seconds, string? expected)
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("uk-UA");
        try
        {
            await using var session = Create();
            await session.StartAsync(@"C:\Videos\test.mp4", seconds is { } s ? TimeSpan.FromSeconds(s) : null, Ct);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }

        Assert.NotNull(_startInfo);
        if (expected is null)
            Assert.DoesNotContain(_startInfo.ArgumentList, a => a.StartsWith("--start", StringComparison.Ordinal));
        else
            Assert.Contains(expected, _startInfo.ArgumentList);
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
            """{"command":["observe_property",1,"path"]}""",
            """{"command":["observe_property",2,"time-pos"]}""",
            """{"command":["observe_property",3,"duration"]}""",
            """{"command":["observe_property",4,"pause"]}""",
            """{"command":["observe_property",5,"eof-reached"]}""",
        ], _pipe.Written);
    }

    private MpvSession CreateAttached(string pipeName = "cozycouch-mpv")
    {
        var session = new MpvSession(
            Player,
            (name, _) =>
            {
                lock (_connectNames) _connectNames.Add(name);
                return Task.FromResult<IMpvPipe?>(_connectFailures-- > 0 ? null : _pipe);
            },
            _ => throw new InvalidOperationException("An attached session starts no process."),
            _clock,
            attachPipeName: pipeName);
        session.Event += _events.Add;
        return session;
    }

    [Fact]
    public async Task Attach_ConnectsOnceToTheFixedPipe_AndReportsTheFileMpvHasOpen()
    {
        await using var session = CreateAttached();

        Assert.True(await session.AttachAsync(Ct));
        await _pipe.WaitUntilReading();
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"path","data":"C:\\Videos\\test.mp4"}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"duration","data":1500}""");
        await _events.WaitFor<PlaybackEvent.Loaded>();

        Assert.Equal("cozycouch-mpv", Assert.Single(_connectNames));
        Assert.Equal(5, _pipe.Written.Count);
        Assert.Equal(
        [
            new PlaybackEvent.FileChanged(@"C:\Videos\test.mp4"),
            new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1500)),
        ], _events.All);
    }

    [Fact]
    public async Task Attach_NoPipe_ReturnsFalse_WithoutRetrying()
    {
        _connectFailures = 1;
        await using var session = CreateAttached();

        Assert.False(await session.AttachAsync(Ct));

        Assert.Single(_connectNames);
        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task Attach_MpvClosesThePipe_RaisesClosed()
    {
        await using var session = CreateAttached();
        await session.AttachAsync(Ct);
        await _pipe.WaitUntilReading();

        _pipe.EndOfStream();

        await _events.WaitFor<PlaybackEvent.Closed>();
    }

    [Fact]
    public async Task Attach_Dispose_RaisesNothing_AndNeverClosesMpv()
    {
        var session = CreateAttached();
        await session.AttachAsync(Ct);
        await _pipe.WaitUntilReading();

        await session.DisposeAsync();
        await Task.Delay(50, Ct);

        Assert.Empty(_events.All);
        Assert.False(_process.CloseRequested);
    }

    [Fact]
    public async Task Attach_Twice_Throws()
    {
        await using var session = CreateAttached();
        await session.AttachAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AttachAsync(Ct));
    }

    [Fact]
    public async Task Mpv_SameFileReported_RaisesNothing()
    {
        await using var session = await StartConnectedAsync();

        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"path","data":"c:/videos/TEST.mp4"}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"duration","data":1500}""");

        await _events.WaitFor<PlaybackEvent.Loaded>();
        Assert.Equal([new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1500))], _events.All);
    }

    [Fact]
    public async Task Mpv_MovesToAnotherFile_RaisesFileChangedAndLoadsAgain()
    {
        await using var session = await StartConnectedAsync();
        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"duration","data":1500}""");
        await _events.WaitFor<PlaybackEvent.Loaded>();

        await _pipe.FeedAsync("""{"event":"end-file","reason":"eof"}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"path","data":"C:\\Videos\\test2.mp4"}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"duration","data":1300}""");
        await Waiting.Until(() => _events.OfType<PlaybackEvent.Loaded>().Count == 2);

        Assert.Equal(
        [
            new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1500)),
            new PlaybackEvent.Ended(),
            new PlaybackEvent.FileChanged(@"C:\Videos\test2.mp4"),
            new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1300)),
        ], _events.All);
    }

    [Fact]
    public async Task Mpv_FileUnloaded_RaisesFileChangedToNothing()
    {
        await using var session = await StartConnectedAsync();
        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"path","data":"C:\\Videos\\test.mp4"}""");

        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"path"}""");
        await _events.WaitFor<PlaybackEvent.FileChanged>();

        Assert.Equal([new PlaybackEvent.FileChanged(null)], _events.All);
    }

    [Fact]
    public async Task Mpv_NoPathYet_IsNotAFileClosing()
    {
        await using var session = await StartConnectedAsync();

        await _pipe.FeedAsync("""{"event":"property-change","id":1,"name":"path"}""");
        await _pipe.FeedAsync("""{"event":"property-change","id":3,"name":"duration","data":1500}""");
        await _events.WaitFor<PlaybackEvent.Loaded>();

        Assert.Equal([new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1500))], _events.All);
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

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[1,2]")]
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
