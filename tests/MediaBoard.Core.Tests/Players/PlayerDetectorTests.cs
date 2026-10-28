using MediaBoard.Core.Activity;
using MediaBoard.Core.Players;
using MediaBoard.Core.Settings;

namespace MediaBoard.Core.Tests.Players;

public class PlayerDetectorTests
{
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    private static readonly string MpcHc = Path.Combine(ProgramFiles, "MPC-HC", "mpc-hc64.exe");
    private static readonly string Vlc = Path.Combine(ProgramFiles, "VideoLAN", "VLC", "vlc.exe");
    private static readonly string Mpv = Path.Combine(ProgramFiles, "mpv", "mpv.exe");

    private static PlayerDetector Create(
        ActivityHub hub,
        IEnumerable<string>? existing = null,
        IEnumerable<(string DisplayName, string? InstallLocation)>? entries = null)
    {
        var files = new HashSet<string>(existing ?? [], StringComparer.OrdinalIgnoreCase);
        var uninstall = (entries ?? []).ToArray();
        return new PlayerDetector(files.Contains, () => uninstall, hub);
    }

    [Fact]
    public void FindsMpcHcAtProgramFiles()
    {
        var detector = Create(new ActivityHub(), existing: [MpcHc]);

        var player = Assert.Single(detector.Detect());

        Assert.Equal("mpc-hc", player.Id);
        Assert.Equal("MPC-HC", player.Name);
        Assert.Equal(MpcHc, player.ExePath);
        Assert.True(player.TracksProgress);
    }

    [Fact]
    public void FindsFromUninstallRegistry()
    {
        var installDir = Path.Combine("D:\\", "Apps", "VLC");
        var detector = Create(
            new ActivityHub(),
            existing: [Path.Combine(installDir, "vlc.exe")],
            entries:
            [
                ("Some Other Tool", Path.Combine("D:\\", "Apps", "Other")),
                ("VLC media player", installDir),
                ("mpv", null),
            ]);

        var player = Assert.Single(detector.Detect());

        Assert.Equal("vlc", player.Id);
        Assert.Equal(Path.Combine(installDir, "vlc.exe"), player.ExePath);
    }

    [Fact]
    public void KnownPathWinsOverRegistryEntryForSamePlayer()
    {
        var registryDir = Path.Combine("D:\\", "Apps", "VLC");
        var detector = Create(
            new ActivityHub(),
            existing: [Vlc, Path.Combine(registryDir, "vlc.exe")],
            entries: [("VLC media player", registryDir)]);

        var player = Assert.Single(detector.Detect());

        Assert.Equal(Vlc, player.ExePath);
    }

    [Fact]
    public void DefaultsToMpcHc()
    {
        var detector = Create(new ActivityHub(), existing: [Vlc, MpcHc]);

        var player = detector.Resolve(new AppSettings());

        Assert.NotNull(player);
        Assert.Equal("mpc-hc", player.Id);
    }

    [Fact]
    public void Resolve_UsesNamedDefault_WhenDetected()
    {
        var detector = Create(new ActivityHub(), existing: [Vlc, MpcHc]);

        var player = detector.Resolve(new AppSettings { DefaultPlayerId = "vlc" });

        Assert.Equal("vlc", player?.Id);
    }

    [Fact]
    public void Resolve_NamedDefaultNotInstalled_LogsAndFallsBackToMpcHc()
    {
        var hub = new ActivityHub();
        var detector = Create(hub, existing: [MpcHc]);

        var player = detector.Resolve(new AppSettings { DefaultPlayerId = "mpv" });

        Assert.Equal("mpc-hc", player?.Id);
        var entry = Assert.Single(hub.Recent);
        Assert.Equal(ActivityLevel.Warning, entry.Level);
        Assert.Equal("Player not found: mpv", entry.Message);
    }

    [Fact]
    public void Resolve_NoMpcHc_FallsBackToFirstDetected()
    {
        var detector = Create(new ActivityHub(), existing: [Mpv, Vlc]);

        var player = detector.Resolve(new AppSettings());

        Assert.Equal("mpv", player?.Id);
    }

    [Fact]
    public void Resolve_CustomPath_ExistingExe_ReturnsCustomPlayer()
    {
        var custom = Path.Combine("D:\\", "Tools", "potplayer.exe");
        var detector = Create(new ActivityHub(), existing: [custom, MpcHc]);

        var player = detector.Resolve(new AppSettings { DefaultPlayerId = "custom", CustomPlayerPath = custom });

        Assert.NotNull(player);
        Assert.Equal("custom", player.Id);
        Assert.Equal(custom, player.ExePath);
        Assert.False(player.TracksProgress);
    }

    [Fact]
    public void CustomPath_MissingExe_ResolvesNull_AndMessage()
    {
        var hub = new ActivityHub();
        var detector = Create(hub, existing: [MpcHc]);
        var missing = Path.Combine("D:\\", "Tools", "gone.exe");

        var player = detector.Resolve(new AppSettings { DefaultPlayerId = "custom", CustomPlayerPath = missing });

        Assert.Null(player);
        var entry = Assert.Single(hub.Recent);
        Assert.Equal(ActivityLevel.Warning, entry.Level);
        Assert.Equal($"Player not found: {missing}", entry.Message);
        Assert.False(string.IsNullOrWhiteSpace(entry.Hint));
    }

    [Fact]
    public void NoPlayers_ReturnsEmpty()
    {
        var hub = new ActivityHub();
        var detector = Create(hub);

        Assert.Empty(detector.Detect());
        Assert.Null(detector.Resolve(new AppSettings()));

        var entry = Assert.Single(hub.Recent);
        Assert.Equal(ActivityLevel.Warning, entry.Level);
        Assert.Equal("No media player found", entry.Message);
        Assert.Equal("Install MPC-HC or choose a player in Settings", entry.Hint);
    }

    [Fact]
    public void CreateDefault_Detect_DoesNotThrow()
    {
        var detector = PlayerDetector.CreateDefault(new ActivityHub());

        var players = detector.Detect();

        Assert.Equal(players.Count, players.Select(p => p.Id).Distinct().Count());
    }
}
