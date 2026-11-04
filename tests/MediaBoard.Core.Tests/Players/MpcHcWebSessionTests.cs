using System.Net;
using System.Text;
using MediaBoard.Core.Players;
using MediaBoard.Core.Players.MpcHc;

namespace MediaBoard.Core.Tests.Players;

public class MpcHcWebSessionTests
{
    private const string File = @"C:\Videos\test.mp4";
    private const string Next = @"C:\Videos\My Show\S01E02.mkv";
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);

    private readonly PlaybackLog _events = new();
    private readonly List<int> _requests = [];
    private string _page = Page("", 0, 0, 0);
    private bool _down;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Page(string path, int state, int positionMs, int durationMs) =>
        $"""
        <!DOCTYPE html><html><head><title>MPC-HC WebServer - Variables</title></head><body class="page-variables">
        <p id="file">{Path.GetFileName(path)}</p>
        <p id="filepatharg">{Uri.EscapeDataString(path)}</p>
        <p id="filepath">{path}</p>
        <p id="filedirarg"></p>
        <p id="filedir">{Path.GetDirectoryName(path)}</p>
        <p id="state">{state}</p>
        <p id="statestring">Playing</p>
        <p id="position">{positionMs}</p>
        <p id="positionstring">00:00:01</p>
        <p id="duration">{durationMs}</p>
        <p id="durationstring">00:25:00</p>
        <p id="volumelevel">100</p>
        <p id="muted">0</p>
        <p id="playbackrate">1</p>
        <p id="size">1 MB</p>
        <p id="reloadtime">0</p>
        <p id="version">2.5.0</p>
        <p id="audiotrack">1: English</p>
        <p id="subtitletrack"></p>
        </body></html>
        """;

    private MpcHcWebSession Create(int port = 13579, TimeSpan? pollInterval = null) => Attach(new MpcHcWebSession(
        "MPC-HC",
        port,
        (p, _) =>
        {
            lock (_requests) _requests.Add(p);
            return _down
                ? Task.FromException<string>(new HttpRequestException("connection refused"))
                : Task.FromResult(_page);
        },
        pollInterval ?? Fast));

    private MpcHcWebSession Attach(MpcHcWebSession session)
    {
        session.Event += _events.Add;
        return session;
    }

    private int RequestCount
    {
        get { lock (_requests) return _requests.Count; }
    }

    // ---- The page ----

    [Fact]
    public void Parser_ReadsTheFileStateAndMilliseconds()
    {
        Assert.True(MpcWebStatus.TryParse(Page(File, 2, 90_500, 1_500_000), out var status));

        Assert.Equal(new MpcWebStatus(File, MpcWebState.Playing, TimeSpan.FromSeconds(90.5), TimeSpan.FromMinutes(25)), status);
    }

    [Theory]
    [InlineData(0, MpcWebState.Stopped)]
    [InlineData(1, MpcWebState.Paused)]
    public void Parser_MapsStates(int number, MpcWebState expected)
    {
        Assert.True(MpcWebStatus.TryParse(Page(File, number, 1000, 9000), out var status));
        Assert.Equal(expected, status.State);
    }

    [Fact]
    public void Parser_UnknownState_IsNull()
    {
        Assert.True(MpcWebStatus.TryParse(Page(File, -1, 0, 0), out var status));
        Assert.Null(status.State);
    }

    [Fact]
    public void Parser_PathIsTakenAsItStands_WithAmpersandsAndNonLatinLetters()
    {
        const string path = @"C:\Відео\Tom & Jerry (1940)\S01E01.mkv";

        Assert.True(MpcWebStatus.TryParse(Page(path, 2, 0, 1000), out var status));

        Assert.Equal(path, status.FilePath);
    }

    [Theory]
    [InlineData("<p id=\"state\">2</p><p id=\"position\">oops</p><p id=\"duration\">-5</p>")]
    [InlineData("<p id=\"state\">2</p>")]
    public void Parser_MissingOrBadPositionAndLength_AreZero(string html)
    {
        Assert.True(MpcWebStatus.TryParse(html, out var status));
        Assert.Equal(TimeSpan.Zero, status.Position);
        Assert.Equal(TimeSpan.Zero, status.Duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Hello</body></html>")]
    [InlineData("<p id=\"state\">playing</p>")]
    [InlineData("<p id=\"state\">2")]
    public void Parser_NotTheVariablesPage_IsRefused(string html)
    {
        Assert.False(MpcWebStatus.TryParse(html, out _));
    }

    [Fact]
    public async Task Client_AsksTheVariablesPageOnLoopback_AndReadsItAsUtf8()
    {
        const string body = "<p id=\"state\">2</p><p id=\"filepath\">C:\\Відео\\a.mkv</p>";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) });
        var client = new MpcWebClient(handler);

        var text = await client.FetchAsync(13579, Ct);

        Assert.Equal(body, text);
        Assert.Equal("http://127.0.0.1:13579/variables.html", handler.Requests.Single());
    }

    // ---- The session ----

    [Fact]
    public async Task Attach_PlayerHasAFileOpen_ReportsItUsingThePortGiven()
    {
        _page = Page(File, 2, 120_000, 1_500_000);
        await using var session = Create(port: 9090);

        Assert.True(await session.AttachAsync(Ct));
        await _events.WaitFor<PlaybackEvent.Position>();

        Assert.Equal(
        [
            new PlaybackEvent.FileChanged(File),
            new PlaybackEvent.Loaded(TimeSpan.FromMinutes(25)),
            new PlaybackEvent.Playing(),
            new PlaybackEvent.Position(TimeSpan.FromSeconds(120)),
        ], _events.All.Take(4));
        Assert.All(_requests.ToArray(), port => Assert.Equal(9090, port));
        Assert.Equal("MPC-HC", session.PlayerName);
    }

    [Fact]
    public async Task Attach_PlayerWithNoFile_ReportsNothing()
    {
        await using var session = Create();

        Assert.True(await session.AttachAsync(Ct));
        var requests = RequestCount;
        await Waiting.Until(() => RequestCount >= requests + 3);

        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task Attach_NothingAnswers_ReturnsFalse()
    {
        _down = true;
        await using var session = Create();

        Assert.False(await session.AttachAsync(Ct));

        Assert.Equal(1, RequestCount);
        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task Attach_SomethingElseOnThePort_ReturnsFalse()
    {
        _page = "<html><body>Not a player</body></html>";
        await using var session = Create();

        Assert.False(await session.AttachAsync(Ct));
    }

    [Fact]
    public async Task Playing_MovesToAnotherFile_RaisesFileChangedBeforeTheNewPosition()
    {
        _page = Page(File, 2, 10_000, 600_000);
        await using var session = Create();
        await session.AttachAsync(Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        _page = Page(Next, 2, 3_000, 1_320_000);
        await _events.WaitFor<PlaybackEvent.FileChanged>();
        await Waiting.Until(() => _events.All.Contains(new PlaybackEvent.Position(TimeSpan.FromSeconds(3))));

        var events = _events.All;
        var changed = events.FindIndex(e => e is PlaybackEvent.FileChanged { Path: Next });
        Assert.True(changed > 0);
        Assert.DoesNotContain(new PlaybackEvent.Position(TimeSpan.FromSeconds(3)), events.Take(changed));
        Assert.Equal(
        [
            new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1320)),
            new PlaybackEvent.Playing(),
            new PlaybackEvent.Position(TimeSpan.FromSeconds(3)),
        ], events.Skip(changed + 1).Take(3));
    }

    [Fact]
    public async Task Playing_SamePathSpelledDifferently_IsNotAnotherFile()
    {
        _page = Page(File, 2, 10_000, 600_000);
        await using var session = Create();
        await session.AttachAsync(Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        _page = Page(@"c:\videos\.\TEST.mp4", 2, 20_000, 600_000);
        await Waiting.Until(() => _events.All.Contains(new PlaybackEvent.Position(TimeSpan.FromSeconds(20))));

        Assert.Single(_events.OfType<PlaybackEvent.FileChanged>());
    }

    [Fact]
    public async Task FileClosed_RaisesFileChangedToNothing_AfterTheLastPosition()
    {
        _page = Page(File, 2, 10_000, 600_000);
        await using var session = Create();
        await session.AttachAsync(Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        _page = Page("", 0, 0, 0);
        await Waiting.Until(() => _events.OfType<PlaybackEvent.FileChanged>().Count == 2);

        Assert.Equal(new PlaybackEvent.FileChanged(null), _events.All[^1]);
    }

    [Fact]
    public async Task PauseAndPlay_RaiseOncePerChange()
    {
        _page = Page(File, 2, 10_000, 600_000);
        await using var session = Create();
        await session.AttachAsync(Ct);
        await _events.WaitFor<PlaybackEvent.Playing>();

        _page = Page(File, 1, 14_000, 600_000);
        await _events.WaitFor<PlaybackEvent.Paused>();
        var requests = RequestCount;
        await Waiting.Until(() => RequestCount >= requests + 3);

        _page = Page(File, 2, 14_000, 600_000);
        await Waiting.Until(() => _events.OfType<PlaybackEvent.Playing>().Count == 2);

        Assert.Single(_events.OfType<PlaybackEvent.Loaded>());
        Assert.Single(_events.OfType<PlaybackEvent.Paused>());
    }

    [Fact]
    public async Task StoppedAfterPlaying_RaisesEndedOnce()
    {
        _page = Page(File, 2, 590_000, 600_000);
        await using var session = Create();
        await session.AttachAsync(Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        _page = Page(File, 0, 0, 600_000);
        await _events.WaitFor<PlaybackEvent.Ended>();
        var requests = RequestCount;
        await Waiting.Until(() => RequestCount >= requests + 3);

        Assert.Single(_events.OfType<PlaybackEvent.Ended>());
    }

    [Fact]
    public async Task StoppedBeforeEverPlaying_IsNotAnEnd()
    {
        _page = Page(File, 0, 0, 600_000);
        await using var session = Create();
        await session.AttachAsync(Ct);
        var requests = RequestCount;
        await Waiting.Until(() => RequestCount >= requests + 3);

        Assert.DoesNotContain(_events.All, e => e is PlaybackEvent.Ended or PlaybackEvent.Loaded);
    }

    [Fact]
    public async Task PlayerStopsAnswering_RaisesClosed()
    {
        _page = Page(File, 2, 10_000, 600_000);
        await using var session = Create();
        await session.AttachAsync(Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        _down = true;

        await _events.WaitFor<PlaybackEvent.Closed>();
    }

    [Fact]
    public async Task OneMissedAnswer_IsForgiven()
    {
        _page = Page(File, 2, 10_000, 600_000);
        await using var session = Create(pollInterval: TimeSpan.FromMilliseconds(100));
        await session.AttachAsync(Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        _down = true;
        var requests = RequestCount;
        await Waiting.Until(() => RequestCount > requests);
        _down = false;
        var after = RequestCount;
        await Waiting.Until(() => RequestCount >= after + 3);

        Assert.Empty(_events.OfType<PlaybackEvent.Closed>());
    }

    [Fact]
    public async Task GetPosition_AsksThePlayerNow()
    {
        _page = Page(File, 2, 10_000, 600_000);
        await using var session = Create(pollInterval: TimeSpan.FromHours(1));
        await session.AttachAsync(Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        _page = Page(File, 2, 42_000, 600_000);

        Assert.Equal(TimeSpan.FromSeconds(42), await session.GetPositionAsync(Ct));
    }

    [Fact]
    public async Task GetPosition_StoppedOrAnotherFile_IsNull()
    {
        _page = Page(File, 2, 10_000, 600_000);
        await using var session = Create(pollInterval: TimeSpan.FromHours(1));
        await session.AttachAsync(Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        _page = Page(File, 0, 0, 600_000);
        Assert.Null(await session.GetPositionAsync(Ct));

        _page = Page(Next, 2, 5_000, 600_000);
        Assert.Null(await session.GetPositionAsync(Ct)); // the poll has not reported the new file yet
    }

    [Fact]
    public async Task Dispose_RaisesNothing_AndStopsAsking()
    {
        _page = Page(File, 2, 10_000, 600_000);
        var session = Create();
        await session.AttachAsync(Ct);
        await _events.WaitFor<PlaybackEvent.Position>();

        await session.DisposeAsync();
        await session.DisposeAsync();
        var requests = RequestCount;
        await Task.Delay(100, Ct);

        Assert.InRange(RequestCount, requests, requests + 1);
        Assert.Empty(_events.OfType<PlaybackEvent.Closed>());
    }

    [Fact]
    public async Task Attach_Twice_Throws()
    {
        await using var session = Create();
        await session.AttachAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AttachAsync(Ct));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }
}
