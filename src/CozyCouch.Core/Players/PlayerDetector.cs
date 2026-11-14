using CozyCouch.Core.Activity;
using CozyCouch.Core.Settings;
using Microsoft.Win32;

namespace CozyCouch.Core.Players;

/// <summary>Finds installed media players and picks the one to play with.</summary>
public sealed class PlayerDetector
{
    public const string CustomId = "custom";

    private const string MpcHcId = "mpc-hc";

    private sealed record Known(string Id, string Name, string DisplayNamePrefix, string[] ExeNames);

    // Order matters: it is the "first detected" order, and MPC-HC comes first as the default.
    private static readonly Known[] Players =
    [
        new(MpcHcId, "MPC-HC", "MPC-HC", ["mpc-hc64.exe", "mpc-hc.exe"]),
        new("mpc-be", "MPC-BE", "MPC-BE", ["mpc-be64.exe", "mpc-be.exe"]),
        new("mpv", "mpv", "mpv", ["mpv.exe"]),
        new("vlc", "VLC", "VLC", ["vlc.exe"]),
    ];

    private readonly Func<string, bool> _fileExists;
    private readonly Func<IEnumerable<(string DisplayName, string? InstallLocation)>> _uninstallEntries;
    private readonly ActivityHub _hub;

    public PlayerDetector(
        Func<string, bool> fileExists,
        Func<IEnumerable<(string DisplayName, string? InstallLocation)>> uninstallEntries,
        ActivityHub hub)
    {
        _fileExists = fileExists;
        _uninstallEntries = uninstallEntries;
        _hub = hub;
    }

    /// <summary>A detector wired to the real file system and the registry's Uninstall keys.</summary>
    public static PlayerDetector CreateDefault(ActivityHub hub) =>
        new(File.Exists, ReadUninstallEntries, hub);

    /// <summary>Detected players, one per player id: known install paths first, then the registry, then PATH.</summary>
    public IReadOnlyList<PlayerInfo> Detect()
    {
        var found = new List<PlayerInfo>();

        void Add(string id, string exePath)
        {
            if (found.Any(p => p.Id == id)) return;
            var known = Players.First(p => p.Id == id);
            found.Add(new PlayerInfo(id, known.Name, exePath, TracksProgress: true));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        (string Id, string Path)[] knownPaths =
        [
            (MpcHcId, Path.Combine(programFiles, "MPC-HC", "mpc-hc64.exe")),
            (MpcHcId, Path.Combine(programFilesX86, "MPC-HC", "mpc-hc.exe")),
            ("mpc-be", Path.Combine(programFiles, "MPC-BE x64", "mpc-be64.exe")),
            ("mpv", Path.Combine(programFiles, "mpv", "mpv.exe")),
            ("vlc", Path.Combine(programFiles, "VideoLAN", "VLC", "vlc.exe")),
        ];
        foreach (var (id, path) in knownPaths)
        {
            if (_fileExists(path)) Add(id, path);
        }

        foreach (var (displayName, installLocation) in _uninstallEntries())
        {
            var known = Players.FirstOrDefault(p => displayName.StartsWith(p.DisplayNamePrefix, StringComparison.OrdinalIgnoreCase));
            if (known is null || string.IsNullOrWhiteSpace(installLocation)) continue;

            foreach (var exe in known.ExeNames)
            {
                var path = Path.Combine(installLocation.Trim().Trim('"'), exe);
                if (!_fileExists(path)) continue;
                Add(known.Id, path);
                break;
            }
        }

        var onPath = FindOnPath("mpv.exe");
        if (onPath is not null) Add("mpv", onPath);

        return found.OrderBy(p => Array.FindIndex(Players, k => k.Id == p.Id)).ToList();
    }

    /// <summary>The player to play with, or null (after logging why) when there is none.</summary>
    public PlayerInfo? Resolve(AppSettings settings)
    {
        if (settings.DefaultPlayerId == CustomId)
        {
            var path = settings.CustomPlayerPath;
            if (!string.IsNullOrWhiteSpace(path) && _fileExists(path))
            {
                return new PlayerInfo(CustomId, Path.GetFileNameWithoutExtension(path), path, TracksProgress: false);
            }

            _hub.Log(ActivityLevel.Warning, $"Player not found: {path ?? "custom player"}", "Choose another player in Settings");
            return null;
        }

        var detected = Detect();

        if (!string.IsNullOrEmpty(settings.DefaultPlayerId))
        {
            var chosen = detected.FirstOrDefault(p => p.Id == settings.DefaultPlayerId);
            if (chosen is not null) return chosen;

            var name = Players.FirstOrDefault(p => p.Id == settings.DefaultPlayerId)?.Name ?? settings.DefaultPlayerId;
            _hub.Log(ActivityLevel.Warning, $"Player not found: {name}", "Choose another player in Settings");
        }

        var fallback = detected.FirstOrDefault(p => p.Id == MpcHcId) ?? detected.FirstOrDefault();
        if (fallback is null)
        {
            _hub.Log(ActivityLevel.Warning, "No media player found", "Install MPC-HC or choose a player in Settings");
        }

        return fallback;
    }

    private string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (_fileExists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped.
            }
        }

        return null;
    }

    private static IEnumerable<(string DisplayName, string? InstallLocation)> ReadUninstallEntries()
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        RegistryHive[] hives = [RegistryHive.LocalMachine, RegistryHive.CurrentUser];
        RegistryView[] views = [RegistryView.Registry64, RegistryView.Registry32];

        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                foreach (var entry in ReadKey(hive, view, uninstall)) yield return entry;
            }
        }
    }

    private static List<(string DisplayName, string? InstallLocation)> ReadKey(RegistryHive hive, RegistryView view, string subKey)
    {
        var entries = new List<(string, string?)>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(subKey);
            if (root is null) return entries;

            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var app = root.OpenSubKey(name);
                    if (app?.GetValue("DisplayName") is string displayName && !string.IsNullOrWhiteSpace(displayName))
                    {
                        entries.Add((displayName, app.GetValue("InstallLocation") as string));
                    }
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // An unreadable entry is skipped.
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // An unreadable hive or view is skipped.
        }

        return entries;
    }
}
