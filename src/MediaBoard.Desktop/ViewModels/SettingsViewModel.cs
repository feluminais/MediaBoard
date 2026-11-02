using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Players;
using MediaBoard.Core.Settings;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>
/// Settings: the TMDB key, the player, and About with the TMDB attribution. Saving the key writes settings.json;
/// <see cref="SettingsStore.Changed"/> is what restarts identification. The key is never logged or put in a message.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public const string HelpUrl = "https://www.themoviedb.org/settings/api";
    public const string Attribution = "This product uses the TMDB API but is not endorsed or certified by TMDB.";

    private readonly SettingsStore _settings;
    private readonly PlayerDetector _detector;
    private readonly ActivityHub _hub;
    private readonly Func<Task<string?>> _browsePlayer;
    private readonly Action<string> _openUrl;
    private readonly BackgroundQuery _query;
    private const string UnreadableMessage = "Settings weren't saved because they couldn't be read. Restart MediaBoard and try again.";

    private bool _applying;
    private bool _readFailed;

    public SettingsViewModel(
        SettingsStore settings,
        PlayerDetector detector,
        ActivityHub hub,
        Func<Task<string?>> browsePlayer,
        Action<string> openUrl)
        : base("Settings")
    {
        _settings = settings;
        _detector = detector;
        _hub = hub;
        _browsePlayer = browsePlayer;
        _openUrl = openUrl;
        _query = new BackgroundQuery(hub, "the players");
        var saved = settings.Load();
        TmdbKey = saved.TmdbKey ?? "";
        _applying = true; // showing the saved choices must not save them again
        try
        {
            FollowOtherPlayers = saved.FollowOtherPlayers;
            MpcPort = saved.MpcWebPort.ToString(CultureInfo.InvariantCulture);
            VlcPort = saved.VlcWebPort.ToString(CultureInfo.InvariantCulture);
            VlcPassword = saved.VlcWebPassword ?? "";
        }
        finally
        {
            _applying = false;
        }
    }

    // ---- TMDB key ----

    [ObservableProperty]
    public partial string TmdbKey { get; set; } = "";

    /// <summary>The key is shown as dots until this is on.</summary>
    [ObservableProperty]
    public partial bool ShowKey { get; set; }

    /// <summary>"Saved", "Key removed" or why saving failed; empty while the user edits.</summary>
    [ObservableProperty]
    public partial string KeyStatus { get; private set; } = "";

    public string HelpLinkText => "Get a free key";

    public string HelpLinkUrl => HelpUrl;

    partial void OnTmdbKeyChanged(string value) => KeyStatus = "";

    [RelayCommand]
    private void SaveKey()
    {
        var key = TmdbKey.Trim();
        var saved = Update(s => s with { TmdbKey = key.Length == 0 ? null : key });

        TmdbKey = key;
        KeyStatus = _readFailed ? UnreadableMessage
            : !saved ? "Couldn't save the key. See the activity panel."
            : key.Length == 0 ? "Key removed"
            : "Saved";
    }

    [RelayCommand]
    private void OpenHelpLink()
    {
        try
        {
            _openUrl(HelpUrl);
        }
        catch (Exception e)
        {
            _hub.Log(ActivityLevel.Warning, $"Couldn't open the browser: {e.Message}", $"Open {HelpUrl} yourself.");
        }
    }

    // ---- Player ----

    public string BrowseLabel => "Other program…";

    /// <summary>The players found on this PC, MPC-HC first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PlayerInfo> Players { get; private set; } = [];

    /// <summary>The detected player in use; null when a program from the browse dialog is, or none was found.</summary>
    [ObservableProperty]
    public partial PlayerInfo? SelectedPlayer { get; set; }

    /// <summary>"Using MPC-HC", or why there is no player.</summary>
    [ObservableProperty]
    public partial string InUse { get; private set; } = "";

    /// <summary>The path of the player in use.</summary>
    [ObservableProperty]
    public partial string InUseDetail { get; private set; } = "";

    partial void OnSelectedPlayerChanged(PlayerInfo? value)
    {
        if (_applying || value is null) return;

        if (Update(s => s with { DefaultPlayerId = value.Id })) _ = LoadPlayersAsync();
    }

    [RelayCommand]
    private async Task BrowsePlayerAsync()
    {
        string? path;
        try
        {
            path = await _browsePlayer();
        }
        catch (Exception e)
        {
            _hub.Log(ActivityLevel.Warning, $"Couldn't open the file picker: {e.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(path)) return;

        if (Update(s => s with { DefaultPlayerId = PlayerDetector.CustomId, CustomPlayerPath = path })) await LoadPlayersAsync();
    }

    /// <summary>
    /// Finds the players and which one is in use. Not part of <see cref="PageViewModel.RefreshAsync"/>: that runs on
    /// every library change, and working out the player in use logs a warning while there is none.
    /// </summary>
    public Task LoadPlayersAsync() => _query.RunAsync(ReadPlayers, data =>
    {
        _applying = true; // showing the saved choice must not save it again
        try
        {
            Players = data.Detected;
            SelectedPlayer = data.InUse is { Id: not PlayerDetector.CustomId } chosen
                ? data.Detected.FirstOrDefault(p => p.Id == chosen.Id)
                : null;
            InUse = data.InUse is null ? "No player found. Install MPC-HC, or choose a program." : $"Using {data.InUse.Name}";
            InUseDetail = data.InUse?.ExePath ?? "";
        }
        finally
        {
            _applying = false;
        }
    });

    public override void Opened() => _ = LoadPlayersAsync(); // a player may have been installed since

    // Thread pool: detection reads the registry and probes install folders, which can be slow.
    private PlayersData ReadPlayers() => new(_detector.Detect(), _detector.Resolve(_settings.Load()));

    private sealed record PlayersData(IReadOnlyList<PlayerInfo> Detected, PlayerInfo? InUse);

    // ---- Players opened outside the app ----

    public string FollowTitle => "Other players";

    public string FollowLabel => "Follow players I open myself";

    public string MpvHint => "mpv: add this line to mpv.conf, then restart mpv.";

    public string MpvLine => @"input-ipc-server=\\.\pipe\" + PlayerWatcher.MpvPipeName;

    public string MpcHint => "MPC-HC and MPC-BE: turn on the web interface under Options, Player, and enter its port here.";

    public string VlcHint => "VLC: turn on Web under Interface, Main interfaces (show all settings), set its Lua password, and enter the port and password here.";

    [ObservableProperty]
    public partial bool FollowOtherPlayers { get; set; }

    [ObservableProperty]
    public partial string MpcPort { get; set; } = "";

    [ObservableProperty]
    public partial string VlcPort { get; set; } = "";

    [ObservableProperty]
    public partial string VlcPassword { get; set; } = "";

    /// <summary>"Saved", or why the VLC port or password was not saved; empty while the user edits.</summary>
    [ObservableProperty]
    public partial string FollowStatus { get; private set; } = "";

    partial void OnFollowOtherPlayersChanged(bool value)
    {
        if (!_applying) Update(s => s with { FollowOtherPlayers = value });
    }

    partial void OnMpcPortChanged(string value) => FollowStatus = "";

    partial void OnVlcPortChanged(string value) => FollowStatus = "";

    partial void OnVlcPasswordChanged(string value) => FollowStatus = "";

    [RelayCommand]
    private void SaveMpc()
    {
        if (!TryReadPort(MpcPort, out var port)) return;

        var saved = Update(s => s with { MpcWebPort = port });
        MpcPort = port.ToString(CultureInfo.InvariantCulture);
        FollowStatus = SaveStatus(saved);
    }

    private bool TryReadPort(string text, out int port)
    {
        if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535) return true;

        FollowStatus = "The port is a number from 1 to 65535.";
        return false;
    }

    private string SaveStatus(bool saved) => _readFailed ? UnreadableMessage : saved ? "Saved" : "Couldn't save. See the activity panel.";

    [RelayCommand]
    private void SaveVlc()
    {
        if (!TryReadPort(VlcPort, out var port)) return;

        var password = VlcPassword.Trim();
        var saved = Update(s => s with { VlcWebPort = port, VlcWebPassword = password.Length == 0 ? null : password });
        VlcPort = port.ToString(CultureInfo.InvariantCulture);
        VlcPassword = password;
        FollowStatus = SaveStatus(saved);
    }

    // ---- About ----

    public string AppName => "MediaBoard";

    public string Version { get; } = ReadVersion();

    /// <summary>Stands in for the TMDB logo.</summary>
    public string TmdbMark => "TMDB";

    public string TmdbAttribution => Attribution;

    private static string ReadVersion()
    {
        var assembly = typeof(SettingsViewModel).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "1.0.0";
        var plus = version.IndexOf('+'); // drops the commit hash the SDK appends
        return $"Version {(plus > 0 ? version[..plus] : version)}";
    }

    // ---- Saving ----

    // Reads the file again, so a change made elsewhere is kept. False when the file can't be read (then nothing is
    // written: saving defaults over it would lose the key) or can't be written.
    private bool Update(Func<AppSettings, AppSettings> change)
    {
        _readFailed = false;
        try
        {
            _settings.Update(change);
            return true;
        }
        catch (SettingsUnreadableException e)
        {
            _readFailed = true;
            _hub.Log(ActivityLevel.Error, $"Couldn't read settings: {e.Message}", "Close any program that has settings.json open, then restart MediaBoard.");
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _hub.Log(ActivityLevel.Error, $"Couldn't save settings: {e.Message}", "Make sure you can write to the MediaBoard data folder.");
            return false;
        }
    }
}
