using System.Diagnostics;
using System.Globalization;

namespace MediaBoard.Core.Players.Mpv;

/// <summary>
/// Plays one file in mpv and follows it over mpv's JSON IPC: mpv is started with <c>--input-ipc-server</c> on a
/// named pipe of its own, and the session observes <c>time-pos</c>, <c>duration</c>, <c>pause</c> and
/// <c>eof-reached</c>. mpv's own configuration is never touched.
/// </summary>
/// <remarks>
/// <see cref="Event"/> is raised on a thread-pool thread, one at a time and in order, with no lock held. Handlers
/// must return quickly, must not block on this session, and must marshal to the UI thread with
/// <c>InvokeAsync</c>/<c>Post</c>, never a synchronous <c>Invoke</c>. <see cref="PlaybackEvent.Closed"/> and
/// <see cref="PlaybackEvent.Failed"/> are final: nothing is raised after them. Disposal stops new events, but one
/// already being delivered may still arrive while, or just after, the session is disposed. A handler that throws is
/// traced and does not stop delivery of the events after it.
/// <para>
/// <see cref="PlaybackEvent.Position"/> is raised at most once a second; <see cref="GetPositionAsync"/> returns the
/// latest position mpv reported, unthrottled.
/// </para>
/// </remarks>
public sealed class MpvSession : IPlayerSession
{
    private const string Name = "mpv";
    private const string PipeDirectory = @"\\.\pipe\";

    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultExitDrain = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PositionInterval = TimeSpan.FromSeconds(1);

    private readonly PlayerInfo _player;
    private readonly Func<string, CancellationToken, Task<IMpvPipe?>> _connect;
    private readonly Func<ProcessStartInfo, IPlayerProcess> _start;
    private readonly TimeProvider _time;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _closeTimeout;
    private readonly TimeSpan _exitDrain;

    private readonly Lock _gate = new();
    private readonly EventPump _pump;
    private readonly CancellationTokenSource _stop = new();

    private IPlayerProcess? _process;
    private IMpvPipe? _pipe;
    private Task _run = Task.CompletedTask;
    private long _startStamp;
    private bool _started;
    private bool _disposed;
    private volatile bool _detached;
    private bool _answered;
    private TimeSpan? _lastTimePos;

    // Touched only by the read loop.
    private bool _loadedRaised;
    private bool? _paused;
    private bool _endedRaised;
    private long? _lastPositionStamp;

    /// <param name="player">mpv as detected; <see cref="PlayerInfo.ExePath"/> is launched.</param>
    /// <param name="connect">One attempt to connect to the named pipe (name without <c>\\.\pipe\</c>); null when it does not exist yet.</param>
    /// <param name="start">Starts the player process.</param>
    /// <param name="time">The clock for throttling positions and judging an early exit.</param>
    /// <param name="connectTimeout">How long to keep trying to connect after launch (default 5 s).</param>
    /// <param name="retryDelay">The pause between connection attempts (default 100 ms).</param>
    /// <param name="closeTimeout">How long disposal waits for mpv to close before killing it (default 3 s).</param>
    /// <param name="exitDrain">After mpv exits, how long to let the pipe deliver what it still holds (default 1 s).</param>
    public MpvSession(
        PlayerInfo player,
        Func<string, CancellationToken, Task<IMpvPipe?>> connect,
        Func<ProcessStartInfo, IPlayerProcess> start,
        TimeProvider? time = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? retryDelay = null,
        TimeSpan? closeTimeout = null,
        TimeSpan? exitDrain = null)
    {
        _player = player;
        _connect = connect;
        _start = start;
        _time = time ?? TimeProvider.System;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _closeTimeout = closeTimeout ?? PlayerLaunch.DefaultCloseTimeout;
        _exitDrain = exitDrain ?? DefaultExitDrain;
        _pump = new EventPump(Name, () => Event);
    }

    /// <summary>A session wired to a real named pipe and process.</summary>
    public static MpvSession Create(PlayerInfo player) =>
        new(player, NamedPipeMpvPipe.TryConnectAsync, PlayerProcess.Start);

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

        var pipeName = "mediaboard-" + Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo(_player.ExePath) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--input-ipc-server=" + PipeDirectory + pipeName);
        startInfo.ArgumentList.Add("--force-window=yes");
        if (startAt > TimeSpan.Zero)
        {
            startInfo.ArgumentList.Add("--start=" + startAt.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        startInfo.ArgumentList.Add(filePath);

        _startStamp = _time.GetTimestamp();
        var process = PlayerLaunch.TryStart(_start, startInfo, Name, _player.ExePath, out var failure);
        if (process is null)
        {
            _pump.Raise(new PlaybackEvent.Failed(failure));
            return Task.CompletedTask;
        }

        var run = RunAsync(pipeName, stop);
        lock (_gate)
        {
            _process = process;
            _run = run;
        }

        _ = ObserveExitAsync(process);
        return Task.CompletedTask;
    }

    public Task<TimeSpan?> GetPositionAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_pump.IsFinished ? null : _lastTimePos);
    }

    public void Detach() => _detached = true;

    /// <summary>
    /// Closes mpv if this session started it and it is still running (asks first, kills after the close timeout),
    /// then closes the pipe. Raises no events.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        IPlayerProcess? process;
        IMpvPipe? pipe;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            (process, pipe) = (_process, _pipe);
        }

        _pump.Stop();
        await _stop.CancelAsync();
        pipe?.Dispose();
        if (process is not null) await PlayerLaunch.ReleaseAsync(process, _closeTimeout, _detached);
    }

    /// <summary>Connects (mpv creates the pipe a moment after it starts), asks for the properties, then reads until mpv closes the pipe.</summary>
    private async Task RunAsync(string pipeName, CancellationToken stop)
    {
        try
        {
            var pipe = await ConnectAsync(pipeName, stop).ConfigureAwait(false);
            if (pipe is null)
            {
                _pump.Raise(new PlaybackEvent.Failed("Could not connect to mpv"));
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    pipe.Dispose();
                    return;
                }

                _pipe = pipe;
                _answered = true;
            }

            var id = 1;
            foreach (var property in new[] { MpvProtocol.TimePosProperty, MpvProtocol.DurationProperty, MpvProtocol.PauseProperty, MpvProtocol.EofReachedProperty })
            {
                await pipe.WriteLineAsync(MpvProtocol.Observe(id++, property), stop).ConfigureAwait(false);
            }

            while (await pipe.ReadLineAsync(stop).ConfigureAwait(false) is { } line)
            {
                OnLine(line);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopped, or mpv went away; the process exit reports what happened.
        }
        catch (Exception e)
        {
            Trace.TraceError($"mpv session: reading from mpv failed: {e}");
        }
    }

    private async Task<IMpvPipe?> ConnectAsync(string pipeName, CancellationToken stop)
    {
        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        attempts.CancelAfter(_connectTimeout);
        try
        {
            while (true)
            {
                var pipe = await _connect(pipeName, attempts.Token).ConfigureAwait(false);
                if (pipe is not null) return pipe;
                await Task.Delay(_retryDelay, attempts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return null; // out of time
        }
    }

    private void OnLine(string line)
    {
        switch (MpvProtocol.Parse(line))
        {
            case MpvMessage.TimePos t:
                OnTimePos(t.At);
                break;
            case MpvMessage.Duration d:
                if (!_loadedRaised)
                {
                    _loadedRaised = true;
                    _pump.Raise(new PlaybackEvent.Loaded(d.Length));
                }

                break;
            case MpvMessage.Pause p:
                if (_paused != p.Paused)
                {
                    _paused = p.Paused;
                    _pump.Raise(p.Paused ? new PlaybackEvent.Paused() : new PlaybackEvent.Playing());
                }

                break;
            case MpvMessage.EofReached e:
                if (e.Reached) RaiseEnded();
                else _endedRaised = false;
                break;
            case MpvMessage.EndFile { Reason: "eof" }:
                RaiseEnded();
                break;
            case MpvMessage.EndFile { Reason: "error" }:
                _pump.Raise(new PlaybackEvent.Failed("mpv could not open the file"));
                break;
        }
    }

    private void OnTimePos(TimeSpan at)
    {
        lock (_gate) _lastTimePos = at;

        var now = _time.GetTimestamp();
        if (_lastPositionStamp is { } last && _time.GetElapsedTime(last, now) < PositionInterval) return;
        _lastPositionStamp = now;
        _pump.Raise(new PlaybackEvent.Position(at));
    }

    /// <summary>mpv says <c>eof-reached</c> and then <c>end-file</c>; the file ended once.</summary>
    private void RaiseEnded()
    {
        if (_endedRaised) return;
        _endedRaised = true;
        _pump.Raise(new PlaybackEvent.Ended());
    }

    private async Task ObserveExitAsync(IPlayerProcess process)
    {
        await process.Exited.ConfigureAwait(false);

        bool answered;
        Task run;
        lock (_gate)
        {
            if (_disposed) return;
            (answered, run) = (_answered, _run);
        }

        // mpv closes its end of the pipe on the way out. Let the read loop deliver what is still in it (the
        // end-file event comes just before the exit) before the final event shuts the session.
        if (answered) await Task.WhenAny(run, Task.Delay(_exitDrain)).ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);

        var exit = PlayerLaunch.ExitEvent(Name, PlayerLaunch.ExitCodeOrZero(process), answered, _time.GetElapsedTime(_startStamp));
        _pump.Raise(exit);
    }
}
