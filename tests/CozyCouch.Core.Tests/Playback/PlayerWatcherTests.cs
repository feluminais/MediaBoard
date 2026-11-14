using CozyCouch.Core.Playback;
using CozyCouch.Core.Players;
using CozyCouch.Core.Settings;
using CozyCouch.Core.Tests.Players;
using CozyCouch.Core.Tests.Support;

namespace CozyCouch.Core.Tests.Playback;

public sealed class PlayerWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cozycouch-test-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _settings;
    private readonly List<IAttachableSession> _offered = [];
    private readonly HashSet<IAttachableSession> _accepted = [];
    private string? _current;

    public PlayerWatcherTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlayerWatcher Watcher(params PlayerWatcher.Probe[] probes) => new(
        _settings,
        (session, _) =>
        {
            _offered.Add(session);
            return Task.FromResult(_accepted.Contains(session));
        },
        () => _current,
        probes);

    [Fact]
    public async Task Check_OffersTheFirstPlayerThatIsThere_AndStopsAtTheFirstThatAttaches()
    {
        var mpv = new FakeAttachableSession("mpv");
        var vlc = new FakeAttachableSession("VLC");
        _accepted.Add(mpv);
        _accepted.Add(vlc);
        await using var watcher = Watcher(_ => mpv, _ => vlc);

        await watcher.CheckAsync(Ct);

        Assert.Equal([mpv], _offered);
    }

    [Fact]
    public async Task Check_PlayerThatDoesNotAttach_LetsTheNextOneTry()
    {
        var mpv = new FakeAttachableSession("mpv");
        var vlc = new FakeAttachableSession("VLC");
        _accepted.Add(vlc);
        await using var watcher = Watcher(_ => mpv, _ => vlc);

        await watcher.CheckAsync(Ct);

        Assert.Equal([mpv, vlc], _offered);
    }

    [Fact]
    public async Task Check_ProbeWithNothingToTry_IsSkipped()
    {
        var vlc = new FakeAttachableSession("VLC");
        _accepted.Add(vlc);
        await using var watcher = Watcher(_ => null, _ => vlc);

        await watcher.CheckAsync(Ct);

        Assert.Equal([vlc], _offered);
    }

    [Fact]
    public async Task Check_APlayerIsFollowedAlready_OffersNothing()
    {
        _current = "mpv";
        var mpv = new FakeAttachableSession("mpv");
        await using var watcher = Watcher(_ => mpv);

        await watcher.CheckAsync(Ct);

        Assert.Empty(_offered);
    }

    [Fact]
    public async Task Check_SettingIsReadOnEveryLook()
    {
        var mpv = new FakeAttachableSession("mpv");
        await using var watcher = Watcher(_ => mpv);
        _settings.Save(new AppSettings { FollowOtherPlayers = false });
        await watcher.CheckAsync(Ct);

        _settings.Save(new AppSettings { FollowOtherPlayers = true });
        await watcher.CheckAsync(Ct);

        Assert.Single(_offered);
    }

    [Fact]
    public async Task Check_AttachThrows_LetsTheNextOneTry()
    {
        var vlc = new FakeAttachableSession("VLC");
        _accepted.Add(vlc);
        var calls = 0;
        await using var watcher = new PlayerWatcher(
            _settings,
            (session, _) => ++calls == 1 ? throw new InvalidOperationException("boom") : Task.FromResult(true),
            () => null,
            [_ => new FakeAttachableSession(), _ => vlc]);

        await watcher.CheckAsync(Ct);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Check_ProbesGetTheCurrentSettings()
    {
        _settings.Save(new AppSettings { VlcWebPort = 9090, VlcWebPassword = "secret" });
        AppSettings? seen = null;
        await using var watcher = Watcher(s =>
        {
            seen = s;
            return null;
        });

        await watcher.CheckAsync(Ct);

        Assert.Equal(9090, seen?.VlcWebPort);
        Assert.Equal("secret", seen?.VlcWebPassword);
    }

    [Fact]
    public async Task Started_LooksAgainAfterTheInterval_UntilDisposed()
    {
        var mpv = new FakeAttachableSession("mpv");
        var watcher = new PlayerWatcher(
            _settings,
            (session, _) =>
            {
                _offered.Add(session);
                return Task.FromResult(false);
            },
            () => null,
            [_ => mpv],
            TimeSpan.FromMilliseconds(20));

        watcher.Start();
        await Waiting.Until(() => _offered.Count >= 3);
        await watcher.DisposeAsync();
        var seen = _offered.Count;
        await Task.Delay(100, Ct);

        Assert.Equal(seen, _offered.Count);
    }
}
