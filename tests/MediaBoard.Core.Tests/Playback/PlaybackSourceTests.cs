using MediaBoard.Core.Activity;
using MediaBoard.Core.Players;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;

namespace MediaBoard.Core.Tests.Playback;

public sealed class PlaybackSourceTests : IDisposable
{
    private const string Url = "https://127.0.0.1:50001/stream/7";
    private const string NextUrl = "https://127.0.0.1:50001/stream/8";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly ActivityHub _hub = new();
    private readonly SettingsStore _settings;
    private readonly FakeSource _source = new();
    private readonly FakeSink _sink = new();
    private readonly List<FakePlayerSession> _sessions = [];
    private readonly PlaybackService _service;

    public PlaybackSourceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        _settings.Save(new AppSettings { DefaultPlayerId = "mpv" });
        _service = new PlaybackService(_source, _sink, new PlayerDetector(_ => true, () => [], _hub), _settings, _hub,
            player =>
            {
                var session = new FakePlayerSession(player.TracksProgress);
                _sessions.Add(session);
                return session;
            });
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Play_UsesSourceLocator()
    {
        _source.Add(new PlayTarget(7, Url, "Remote", 6000, Saved: null, Missing: false));

        await _service.PlayAsync(7, fromStart: false, CancellationToken.None);

        Assert.Equal(Url, Assert.Single(Assert.Single(_sessions).Starts).FilePath);
    }

    [Fact]
    public async Task FileChanged_UsesFindByLocator()
    {
        _source.Add(new PlayTarget(7, Url, "Remote", 6000, Saved: null, Missing: false));
        _source.Add(new PlayTarget(8, NextUrl, "Next", 6000, new Progress(8, 300, 6000, false, DateTime.UtcNow), Missing: false));
        await _service.PlayAsync(7, fromStart: false, CancellationToken.None);
        var session = _sessions[^1];

        session.Raise(new PlaybackEvent.FileChanged(NextUrl));
        session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(0.5)));
        session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(20)));

        // The target's saved position holds saving back until the player is past the start; its duration came with it too.
        Assert.Equal([(8, 20d, 6000d)], _sink.Saves);
    }

    [Fact]
    public async Task SinkFailure_HttpException_LogsOnce()
    {
        _source.Add(new PlayTarget(7, Url, "Remote", 6000, Saved: null, Missing: false));
        _sink.Failure = new HttpRequestException("offline");
        await _service.PlayAsync(7, fromStart: false, CancellationToken.None);
        var session = _sessions[^1];

        session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(5)));
        session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(6)));

        Assert.Single(_hub.Recent, e => e.Message == "Couldn't save progress");
        Assert.Equal("mpv", _hub.Current.PlayerName);
    }

    // A file on this PC can't lose its stream, so an early Ended changes nothing about what the player is believed on.
    [Fact]
    public async Task EarlyEnded_LocalSource_StillTakesTheFreshPosition()
    {
        _source.Add(new PlayTarget(7, Url, "Local", 6000, Saved: null, Missing: false));
        await _service.PlayAsync(7, fromStart: false, CancellationToken.None);
        var session = _sessions[^1];
        session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(120)));
        session.Raise(new PlaybackEvent.Ended());
        session.Position = TimeSpan.FromSeconds(300);

        await _service.StopAsync(closePlayer: true, CancellationToken.None);

        Assert.Equal((7, 300d, 6000d), _sink.Saves[^1]);
    }

    [Fact]
    public async Task PlayOtherFile_ResolvesOnce()
    {
        _source.Add(new PlayTarget(7, Url, "Remote", 6000, Saved: null, Missing: false));
        _source.Add(new PlayTarget(8, NextUrl, "Next", 6000, Saved: null, Missing: false));
        await _service.PlayAsync(7, fromStart: false, CancellationToken.None);
        _source.Resolves.Clear();

        await _service.PlayAsync(8, fromStart: false, CancellationToken.None);

        Assert.Equal([8], _source.Resolves);
    }

    private sealed class FakeSource : IPlaybackSource
    {
        private readonly Dictionary<int, PlayTarget> _byId = [];

        public List<int> Resolves { get; } = [];

        public void Add(PlayTarget target) => _byId[target.FileId] = target;

        public Task<PlayTarget?> ResolveAsync(int fileId, CancellationToken ct)
        {
            Resolves.Add(fileId);
            return Task.FromResult(_byId.GetValueOrDefault(fileId));
        }

        public PlayTarget? FindByLocator(string locator) =>
            _byId.Values.FirstOrDefault(t => string.Equals(t.Locator, locator, StringComparison.Ordinal));
    }

    private sealed class FakeSink : IProgressSink
    {
        public List<(int FileId, double PositionS, double DurationS)> Saves { get; } = [];

        public Exception? Failure { get; set; }

        public void Save(int fileId, double positionS, double durationS)
        {
            if (Failure is not null) throw Failure;
            Saves.Add((fileId, positionS, durationS));
        }

        public void SetWatched(int fileId, bool watched)
        {
            if (Failure is not null) throw Failure;
        }
    }
}
