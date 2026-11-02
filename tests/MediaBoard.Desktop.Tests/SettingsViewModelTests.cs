using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Players;
using MediaBoard.Core.Settings;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mediaboard-settings-").FullName;
    private readonly ActivityHub _hub = new();
    private readonly List<string> _opened = [];
    private readonly HashSet<string> _existingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string DisplayName, string? InstallLocation)> _installed = [];
    private string? _browsed;

    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SettingsStore Store => new(Path.Combine(_dir, "settings.json"));

    private PlayerDetector Detector => new(path => _existingFiles.Contains(path), () => _installed, _hub);

    private SettingsViewModel Create(SettingsStore? store = null) => new(
        store ?? Store, Detector, _hub,
        browsePlayer: () => Task.FromResult(_browsed),
        openUrl: _opened.Add);

    private static async Task LoadAsync(SettingsViewModel vm)
    {
        await vm.LoadPlayersAsync();
        Ui.Flush();
    }

    // A fake provider that is "no key" until the settings file holds one, and counts what it was asked.
    private sealed class KeyGatedProvider(Func<AppSettings> settings) : IMetadataProvider
    {
        public int Searches;

        public Task<IReadOnlyList<SearchCandidate>> SearchAsync(string title, int? year, TitleKind kind, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(settings().TmdbKey)) throw new MetadataUnavailableException(UnavailableReason.NoKey);
            Interlocked.Increment(ref Searches);
            return Task.FromResult<IReadOnlyList<SearchCandidate>>([]);
        }

        public Task<IReadOnlyList<SearchCandidate>> SearchAnyAsync(string query, CancellationToken ct) => throw new NotSupportedException();

        public Task<(int TmdbId, TitleKind Kind)?> FindByImdbIdAsync(string imdbId, CancellationToken ct) =>
            Task.FromResult<(int TmdbId, TitleKind Kind)?>(null);

        public Task<TitleDetails> GetTitleAsync(int tmdbId, TitleKind kind, CancellationToken ct) => throw new NotSupportedException();

        public Task<SeasonDetails> GetSeasonAsync(int showTmdbId, int season, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void FollowOtherPlayers_ShowsTheSavedChoices_WithoutSavingThem()
    {
        var store = Store;
        store.Save(new AppSettings { FollowOtherPlayers = false, VlcWebPort = 9090, VlcWebPassword = "secret" });
        var changes = 0;
        store.Changed += _ => changes++;

        var vm = Create(store);

        Assert.False(vm.FollowOtherPlayers);
        Assert.Equal("9090", vm.VlcPort);
        Assert.Equal("secret", vm.VlcPassword);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void FollowOtherPlayers_DefaultsToOnWithVlcsOwnPort()
    {
        var vm = Create();

        Assert.True(vm.FollowOtherPlayers);
        Assert.Equal("8080", vm.VlcPort);
        Assert.Equal("", vm.VlcPassword);
    }

    [Fact]
    public void FollowOtherPlayers_ToggleIsSaved()
    {
        var vm = Create();

        vm.FollowOtherPlayers = false;

        Assert.False(Store.Load().FollowOtherPlayers);
    }

    [Fact]
    public void SaveVlc_StoresThePortAndPassword()
    {
        var vm = Create();
        vm.VlcPort = " 9090 ";
        vm.VlcPassword = " secret ";

        vm.SaveVlcCommand.Execute(null);

        var saved = Store.Load();
        Assert.Equal(9090, saved.VlcWebPort);
        Assert.Equal("secret", saved.VlcWebPassword);
        Assert.Equal("Saved", vm.FollowStatus);
        Assert.Equal("9090", vm.VlcPort);
    }

    [Fact]
    public void SaveMpc_StoresThePort_AndLeavesVlcAlone()
    {
        var store = Store;
        store.Save(new AppSettings { VlcWebPort = 9090, VlcWebPassword = "secret" });
        var vm = Create(store);
        Assert.Equal("13579", vm.MpcPort);
        vm.MpcPort = " 13580 ";

        vm.SaveMpcCommand.Execute(null);

        var saved = Store.Load();
        Assert.Equal(13580, saved.MpcWebPort);
        Assert.Equal(9090, saved.VlcWebPort);
        Assert.Equal("secret", saved.VlcWebPassword);
        Assert.Equal("Saved", vm.FollowStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("70000")]
    public void SaveMpc_BadPort_SavesNothingAndSaysSo(string port)
    {
        var vm = Create();
        vm.MpcPort = port;

        vm.SaveMpcCommand.Execute(null);

        Assert.Equal(13579, Store.Load().MpcWebPort);
        Assert.Equal("The port is a number from 1 to 65535.", vm.FollowStatus);
    }

    [Fact]
    public void SaveVlc_EmptyPassword_ClearsIt()
    {
        var store = Store;
        store.Save(new AppSettings { VlcWebPassword = "secret" });
        var vm = Create(store);
        vm.VlcPassword = "";

        vm.SaveVlcCommand.Execute(null);

        Assert.Null(Store.Load().VlcWebPassword);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-5")]
    public void SaveVlc_BadPort_SavesNothingAndSaysSo(string port)
    {
        var vm = Create();
        vm.VlcPort = port;
        vm.VlcPassword = "secret";

        vm.SaveVlcCommand.Execute(null);

        Assert.Equal(8080, Store.Load().VlcWebPort);
        Assert.Null(Store.Load().VlcWebPassword);
        Assert.Equal("The port is a number from 1 to 65535.", vm.FollowStatus);
    }

    [Fact]
    public void FollowStatus_ClearsWhileTheUserEdits()
    {
        var vm = Create();
        vm.SaveVlcCommand.Execute(null);
        Assert.Equal("Saved", vm.FollowStatus);

        vm.VlcPassword = "x";

        Assert.Equal("", vm.FollowStatus);
    }

    [AvaloniaFact]
    public async Task TmdbKey_SaveTriggersIdentification()
    {
        KeyGatedProvider? provider = null;
        using var stack = new ShellStack(services => services.AddSingleton<IMetadataProvider>(sp =>
            provider = new KeyGatedProvider(sp.GetRequiredService<SettingsStore>().Load)));
        var root = stack.MakeLibrary("Movies", "Heat (1995).mkv");
        await stack.Actions.AddAsync(root); // scans and tries to identify: no key, so it waits
        await stack.Actions.WhenIdleAsync();
        var identification = stack.Services.GetRequiredService<MediaBoard.Core.Identification.IdentificationService>();
        identification.Start(); // listens for a new key, as AppHost does
        await identification.WhenIdleAsync();
        Assert.NotNull(provider);
        Assert.Equal(0, provider.Searches);

        var vm = new SettingsViewModel(stack.Settings, Detector, stack.Hub, () => Task.FromResult<string?>(null), _ => { });
        vm.TmdbKey = "  abc123  ";
        vm.SaveKeyCommand.Execute(null);
        await identification.WhenIdleAsync();

        Assert.Equal("abc123", stack.Settings.Load().TmdbKey);
        Assert.True(provider.Searches > 0, "saving the key should have restarted identification");
    }

    [AvaloniaFact]
    public void TmdbKey_Save_KeepsOtherSettings_AndEmptyRemovesTheKey()
    {
        var store = Store;
        store.Save(new AppSettings { TmdbKey = "old", DefaultPlayerId = "vlc", FfprobePath = @"C:\tools\ffprobe.exe" });
        var vm = Create(store);
        Assert.Equal("old", vm.TmdbKey);

        vm.TmdbKey = "new";
        vm.SaveKeyCommand.Execute(null);
        var saved = store.Load();
        Assert.Equal("new", saved.TmdbKey);
        Assert.Equal("vlc", saved.DefaultPlayerId);
        Assert.Equal(@"C:\tools\ffprobe.exe", saved.FfprobePath);
        Assert.Equal("Saved", vm.KeyStatus);

        vm.TmdbKey = "   ";
        vm.SaveKeyCommand.Execute(null);
        Assert.Null(store.Load().TmdbKey);
        Assert.Equal("Key removed", vm.KeyStatus);
    }

    [AvaloniaFact]
    public void Saving_WhenTheSettingsFileCannotBeRead_SavesNothing_AndSaysSo()
    {
        var store = Store;
        store.Save(new AppSettings { TmdbKey = "old", DefaultPlayerId = "vlc" });
        var vm = Create(store);

        using (new FileStream(Path.Combine(_dir, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            vm.TmdbKey = "new";
            vm.SaveKeyCommand.Execute(null);
        }

        Assert.Equal("Settings weren't saved because they couldn't be read. Restart MediaBoard and try again.", vm.KeyStatus);
        var entry = _hub.Recent[^1];
        Assert.Equal(ActivityLevel.Error, entry.Level);
        Assert.StartsWith("Couldn't read settings: ", entry.Message);
        Assert.Equal("Close any program that has settings.json open, then restart MediaBoard.", entry.Hint);
        Assert.Equal(new AppSettings { TmdbKey = "old", DefaultPlayerId = "vlc" }, store.Load());
    }

    [AvaloniaFact]
    public void TmdbKey_IsMaskedUntilShown_AndNeverLogged()
    {
        var vm = Create();
        Assert.False(vm.ShowKey);

        vm.TmdbKey = "super-secret-key";
        vm.SaveKeyCommand.Execute(null);

        Assert.DoesNotContain(_hub.Recent, e => e.Message.Contains("super-secret-key") || (e.Hint ?? "").Contains("super-secret-key"));
        Assert.DoesNotContain("super-secret-key", vm.KeyStatus);
    }

    [AvaloniaFact]
    public void TmdbKey_HelpLinkPresent()
    {
        var vm = Create();

        Assert.Equal("Get a free key", vm.HelpLinkText);
        Assert.Equal("https://www.themoviedb.org/settings/api", vm.HelpLinkUrl);

        vm.OpenHelpLinkCommand.Execute(null);

        Assert.Equal(["https://www.themoviedb.org/settings/api"], _opened);
    }

    [AvaloniaFact]
    public async Task PlayerPicker_ListsDetected_AndBrowse()
    {
        _existingFiles.Add(Path.Combine(ProgramFiles, "MPC-HC", "mpc-hc64.exe"));
        _existingFiles.Add(Path.Combine(ProgramFiles, "mpv", "mpv.exe"));
        var store = Store;
        var vm = Create(store);

        await LoadAsync(vm);

        Assert.Equal(["MPC-HC", "mpv"], vm.Players.Select(p => p.Name));
        Assert.Equal("MPC-HC", vm.SelectedPlayer?.Name); // the default when nothing was chosen
        Assert.Equal("Using MPC-HC", vm.InUse);
        Assert.Equal("Other program…", vm.BrowseLabel);

        // Choosing a detected player saves it.
        vm.SelectedPlayer = vm.Players[1];
        Assert.Equal("mpv", store.Load().DefaultPlayerId);
        await Ui.WaitUntil(() => vm.InUse == "Using mpv");

        // Browsing to a program makes it the custom player.
        _existingFiles.Add(@"D:\Tools\Player\fancy.exe");
        _browsed = @"D:\Tools\Player\fancy.exe";
        await vm.BrowsePlayerCommand.ExecuteAsync(null);
        await Ui.WaitUntil(() => vm.InUse == "Using fancy");

        var saved = store.Load();
        Assert.Equal(PlayerDetector.CustomId, saved.DefaultPlayerId);
        Assert.Equal(@"D:\Tools\Player\fancy.exe", saved.CustomPlayerPath);
        Assert.Null(vm.SelectedPlayer);
        Assert.Equal(@"D:\Tools\Player\fancy.exe", vm.InUseDetail);
    }

    [AvaloniaFact]
    public async Task PlayerPicker_CancelledBrowse_ChangesNothing()
    {
        _existingFiles.Add(Path.Combine(ProgramFiles, "MPC-HC", "mpc-hc64.exe"));
        var store = Store;
        var vm = Create(store);
        await LoadAsync(vm);

        _browsed = null;
        await vm.BrowsePlayerCommand.ExecuteAsync(null);

        Assert.Null(store.Load().DefaultPlayerId);
        Assert.Equal("Using MPC-HC", vm.InUse);
    }

    [AvaloniaFact]
    public async Task PlayerPicker_NoPlayerFound_SaysSo()
    {
        var vm = Create();

        await LoadAsync(vm);

        Assert.Empty(vm.Players);
        Assert.Null(vm.SelectedPlayer);
        Assert.StartsWith("No player found", vm.InUse);
    }

    [AvaloniaFact]
    public void About_ShowsTmdbAttribution()
    {
        var vm = Create();

        Assert.Equal("MediaBoard", vm.AppName);
        Assert.StartsWith("Version ", vm.Version);
        Assert.Equal("This product uses the TMDB API but is not endorsed or certified by TMDB.", vm.TmdbAttribution);
        Assert.Equal("TMDB", vm.TmdbMark);

        // And the page really shows it.
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        Ui.Flush();
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("This product uses the TMDB API but is not endorsed or certified by TMDB.", texts);
        Assert.Contains("TMDB", texts);
        Assert.Contains("Get a free key", view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        window.Close();
    }
}
