using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using MediaBoard.Core.Players;
using MediaBoard.Core.Players.Vlc;

namespace MediaBoard.Core.Tests.Players;

public class VlcSessionTests
{
    private const string Exe = @"C:\Program Files\VideoLAN\VLC\vlc.exe";
    private const string File = @"C:\Videos\test.mp4";
    private static readonly PlayerInfo Player = new("vlc", "VLC", Exe, TracksProgress: true);
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Slow = TimeSpan.FromHours(1);

    private readonly FakePlayerProcess _process = new();
    private readonly PlaybackLog _events = new();
    private readonly ManualClock _clock = new();
    private readonly List<(int Port, string Password)> _requests = [];
    private string _status = Status("stopped", 0, 0);
    private bool _httpDown;
    private ProcessStartInfo? _startInfo;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Status(string state, int time, int length) =>
        $$"""{"state":"{{state}}","time":{{time}},"length":{{length}},"volume":256}""";

    private int RequestCount
    {
        get { lock (_requests) return _requests.Count; }
    }

    private VlcSession Create(
        TimeSpan? pollInterval = null,
        TimeSpan? closeTimeout = null,
        Func<ProcessStartInfo, IPlayerProcess>? start = null)
    {
        var session = new VlcSession(
            Player,
            (port, password, _) =>
            {
                lock (_requests) _requests.Add((port, password));
                return _httpDown
                    ? Task.FromException<string>(new HttpRequestException("connection refused"))
                    : Task.FromResult(_status);
            },
            start ?? (psi =>
            {
                _startInfo = psi;
                return _process;
            }),
            _clock,
            pollInterval ?? Fast,
            closeTimeout: closeTimeout);
        session.Event += _events.Add;
        return session;
    }

    [Fact]
    public async Task Session_StartsVlcWithHttpInterfaceAndArgumentList()
    {
        const string file = @"C:\Відео\Bob's Burgers (2011)\S01E01.mkv";
        await using var session = Create();

        await session.StartAsync(file, TimeSpan.FromSeconds(754.5), Ct);

        Assert.NotNull(_startInfo);
        Assert.Equal(Exe, _startInfo.FileName);
        Assert.False(_startInfo.UseShellExecute);
        var args = _startInfo.ArgumentList;
        Assert.Equal(["--extraintf", "http", "--http-host", "127.0.0.1", "--http-port"], args.Take(5));
        Assert.InRange(int.Parse(args[5]), 1024, 65535);
        Assert.Equal("--http-password", args[6]);
        Assert.Matches("^[A-Za-z0-9]{24}$", args[7]);
        Assert.Equal(["--start-time=754.5", file], args.Skip(8));
    }

    [Fact]
    public async Task Session_NoStartAt_OmitsStartTime()
    {
        await using var session = Create();

        await session.StartAsync(File, null, Ct);

        Assert.DoesNotContain(_startInfo!.ArgumentList, a => a.StartsWith("--start-time", StringComparison.Ordinal));
        Assert.Equal(File, _startInfo.ArgumentList[^1]);
    }

    [Fact]
    public async Task Session_StartAtIsInvariantCulture()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("uk-UA");
        try
        {
            await using var session = Create();
            await session.StartAsync(File, TimeSpan.FromSeconds(90.25), Ct);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }

        Assert.Contains("--start-time=90.25", _startInfo!.ArgumentList);
    }

    [Fact]
    public async Task Session_EachSessionGetsItsOwnPasswordAndPort()
    {
        await using var first = Create();
        await first.StartAsync(File, null, Ct);
        var firstArgs = _startInfo!.ArgumentList.ToList();

        await using var second = Create();
        await second.StartAsync(File, null, Ct);
        var secondArgs = _startInfo.ArgumentList.ToList();

        Assert.NotEqual(firstArgs[7], secondArgs[7]);
    }

    [Fact]
    public async Task Vlc_ParsesStatusJson()
    {
        _status = Status("playing", 120, 1320);
        await using var session = Create(pollInterval: Slow);

        await session.StartAsync(File, null, Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        Assert.Equal(
        [
            new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1320)),
            new PlaybackEvent.Playing(),
            new PlaybackEvent.Position(TimeSpan.FromSeconds(120)),
        ], _events.All);
        var args = _startInfo!.ArgumentList;
        Assert.Equal((int.Parse(args[5]), args[7]), Assert.Single(_requests));
    }

    [Fact]
    public async Task Vlc_PlayPauseTransitions_RaiseOnce()
    {
        _status = Status("playing", 10, 600);
        await using var session = Create();
        await session.StartAsync(File, null, Ct);
        await _events.WaitFor<PlaybackEvent.Playing>();

        _status = Status("paused", 14, 600);
        await _events.WaitFor<PlaybackEvent.Paused>();
        var requests = RequestCount;
        await Waiting.Until(() => RequestCount >= requests + 3);

        _status = Status("playing", 14, 600);
        await Waiting.Until(() => _events.OfType<PlaybackEvent.Playing>().Count == 2);

        Assert.Single(_events.OfType<PlaybackEvent.Loaded>());
        Assert.Single(_events.OfType<PlaybackEvent.Paused>());
        Assert.Contains(new PlaybackEvent.Position(TimeSpan.FromSeconds(14)), _events.All);
    }

    [Fact]
    public async Task Vlc_PausedPosition_IsReportedOnce()
    {
        _status = Status("paused", 33, 600);
        await using var session = Create();
        await session.StartAsync(File, null, Ct);
        await _events.WaitFor<PlaybackEvent.Position>();
        var requests = RequestCount;
        await Waiting.Until(() => RequestCount >= requests + 3);

        Assert.Equal(
        [
            new PlaybackEvent.Loaded(TimeSpan.FromSeconds(600)),
            new PlaybackEvent.Paused(),
            new PlaybackEvent.Position(TimeSpan.FromSeconds(33)),
        ], _events.All);
    }

    [Fact]
    public async Task Vlc_StoppedAfterPlaying_RaisesEnded()
    {
        _status = Status("playing", 1300, 1320);
        await using var session = Create();
        await session.StartAsync(File, null, Ct);
        await _events.WaitFor<PlaybackEvent.Playing>();

        _status = Status("stopped", 0, 0);

        await _events.WaitFor<PlaybackEvent.Ended>();
        var requests = RequestCount;
        await Waiting.Until(() => RequestCount >= requests + 3);
        Assert.Single(_events.OfType<PlaybackEvent.Ended>());
    }

    [Fact]
    public async Task Vlc_StoppedBeforePlaying_RaisesNothing()
    {
        _status = Status("stopped", 0, 0);
        await using var session = Create();

        await session.StartAsync(File, null, Ct);
        await Waiting.Until(() => RequestCount >= 4);

        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task Vlc_PlayingAgainAfterEnd_CanEndAgain()
    {
        _status = Status("playing", 100, 120);
        await using var session = Create();
        await session.StartAsync(File, null, Ct);
        await _events.WaitFor<PlaybackEvent.Playing>();
        _status = Status("stopped", 0, 0);
        await _events.WaitFor<PlaybackEvent.Ended>();

        _status = Status("playing", 5, 120);
        await Waiting.Until(() => _events.OfType<PlaybackEvent.Playing>().Count == 2);
        _status = Status("stopped", 0, 0);

        await Waiting.Until(() => _events.OfType<PlaybackEvent.Ended>().Count == 2);
        Assert.Single(_events.OfType<PlaybackEvent.Loaded>());
    }

    [Fact]
    public async Task Vlc_HttpFailureWhileAlive_IsIgnoredUntilVlcAnswers()
    {
        _httpDown = true;
        await using var session = Create();
        await session.StartAsync(File, null, Ct);
        await Waiting.Until(() => RequestCount >= 4);
        Assert.Empty(_events.All);

        _status = Status("playing", 7, 90);
        _httpDown = false;

        await _events.WaitFor<PlaybackEvent.Position>();
        Assert.Equal(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(90)), _events.All[0]);
    }

    [Fact]
    public async Task Vlc_UnusableAnswers_AreIgnored()
    {
        await using var session = Create();
        await session.StartAsync(File, null, Ct);

        foreach (var junk in new[] { "", "<html>", "{}", """{"state":"buffering","time":1,"length":2}""" })
        {
            _status = junk;
            var requests = RequestCount;
            await Waiting.Until(() => RequestCount >= requests + 2);
        }

        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task Vlc_DoesNotPollFasterThanTheIntervalOnceItAnswers()
    {
        _status = Status("playing", 1, 60);
        await using var session = Create(pollInterval: TimeSpan.FromMilliseconds(300));
        await session.StartAsync(File, null, Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        await Task.Delay(200, Ct);

        Assert.Equal(1, RequestCount);
    }

    [Fact]
    public async Task Session_MissingExecutable_RaisesFailed()
    {
        await using var session = Create(start: _ => throw new Win32Exception(2));

        await session.StartAsync(File, null, Ct);

        Assert.Equal([new PlaybackEvent.Failed($"VLC not found: {Exe}")], _events.All);
        Assert.Equal(0, RequestCount);
    }

    [Fact]
    public async Task Session_OtherLaunchError_RaisesFailed()
    {
        await using var session = Create(start: _ => throw new InvalidOperationException("boom"));

        await session.StartAsync(File, null, Ct);

        Assert.Equal([new PlaybackEvent.Failed("VLC could not start: boom")], _events.All);
    }

    [Fact]
    public async Task Session_EarlyExit_RaisesFailed()
    {
        _httpDown = true;
        await using var session = Create();
        await session.StartAsync(File, null, Ct);

        _process.Exit(1);

        var failed = await _events.WaitFor<PlaybackEvent.Failed>();
        Assert.Equal("VLC closed right after starting", failed.Message);
    }

    [Fact]
    public async Task Session_ProcessExitAfterAnswering_RaisesClosedAndStopsPolling()
    {
        _status = Status("playing", 5, 60);
        await using var session = Create();
        await session.StartAsync(File, null, Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        _process.Exit();

        await _events.WaitFor<PlaybackEvent.Closed>();
        await Task.Delay(100, Ct);
        var requests = RequestCount;
        await Task.Delay(100, Ct);
        Assert.Equal(requests, RequestCount);
        Assert.IsType<PlaybackEvent.Closed>(_events.All[^1]);
        Assert.Single(_events.OfType<PlaybackEvent.Closed>());
    }

    [Fact]
    public async Task Session_CrashAfterAnswering_RaisesFailed()
    {
        _status = Status("playing", 5, 60);
        await using var session = Create();
        await session.StartAsync(File, null, Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        _process.Exit(-1073741819);

        var failed = await _events.WaitFor<PlaybackEvent.Failed>();
        Assert.Equal("VLC stopped unexpectedly", failed.Message);
    }

    [Fact]
    public async Task Session_NothingFollowsAFinalEvent()
    {
        _status = Status("playing", 5, 60);
        await using var session = Create();
        await session.StartAsync(File, null, Ct);
        await _events.WaitFor<PlaybackEvent.Position>();
        _process.Exit();
        await _events.WaitFor<PlaybackEvent.Closed>();
        var count = _events.All.Count;

        _status = Status("paused", 9, 60);
        await Task.Delay(100, Ct);

        Assert.Equal(count, _events.All.Count);
    }

    [Fact]
    public async Task Handler_Throwing_StillGetsFinalEvent()
    {
        _status = Status("playing", 5, 60);
        await using var session = Create(pollInterval: Slow);
        session.Event += e =>
        {
            if (e is PlaybackEvent.Loaded) throw new InvalidOperationException("handler bug");
        };

        await session.StartAsync(File, null, Ct);
        await _events.WaitFor<PlaybackEvent.Position>();
        _process.Exit();

        await _events.WaitFor<PlaybackEvent.Closed>();
    }

    [Fact]
    public async Task GetPosition_AsksVlcForTheCurrentTime()
    {
        _status = Status("playing", 77, 600);
        await using var session = Create(pollInterval: Slow);
        Assert.Null(await session.GetPositionAsync(Ct));
        await session.StartAsync(File, null, Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        _status = Status("playing", 81, 600);

        Assert.Equal(TimeSpan.FromSeconds(81), await session.GetPositionAsync(Ct));
    }

    [Fact]
    public async Task GetPosition_WhenVlcCannotAnswer_IsNull()
    {
        await using var session = Create(pollInterval: Slow);
        await session.StartAsync(File, null, Ct);
        _httpDown = true;

        Assert.Null(await session.GetPositionAsync(Ct));
    }

    [Fact]
    public async Task GetPosition_VlcNotPlaying_IsNull()
    {
        _status = Status("stopped", 0, 0);
        await using var session = Create(pollInterval: Slow);
        await session.StartAsync(File, null, Ct);

        Assert.Null(await session.GetPositionAsync(Ct));
    }

    [Fact]
    public async Task Dispose_ClosesProcessItStartedAndStopsPolling()
    {
        var session = Create();
        await session.StartAsync(File, null, Ct);
        await Waiting.Until(() => RequestCount >= 2);

        await session.DisposeAsync();

        Assert.True(_process.CloseRequested);
        Assert.False(_process.Killed);
        Assert.True(_process.Disposed);
        var requests = RequestCount;
        await Task.Delay(100, Ct);
        Assert.Equal(requests, RequestCount);
        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task Dispose_KillsPlayerThatIgnoresClose()
    {
        _process.ExitOnClose = false;
        var session = Create(closeTimeout: TimeSpan.FromMilliseconds(50));
        await session.StartAsync(File, null, Ct);

        await session.DisposeAsync();

        Assert.True(_process.CloseRequested);
        Assert.True(_process.Killed);
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
    public async Task Start_TwiceOrAfterDispose_Throws()
    {
        var session = Create();
        await session.StartAsync(File, null, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(File, null, Ct));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync(File, null, Ct));
    }

    [Fact]
    public void Parser_ReadsAStatusWithManyOtherFields()
    {
        const string json = """
            {"fullscreen":0,"stats":{"readbytes":1,"lostabuffers":0},"aspectratio":"default","audiodelay":0,
             "apiversion":3,"currentplid":6,"time":120,"volume":256,"length":1320,"random":false,
             "audiofilters":{"filter_0":""},"rate":1,"videoeffects":{"hue":0},"state":"playing","loop":false,
             "version":"3.0.20 Vetinari","position":0.09,"repeat":false,"subtitledelay":0,"equalizer":[]}
            """;

        Assert.True(VlcStatus.TryParse(json, out var status));
        Assert.Equal(new VlcStatus(VlcState.Playing, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(1320)), status);
    }

    [Theory]
    [InlineData("playing", VlcState.Playing)]
    [InlineData("paused", VlcState.Paused)]
    [InlineData("stopped", VlcState.Stopped)]
    public void Parser_MapsStates(string state, VlcState expected)
    {
        Assert.True(VlcStatus.TryParse($$"""{"state":"{{state}}","time":3,"length":9}""", out var status));
        Assert.Equal(expected, status.State);
    }

    [Fact]
    public void Parser_MissingTimeAndLength_AreZero_AndNegativesToo()
    {
        Assert.True(VlcStatus.TryParse("""{"state":"stopped"}""", out var none));
        Assert.Equal(new VlcStatus(VlcState.Stopped, TimeSpan.Zero, TimeSpan.Zero), none);

        Assert.True(VlcStatus.TryParse("""{"state":"playing","time":-1,"length":-1}""", out var negative));
        Assert.Equal(new VlcStatus(VlcState.Playing, TimeSpan.Zero, TimeSpan.Zero), negative);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"state":"buffering"}""")]
    [InlineData("""{"state":7}""")]
    [InlineData("""{"state":"playing","time":"soon","length":9}""")]
    public void Parser_RejectsWhatItCannotUse(string json)
    {
        Assert.False(VlcStatus.TryParse(json, out _));
    }

    [Fact]
    public async Task Client_RequestsStatusWithBasicAuthAndEmptyUser()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, Status("playing", 1, 2));
        var client = new VlcStatusClient(handler);

        var body = await client.FetchAsync(8080, "s3cret", Ct);

        Assert.Equal(Status("playing", 1, 2), body);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:8080/requests/status.json", request.Uri);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":s3cret")), request.Authorization);
    }

    [Fact]
    public async Task Client_NonSuccessStatus_Throws()
    {
        var client = new VlcStatusClient(new RecordingHandler(HttpStatusCode.Unauthorized, ""));

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => client.FetchAsync(8080, "s3cret", Ct));

        Assert.DoesNotContain("s3cret", thrown.Message);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
