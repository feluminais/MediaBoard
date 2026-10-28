using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MediaBoard.Core.Players.Vlc;

/// <summary>
/// Plays one file in VLC and follows it through VLC's HTTP interface: VLC is started with <c>--extraintf http</c> on
/// 127.0.0.1, on a free port and with a random password, and the session polls <c>/requests/status.json</c>.
/// VLC's own configuration is never touched.
/// </summary>
/// <remarks>
/// <see cref="Event"/> is raised on a thread-pool thread, one at a time and in order, with no lock held. Handlers
/// must return quickly, must not block on this session, and must marshal to the UI thread with
/// <c>InvokeAsync</c>/<c>Post</c>, never a synchronous <c>Invoke</c>. <see cref="PlaybackEvent.Closed"/> and
/// <see cref="PlaybackEvent.Failed"/> are final: nothing is raised after them. Disposal stops new events, but one
/// already being delivered may still arrive while, or just after, the session is disposed. A handler that throws is
/// traced and does not stop delivery of the events after it.
/// <para>
/// While VLC is still starting its HTTP interface does not answer yet; failed requests are ignored for as long as
/// the process lives, and asked again quickly until VLC answers the first time.
/// </para>
/// </remarks>
public sealed class VlcSession : IPlayerSession
{
    private const string Name = "VLC";
    private const string PasswordAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const int PasswordLength = 24;

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly VlcStatusClient SharedClient = new();

    private readonly PlayerInfo _player;
    private readonly VlcStatusFetcher _fetch;
    private readonly Func<ProcessStartInfo, IPlayerProcess> _start;
    private readonly TimeProvider _time;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _startupInterval;
    private readonly TimeSpan _closeTimeout;

    private readonly Lock _gate = new();
    private readonly EventPump _pump;
    private readonly CancellationTokenSource _stop = new();

    private IPlayerProcess? _process;
    private long _startStamp;
    private bool _started;
    private bool _disposed;
    private volatile bool _detached;
    private bool _answered;
    private int _port;
    private string _password = "";

    // Touched only by the poll loop.
    private bool _loadedRaised;
    private bool _sawPlaying;
    private bool _endedRaised;
    private VlcState? _lastState;

    /// <param name="player">VLC as detected; <see cref="PlayerInfo.ExePath"/> is launched.</param>
    /// <param name="fetch">Asks VLC's HTTP interface for the status.</param>
    /// <param name="start">Starts the player process.</param>
    /// <param name="time">The clock for judging an early exit.</param>
    /// <param name="pollInterval">How often the status is asked for once VLC answers (default 5 s).</param>
    /// <param name="closeTimeout">How long disposal waits for VLC to close before killing it (default 3 s).</param>
    public VlcSession(
        PlayerInfo player,
        VlcStatusFetcher fetch,
        Func<ProcessStartInfo, IPlayerProcess> start,
        TimeProvider? time = null,
        TimeSpan? pollInterval = null,
        TimeSpan? closeTimeout = null)
    {
        _player = player;
        _fetch = fetch;
        _start = start;
        _time = time ?? TimeProvider.System;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _startupInterval = _pollInterval < StartupPollInterval ? _pollInterval : StartupPollInterval;
        _closeTimeout = closeTimeout ?? PlayerLaunch.DefaultCloseTimeout;
        _pump = new EventPump(Name, () => Event);
    }

    /// <summary>A session wired to a real process and VLC's HTTP interface.</summary>
    public static VlcSession Create(PlayerInfo player) =>
        new(player, SharedClient.FetchAsync, PlayerProcess.Start);

    public bool TracksProgress => true;

    public event Action<PlaybackEvent>? Event;

    public Task StartAsync(string filePath, TimeSpan? startAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stop = _stop.Token;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("A session plays one file; create a new session.");
            _started = true;
        }

        int port;
        try
        {
            port = FreePort();
        }
        catch (SocketException e)
        {
            _pump.Raise(new PlaybackEvent.Failed($"{Name} could not start: {e.Message}"));
            return Task.CompletedTask;
        }

        var password = RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength);

        var startInfo = new ProcessStartInfo(_player.ExePath) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--extraintf");
        startInfo.ArgumentList.Add("http");
        startInfo.ArgumentList.Add("--http-host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--http-port");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--http-password");
        startInfo.ArgumentList.Add(password);
        if (startAt > TimeSpan.Zero)
        {
            startInfo.ArgumentList.Add("--start-time=" + startAt.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        startInfo.ArgumentList.Add(filePath);

        _startStamp = _time.GetTimestamp();
        var process = PlayerLaunch.TryStart(_start, startInfo, Name, _player.ExePath, out var failure);
        if (process is null)
        {
            _pump.Raise(new PlaybackEvent.Failed(failure));
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            _process = process;
            (_port, _password) = (port, password);
        }

        _ = PollAsync(port, password, stop);
        _ = ObserveExitAsync(process);
        return Task.CompletedTask;
    }

    /// <summary>Asks VLC for its position right now; null when it does not answer or is not playing.</summary>
    public async Task<TimeSpan?> GetPositionAsync(CancellationToken ct)
    {
        int port;
        string password;
        lock (_gate)
        {
            if (_pump.IsFinished || _port == 0) return null;
            (port, password) = (_port, _password);
        }

        var status = await TryFetchAsync(port, password, ct).ConfigureAwait(false);
        return status is { State: not VlcState.Stopped } playing ? playing.Time : null;
    }

    public void Detach() => _detached = true;

    /// <summary>Closes VLC if this session started it and it is still running (asks first, kills after the close timeout). Raises no events.</summary>
    public async ValueTask DisposeAsync()
    {
        IPlayerProcess? process;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            process = _process;
        }

        _pump.Stop();
        await _stop.CancelAsync();
        if (process is not null) await PlayerLaunch.ReleaseAsync(process, _closeTimeout, _detached);
    }

    /// <summary>A port nothing listens on right now. VLC binds it a moment later, so another program could in theory take it first.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task PollAsync(int port, string password, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (await TryFetchAsync(port, password, stop).ConfigureAwait(false) is { } status)
                {
                    lock (_gate) _answered = true;
                    Apply(status);
                }

                bool answered;
                lock (_gate) answered = _answered;
                await Task.Delay(answered ? _pollInterval : _startupInterval, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (Exception e)
        {
            Trace.TraceError($"{Name} session: polling failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The status, or null when VLC did not answer (it may still be starting) or said something unusable.</summary>
    private async Task<VlcStatus?> TryFetchAsync(int port, string password, CancellationToken ct)
    {
        try
        {
            var json = await _fetch(port, password, ct).ConfigureAwait(false);
            return VlcStatus.TryParse(json, out var status) ? status : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private void Apply(VlcStatus status)
    {
        if (!_loadedRaised && status.State != VlcState.Stopped)
        {
            _loadedRaised = true;
            _pump.Raise(new PlaybackEvent.Loaded(status.Length));
        }

        var changed = _lastState != status.State;
        _lastState = status.State;
        switch (status.State)
        {
            case VlcState.Playing:
                (_sawPlaying, _endedRaised) = (true, false);
                if (changed) _pump.Raise(new PlaybackEvent.Playing());
                _pump.Raise(new PlaybackEvent.Position(status.Time));
                break;
            case VlcState.Paused:
                (_sawPlaying, _endedRaised) = (true, false);
                if (changed)
                {
                    _pump.Raise(new PlaybackEvent.Paused());
                    _pump.Raise(new PlaybackEvent.Position(status.Time));
                }

                break;
            case VlcState.Stopped:
                // Stopped is also what VLC says before it has opened the file; only a stop after playing is the end.
                if (_sawPlaying && !_endedRaised)
                {
                    _endedRaised = true;
                    _pump.Raise(new PlaybackEvent.Ended());
                }

                break;
        }
    }

    private async Task ObserveExitAsync(IPlayerProcess process)
    {
        await process.Exited.ConfigureAwait(false);

        bool answered;
        lock (_gate)
        {
            if (_disposed) return;
            answered = _answered;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _pump.Raise(PlayerLaunch.ExitEvent(Name, PlayerLaunch.ExitCodeOrZero(process), answered, _time.GetElapsedTime(_startStamp)));
    }
}
