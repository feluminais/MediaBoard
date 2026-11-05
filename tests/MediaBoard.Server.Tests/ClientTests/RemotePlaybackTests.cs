using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Players;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using MediaBoard.Desktop.Remote;
using Microsoft.Extensions.Time.Testing;

namespace MediaBoard.Server.Tests.ClientTests;

/// <summary>
/// <see cref="PlaybackService"/> in remote mode, against the real server on a real socket. Only the player is a stand-in,
/// which a test drives by hand: no real player starts.
/// </summary>
public sealed class RemotePlaybackTests
{
    private const long FilmBytes = 64 * 1024 * 1024;
    private const double Duration = 2588.96;

    private static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakePlayer : IPlayerSession
    {
        public bool TracksProgress => true;

        public event Action<PlaybackEvent>? Event;

        public List<(string Locator, TimeSpan? StartAt)> Starts { get; } = [];

        /// <summary>What <see cref="GetPositionAsync"/> reports.</summary>
        public TimeSpan? Position { get; set; }

        /// <summary>When set, <see cref="GetPositionAsync"/> answers only once this completes.</summary>
        public TaskCompletionSource? PositionGate { get; set; }

        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Raise(PlaybackEvent e) => Event?.Invoke(e);

        public Task StartAsync(string filePath, TimeSpan? startAt, CancellationToken ct)
        {
            Starts.Add((filePath, startAt));
            return Task.CompletedTask;
        }

        public async Task<TimeSpan?> GetPositionAsync(CancellationToken ct)
        {
            Asked.TrySetResult();
            if (PositionGate is { } gate) await gate.Task;
            return Position;
        }

        public void Detach()
        {
        }

        public ValueTask DisposeAsync()
        {
            Closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeUrls : IStreamUrls
    {
        private readonly Dictionary<string, int> _files = [];

        public event Action<int, ApiErrorCode>? StreamFailed;

        public List<string> Handed { get; } = [];

        public List<string> Released { get; } = [];

        public string UrlFor(int fileId, string fileName)
        {
            var url = $"http://127.0.0.1:1/{Handed.Count}/{fileId}/{fileName}";
            _files[url] = fileId;
            Handed.Add(url);
            return url;
        }

        public int? FileOf(string locator) => _files.TryGetValue(locator, out var fileId) ? fileId : null;

        public void Release(string url) => Released.Add(url);

        public void Fail(int fileId, ApiErrorCode code) => StreamFailed?.Invoke(fileId, code);
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-remote-play-" + Guid.NewGuid().ToString("N"));
        private readonly StreamProxy? _proxy;

        private World(LiveServer live, IStreamUrls? urls, TimeProvider? outboxTime, int proxyPort, bool noPlayer)
        {
            Directory.CreateDirectory(_dir);
            Live = live;
            Client = live.NewClient();
            _proxy = urls is null ? new StreamProxy(Client, proxyPort) : null;
            Urls = urls ?? _proxy!;
            Outbox = new ProgressOutbox(Client, Path.Combine(_dir, "pending-progress.json"), outboxTime ?? TimeProvider.System);
            Source = new RemotePlaybackSource(Client, Urls, Outbox);
            var settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
            settings.Save(new AppSettings { DefaultPlayerId = "mpv" });
            Service = new PlaybackService(Source, Outbox, new PlayerDetector(_ => !noPlayer, () => [], Hub), settings, Hub, _ =>
            {
                var player = new FakePlayer();
                Players.Add(player);
                return player;
            });
            Source.StreamLost += (fileId, reason) => _ = Service.AbandonAsync(fileId, reason);
        }

        public LiveServer Live { get; }

        public ApiClient Client { get; }

        public IStreamUrls Urls { get; }

        public ProgressOutbox Outbox { get; }

        public RemotePlaybackSource Source { get; }

        public PlaybackService Service { get; }

        public ActivityHub Hub { get; } = new();

        public List<FakePlayer> Players { get; } = [];

        public FakePlayer Player => Players[^1];

        public int File { get; private set; }

        public string FilePath { get; private set; } = "";

        public static async Task<World> StartAsync(IStreamUrls? urls = null, TimeProvider? outboxTime = null, int proxyPort = 0, bool noPlayer = false)
        {
            var live = await LiveServer.StartAsync(Ct);
            var world = new World(live, urls, outboxTime, proxyPort, noPlayer);
            (world.File, world.FilePath) = await live.AddFilmAsync("Heat (1995).mkv", FilmBytes);
            return world;
        }

        public void SaveOnServer(double positionS) => Live.Fixture.Progress.Save(Live.Sam.Id, File, positionS, Duration);

        public Task PlayAsync(bool fromStart = false) => Service.PlayAsync(File, fromStart, Ct);

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (_proxy is not null) await _proxy.DisposeAsync();
            await Outbox.DisposeAsync();
            Client.Dispose();
            await Live.DisposeAsync();
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
    }

    // ---- Starting ----

    [Fact]
    public async Task Play_ResumesFromServerProgress()
    {
        await using var world = await World.StartAsync();
        world.SaveOnServer(600);

        await world.PlayAsync();

        var (locator, startAt) = Assert.Single(world.Player.Starts);
        Assert.StartsWith("http://127.0.0.1:", locator, StringComparison.Ordinal);
        Assert.EndsWith(".mkv", locator, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(600), startAt);
    }

    [Fact]
    public async Task Play_ThroughTheProxy_ReachesTheFile()
    {
        await using var world = await World.StartAsync();
        await File.WriteAllBytesAsync(world.FilePath, [1, 2, 3, 4, 5], Ct);

        await world.PlayAsync();

        using var player = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, world.Player.Starts[0].Locator);
        request.Headers.TryAddWithoutValidation("Range", "bytes=1-3");
        using var response = await player.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new byte[] { 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Play_FileMissing_SaysSo()
    {
        await using var world = await World.StartAsync();
        File.Delete(world.FilePath);

        await world.PlayAsync();

        Assert.Empty(world.Players);
        Assert.Contains(world.Hub.Recent, entry => entry.Message == "File missing: Heat (1995).mkv");
    }

    [Fact]
    public async Task Play_UnknownFile_SaysSo()
    {
        await using var world = await World.StartAsync();

        await world.Service.PlayAsync(999999, fromStart: false, Ct);

        Assert.Empty(world.Players);
        Assert.Contains(world.Hub.Recent, entry => entry.Message == "File missing");
    }

    [Fact]
    public async Task Play_ServerDown_SaysCantReach()
    {
        await using var world = await World.StartAsync();
        await world.Live.Fixture.Server.StopAsync();

        await world.PlayAsync();

        Assert.Empty(world.Players);
        Assert.Contains(world.Hub.Recent, entry => entry.Message == "Can't reach the server");
    }

    [Fact]
    public async Task Play_ProxyCantBind_SaysSo()
    {
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            await using var world = await World.StartAsync(proxyPort: ((IPEndPoint)taken.LocalEndpoint).Port);

            await world.PlayAsync();

            Assert.Empty(world.Players);
            Assert.Contains(world.Hub.Recent, entry => entry.Message == "Couldn't start the stream");
        }
        finally
        {
            taken.Stop();
        }
    }

    [Fact]
    public async Task FindByLocator_NoNetwork()
    {
        await using var world = await World.StartAsync();
        await world.PlayAsync();
        var locator = world.Player.Starts[0].Locator;
        await world.Live.Fixture.Server.StopAsync();

        var found = world.Source.FindByLocator(locator);

        Assert.Equal(world.File, found?.FileId);
        Assert.Equal("Heat", found?.Title);
        Assert.Null(world.Source.FindByLocator(locator + "x"));
    }

    // ---- The outbox over the server's progress ----

    [Fact]
    public async Task Replay_BeforeTheFlush_ResumesFromTheOutbox()
    {
        await using var world = await World.StartAsync();
        world.SaveOnServer(600);
        await world.PlayAsync();
        var first = world.Player;
        first.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(700)));
        first.Position = TimeSpan.FromSeconds(710);

        await world.PlayAsync();

        Assert.NotSame(first, world.Player);
        Assert.Equal(TimeSpan.FromSeconds(710), Assert.Single(world.Player.Starts).StartAt);
    }

    [Fact]
    public async Task Resolve_ServerNewerThanTheOutbox_KeepsTheServers()
    {
        var yesterday = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(-1));
        await using var world = await World.StartAsync(outboxTime: yesterday);
        world.Outbox.Save(world.File, 100, Duration);
        world.SaveOnServer(600);

        await world.PlayAsync();

        Assert.Equal(TimeSpan.FromSeconds(600), Assert.Single(world.Player.Starts).StartAt);
    }

    [Fact]
    public async Task Resolve_WatchedInTheOutbox_StartsFromTheBeginning()
    {
        await using var world = await World.StartAsync();
        world.SaveOnServer(600);
        world.Outbox.SetWatched(world.File, true);

        await world.PlayAsync();

        Assert.Null(Assert.Single(world.Player.Starts).StartAt);
    }

    // ---- Progress on its way out ----

    [Fact]
    public async Task PlayerPause_ReachesTheServer_WithoutWaitingForTheTick()
    {
        await using var world = await World.StartAsync();
        world.Outbox.Start();
        await world.PlayAsync();

        world.Player.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(300)));
        world.Player.Raise(new PlaybackEvent.Paused());

        await WaitUntilAsync(() => world.Live.Fixture.Progress.Get(world.Live.Sam.Id, world.File)?.PositionS == 300);
    }

    [Fact]
    public async Task PlayerClosed_ReachesTheServer_WithTheDuration()
    {
        await using var world = await World.StartAsync();
        world.Outbox.Start();
        await world.PlayAsync();
        world.Player.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Duration)));
        world.Player.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(300)));

        world.Player.Raise(new PlaybackEvent.Closed());

        await WaitUntilAsync(() => world.Live.Fixture.Progress.Get(world.Live.Sam.Id, world.File)?.DurationS == Duration);
    }

    // ---- Streams that fail ----

    [Fact]
    public async Task StreamStopped_ShowsMessageWithOwnerName()
    {
        await using var world = await World.StartAsync();
        await world.PlayAsync();
        using var player = new HttpClient();
        using var response = await player.GetAsync(world.Player.Starts[0].Locator, HttpCompletionOption.ResponseHeadersRead, Ct);
        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        await body.ReadExactlyAsync(new byte[64 * 1024], Ct);

        world.Live.Fixture.Streams.Stop(world.Live.Fixture.Streams.Current.Single().Id);

        await Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(Stream.Null, Ct).WaitAsync(Within, Ct));
        await world.Player.Closed.Task.WaitAsync(Within, Ct);
        Assert.Contains(world.Hub.Recent, entry => entry.Message == $"{world.Live.OwnerName} stopped this stream");
        Assert.Null(world.Service.CurrentPlayer);
    }

    [Theory]
    [InlineData(ApiErrorCode.StreamStopped)]
    [InlineData(ApiErrorCode.FileMissing)]
    [InlineData(ApiErrorCode.DriveUnavailable)]
    [InlineData(ApiErrorCode.Unreachable)]
    public async Task StreamFailed_KeepsLastPosition_NotWatched(ApiErrorCode code)
    {
        var urls = new FakeUrls();
        await using var world = await World.StartAsync(urls);
        await world.PlayAsync();
        var player = world.Player;
        var changes = 0;
        world.Service.ProgressChanged += () => changes++;
        player.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Duration)));
        player.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(115)));

        urls.Fail(world.File, code);

        // What the spike saw: the player plays out its buffer and then claims the whole file.
        player.Position = TimeSpan.FromSeconds(Duration);
        player.Raise(new PlaybackEvent.Paused());
        player.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(Duration)));
        player.Raise(new PlaybackEvent.Ended());
        await world.Service.StopAsync(closePlayer: true, Ct);

        var waiting = world.Outbox.Waiting(world.File);
        Assert.Equal(115, waiting?.PositionS);
        Assert.NotEqual(true, waiting?.Watched);
        Assert.True(player.Closed.Task.IsCompleted);
        Assert.Null(world.Service.CurrentPlayer);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task StreamFailed_DuringAStop_KeepsLastPosition()
    {
        var urls = new FakeUrls();
        await using var world = await World.StartAsync(urls);
        await world.PlayAsync();
        var player = world.Player;
        player.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Duration)));
        player.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(115)));
        player.PositionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stop = world.Service.StopAsync(closePlayer: true, Ct);
        await player.Asked.Task.WaitAsync(Within, Ct);
        urls.Fail(world.File, ApiErrorCode.StreamStopped);
        player.Position = TimeSpan.FromSeconds(Duration);
        player.PositionGate.SetResult();
        await stop.WaitAsync(Within, Ct);

        var waiting = world.Outbox.Waiting(world.File);
        Assert.Equal(115, waiting?.PositionS);
        Assert.NotEqual(true, waiting?.Watched);
        Assert.Contains(world.Hub.Recent, entry => entry.Message == $"{world.Live.OwnerName} stopped this stream");
    }

    [Fact]
    public async Task TooManyStreams_SaysSo()
    {
        var urls = new FakeUrls();
        await using var world = await World.StartAsync(urls);
        await world.PlayAsync();

        urls.Fail(world.File, ApiErrorCode.TooManyStreams);

        Assert.Contains(world.Hub.Recent, entry => entry.Message == "Too many streams on this account");
    }

    // ---- Giving back what was resolved, so the proxy listens only while something plays ----

    [Theory]
    [InlineData("Closed")]
    [InlineData("Failed")]
    [InlineData("Stopped")]
    [InlineData("Abandoned")]
    public async Task SessionEnd_GivesItsUrlBack(string how)
    {
        var urls = new FakeUrls();
        await using var world = await World.StartAsync(urls);
        await world.PlayAsync();

        Assert.Empty(urls.Released);
        switch (how)
        {
            case "Closed":
                world.Player.Raise(new PlaybackEvent.Closed());
                break;
            case "Failed":
                world.Player.Raise(new PlaybackEvent.Failed("no"));
                break;
            case "Stopped":
                await world.Service.StopAsync(closePlayer: true, Ct);
                break;
            default:
                urls.Fail(world.File, ApiErrorCode.Unreachable);
                break;
        }

        await WaitUntilAsync(() => urls.Released.Count > 0);
        Assert.Equal(urls.Handed, urls.Released);
    }

    [Fact]
    public async Task Ended_KeepsTheUrl_ThePlayerCanStillSeekBack()
    {
        var urls = new FakeUrls();
        await using var world = await World.StartAsync(urls);
        await world.PlayAsync();

        world.Player.Raise(new PlaybackEvent.Ended());

        Assert.Empty(urls.Released);
    }

    [Fact]
    public async Task NoPlayer_GivesTheUrlBack()
    {
        var urls = new FakeUrls();
        await using var world = await World.StartAsync(urls, noPlayer: true);

        await world.PlayAsync();

        Assert.Empty(world.Players);
        Assert.Equal(urls.Handed, urls.Released);
    }

    [Fact]
    public async Task Replay_GivesBackEveryUrlButTheOneInUse()
    {
        var urls = new FakeUrls();
        await using var world = await World.StartAsync(urls);
        await world.PlayAsync();

        await world.PlayAsync();

        // The first session's, and the one resolved before the stop made it stale.
        var inUse = Assert.Single(world.Player.Starts).Locator;
        Assert.Equal(3, urls.Handed.Count);
        Assert.Equal(urls.Handed.Where(url => url != inUse).Order(StringComparer.Ordinal), urls.Released.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RealProxy_SessionEnd_StopsListening_TheNextPlayWorks()
    {
        await using var world = await World.StartAsync();
        await File.WriteAllBytesAsync(world.FilePath, [1, 2, 3, 4, 5], Ct);
        await world.PlayAsync();
        var first = new Uri(world.Player.Starts[0].Locator);

        world.Player.Raise(new PlaybackEvent.Closed());

        await WaitUntilAsync(() => !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(listener => listener.Port == first.Port));

        await world.PlayAsync();

        using var player = new HttpClient();
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await player.GetByteArrayAsync(world.Player.Starts[0].Locator, Ct));
    }

    [Fact]
    public async Task StreamFailed_ForAnotherFile_LeavesThePlayerAlone()
    {
        var urls = new FakeUrls();
        await using var world = await World.StartAsync(urls);
        await world.PlayAsync();

        urls.Fail(world.File + 1, ApiErrorCode.StreamStopped);

        Assert.False(world.Player.Closed.Task.IsCompleted);
        Assert.Equal("MPV", world.Service.CurrentPlayer, ignoreCase: true);
    }

    [Fact]
    public async Task StreamFailed_Twice_SaysItOnce()
    {
        var urls = new FakeUrls();
        await using var world = await World.StartAsync(urls);
        await world.PlayAsync();

        urls.Fail(world.File, ApiErrorCode.Unreachable);
        urls.Fail(world.File, ApiErrorCode.Unreachable);

        Assert.Single(world.Hub.Recent, entry => entry.Message == "Can't reach the server");
    }

    [Fact]
    public async Task PausePastTheEnd_OfAHealthyStream_StillCountsAsWatched()
    {
        var urls = new FakeUrls();
        await using var world = await World.StartAsync(urls);
        await world.PlayAsync();
        world.Player.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Duration)));

        world.Player.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(Duration - 1)));
        world.Player.Raise(new PlaybackEvent.Ended());

        Assert.True(world.Outbox.Waiting(world.File)?.Watched);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Within);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
