using System.Diagnostics;
using MediaBoard.Core.Players;
using MediaBoard.Core.Players.Mpv;
using MediaBoard.Core.Players.Vlc;
using MediaBoard.Core.Settings;

namespace MediaBoard.Core.Playback;

/// <summary>
/// Looks every few seconds for a player the viewer started without MediaBoard, and hands the one it finds to the
/// playback service, so an episode watched that way keeps its progress too. It only looks while nothing is followed
/// and <see cref="AppSettings.FollowOtherPlayers"/> is on. A player is found only if it was set up to listen: mpv
/// with <c>input-ipc-server=\\.\pipe\mediaboard-mpv</c> in its <c>mpv.conf</c>, VLC with its web interface on and
/// the port and password from the settings.
/// </summary>
public sealed class PlayerWatcher : IAsyncDisposable
{
    /// <summary>The pipe name MediaBoard looks for; the viewer puts it in <c>mpv.conf</c>.</summary>
    public const string MpvPipeName = "mediaboard-mpv";

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(3);

    /// <summary>A session to try for the player a probe stands for, or null when that player is not worth trying now.</summary>
    public delegate IAttachableSession? Probe(AppSettings settings);

    private readonly SettingsStore _settings;
    private readonly Func<IAttachableSession, CancellationToken, Task<bool>> _attach;
    private readonly Func<string?> _currentPlayer;
    private readonly IReadOnlyList<Probe> _probes;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private int _disposed;

    /// <param name="settings">Read on every look, so a change takes effect at once.</param>
    /// <param name="attach">Hands a session to the playback service; false when it did not attach.</param>
    /// <param name="currentPlayer">The name of the player the playback service follows now, or null when none.</param>
    /// <param name="probes">The players to look for, in order (default mpv, then VLC).</param>
    /// <param name="interval">How often to look (default 3 s).</param>
    public PlayerWatcher(
        SettingsStore settings,
        Func<IAttachableSession, CancellationToken, Task<bool>> attach,
        Func<string?> currentPlayer,
        IReadOnlyList<Probe>? probes = null,
        TimeSpan? interval = null)
    {
        _settings = settings;
        _attach = attach;
        _currentPlayer = currentPlayer;
        _probes = probes ?? DefaultProbes;
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>mpv on its fixed pipe; VLC on its web interface, when VLC runs and a password is set.</summary>
    public static IReadOnlyList<Probe> DefaultProbes { get; } =
    [
        _ => MpvSession.CreateAttached(MpvPipeName),
        s => !string.IsNullOrEmpty(s.VlcWebPassword) && IsRunning("vlc")
            ? VlcSession.CreateAttached(s.VlcWebPort, s.VlcWebPassword)
            : null,
    ];

    /// <summary>Starts looking. Call once.</summary>
    public void Start() => _loop = Task.Run(() => RunAsync(_stop.Token));

    /// <summary>One look: follows the first player that answers, unless one is followed already or the setting is off.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        AppSettings settings;
        try
        {
            settings = _settings.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (!settings.FollowOtherPlayers || _currentPlayer() is not null) return;

        foreach (var probe in _probes)
        {
            var session = probe(settings);
            if (session is null) continue;

            try
            {
                if (await _attach(session, ct).ConfigureAwait(false)) return;
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            {
                Trace.TraceWarning($"Looking for a player failed: {e}");
            }
        }
    }

    /// <summary>Stops looking. Safe to call twice: the app stops it, then the service container does.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false); // RunAsync catches everything
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            do
            {
                await CheckAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (Exception e)
        {
            Trace.TraceError($"Looking for players stopped: {e}");
        }
    }

    private static bool IsRunning(string processName)
    {
        try
        {
            var found = Process.GetProcessesByName(processName);
            foreach (var process in found) process.Dispose();
            return found.Length > 0;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
