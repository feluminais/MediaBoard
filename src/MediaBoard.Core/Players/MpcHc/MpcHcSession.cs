using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace MediaBoard.Core.Players.MpcHc;

/// <summary>
/// Plays one file in MPC-HC started in slave mode (<c>/slave &lt;hwnd&gt;</c>), resumes at a saved position and
/// reports what happens through <see cref="Event"/>. MPC-HC's own settings are never touched.
/// </summary>
/// <remarks>
/// <see cref="Event"/> is raised on the message-window thread (for messages from MPC-HC) or on a thread-pool
/// thread (when the process exits or start fails). Handlers must return quickly, must not block on this session,
/// and must marshal to the UI thread themselves. <see cref="PlaybackEvent.Closed"/> and
/// <see cref="PlaybackEvent.Failed"/> are final: nothing is raised after them, nor after disposal.
/// </remarks>
public sealed class MpcHcSession : IPlayerSession
{
    private const string EarlyExitMessage = "MPC-HC closed right after starting";
    private const string OpenFailedMessage = "MPC-HC could not open the file";

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultCloseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EarlyExitWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PositionReplyTimeout = TimeSpan.FromSeconds(2);

    // MPC-HC seeks to its own remembered position while opening, before it reports Loaded, so the app's seek
    // after Loaded should win. In case it does not, the position is checked once more shortly after.
    private static readonly TimeSpan ResumeCheckDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ResumeTolerance = TimeSpan.FromSeconds(2);

    private readonly PlayerInfo _player;
    private readonly Func<IMpcTransport> _transportFactory;
    private readonly Func<ProcessStartInfo, IPlayerProcess> _start;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _closeTimeout;

    private readonly Lock _gate = new();
    private readonly Lock _raiseGate = new();
    private readonly Stopwatch _sinceStart = new();

    private IMpcTransport? _transport;
    private IPlayerProcess? _process;
    private nint _playerWindow;
    private bool _started;
    private bool _disposed;
    private TimeSpan? _startAt;
    private bool _fileLoaded;
    private NowPlaying? _nowPlaying;
    private bool _loadedRaised;
    private MpcPlayState? _playState;
    private Timer? _pollTimer;
    private int _polling;
    private TaskCompletionSource<TimeSpan>? _positionReply;
    private volatile bool _finished;

    /// <param name="player">MPC-HC as detected; <see cref="PlayerInfo.ExePath"/> is launched.</param>
    /// <param name="transportFactory">Creates the window MPC-HC talks to; called once by <see cref="StartAsync"/>.</param>
    /// <param name="start">Starts the player process.</param>
    /// <param name="pollInterval">How often the position is asked for while playing (default 5 s).</param>
    /// <param name="closeTimeout">How long disposal waits for MPC-HC to close before killing it (default 3 s).</param>
    public MpcHcSession(
        PlayerInfo player,
        Func<IMpcTransport> transportFactory,
        Func<ProcessStartInfo, IPlayerProcess> start,
        TimeSpan? pollInterval = null,
        TimeSpan? closeTimeout = null)
    {
        _player = player;
        _transportFactory = transportFactory;
        _start = start;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _closeTimeout = closeTimeout ?? DefaultCloseTimeout;
    }

    /// <summary>A session wired to a real message window and process.</summary>
    public static MpcHcSession Create(PlayerInfo player, TimeSpan? pollInterval = null) =>
        new(player, () => new MessageWindow(), PlayerProcess.Start, pollInterval);

    public bool TracksProgress => true;

    public event Action<PlaybackEvent>? Event;

    public Task StartAsync(string filePath, TimeSpan? startAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("A session plays one file; create a new session.");
            _started = true;
            _startAt = startAt > TimeSpan.Zero ? startAt : null;
        }

        IMpcTransport transport;
        try
        {
            transport = _transportFactory();
        }
        catch (Win32Exception e)
        {
            Raise(new PlaybackEvent.Failed($"MPC-HC could not start: {e.Message}"));
            return Task.CompletedTask;
        }

        transport.Received += OnReceived;
        lock (_gate) _transport = transport;

        var startInfo = new ProcessStartInfo(_player.ExePath) { UseShellExecute = false };
        startInfo.ArgumentList.Add("/slave");
        startInfo.ArgumentList.Add(((long)transport.HostHandle).ToString(CultureInfo.InvariantCulture));
        // A new instance even when MPC-HC is set to reuse a running one: a file handed over to another
        // instance would leave this session talking to a process that has already exited.
        startInfo.ArgumentList.Add("/new");
        startInfo.ArgumentList.Add(filePath);

        IPlayerProcess process;
        _sinceStart.Start();
        try
        {
            process = _start(startInfo);
        }
        catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3)
        {
            Raise(new PlaybackEvent.Failed($"MPC-HC not found: {_player.ExePath}"));
            return Task.CompletedTask;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Raise(new PlaybackEvent.Failed($"MPC-HC could not start: {e.Message}"));
            return Task.CompletedTask;
        }

        lock (_gate) _process = process;
        process.Exited.ContinueWith(_ => OnProcessExited(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public async Task<TimeSpan?> GetPositionAsync(CancellationToken ct)
    {
        TaskCompletionSource<TimeSpan> reply;
        lock (_gate)
        {
            if (_finished || _playerWindow == 0) return null;
            reply = _positionReply ??= new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        SendToPlayer(MpcApi.CMD_GETCURRENTPOSITION, "");
        try
        {
            return await reply.Task.WaitAsync(PositionReplyTimeout, ct);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (_positionReply == reply) _positionReply = null;
            }

            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // disposed while waiting
        }
    }

    /// <summary>Jumps to a position. Does nothing until MPC-HC has connected.</summary>
    public void Seek(TimeSpan at) => SendToPlayer(MpcApi.CMD_SETPOSITION, MpcApi.FormatSeconds(at));

    /// <summary>Pauses playback. Does nothing until MPC-HC has connected.</summary>
    public void Pause() => SendToPlayer(MpcApi.CMD_PAUSE, "");

    /// <summary>Asks MPC-HC to close; <see cref="PlaybackEvent.Closed"/> follows. Does nothing until it has connected.</summary>
    public void Close() => SendToPlayer(MpcApi.CMD_CLOSEAPP, "");

    /// <summary>
    /// Closes MPC-HC if this session started it and it is still running (asks first, kills after the close
    /// timeout), then destroys the message window. Raises no events.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        IPlayerProcess? process;
        IMpcTransport? transport;
        Timer? timer;
        TaskCompletionSource<TimeSpan>? reply;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            (process, transport, timer, reply) = (_process, _transport, _pollTimer, _positionReply);
            _pollTimer = null;
            _positionReply = null;
        }

        lock (_raiseGate) _finished = true;
        reply?.TrySetCanceled();
        if (timer is not null) await timer.DisposeAsync();

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    await Task.WhenAny(process.Exited, Task.Delay(_closeTimeout));
                    if (!process.HasExited)
                    {
                        process.Kill();
                        await Task.WhenAny(process.Exited, Task.Delay(_closeTimeout));
                    }
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        if (transport is not null)
        {
            transport.Received -= OnReceived;
            transport.Dispose();
        }
    }

    private void OnReceived(uint cmd, string payload)
    {
        if (_finished) return;

        switch (cmd)
        {
            case MpcApi.CMD_CONNECT:
                if (long.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hwnd))
                {
                    lock (_gate) _playerWindow = (nint)hwnd;
                }

                break;
            case MpcApi.CMD_STATE:
                if (int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var loadState))
                {
                    OnLoadState((MpcLoadState)loadState);
                }

                break;
            case MpcApi.CMD_PLAYMODE:
                if (int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var playState))
                {
                    OnPlayState((MpcPlayState)playState);
                }

                break;
            case MpcApi.CMD_NOWPLAYING:
                OnNowPlaying(payload);
                break;
            case MpcApi.CMD_CURRENTPOSITION or MpcApi.CMD_NOTIFYSEEK:
                OnPosition(payload);
                break;
            case MpcApi.CMD_NOTIFYENDOFSTREAM:
                Raise(new PlaybackEvent.Ended());
                break;
            case MpcApi.CMD_DISCONNECT:
                StopPolling();
                Raise(new PlaybackEvent.Closed());
                break;
        }
    }

    private void OnLoadState(MpcLoadState state)
    {
        if (state == MpcLoadState.Failing)
        {
            StopPolling();
            Raise(new PlaybackEvent.Failed(OpenFailedMessage));
            return;
        }

        if (state != MpcLoadState.Loaded) return;

        TimeSpan? startAt;
        lock (_gate)
        {
            if (_fileLoaded) return;
            _fileLoaded = true;
            startAt = _startAt;
        }

        if (startAt is { } at) SendToPlayer(MpcApi.CMD_SETPOSITION, MpcApi.FormatSeconds(at));
        RaiseLoadedOnce();
    }

    private void OnNowPlaying(string payload)
    {
        NowPlaying nowPlaying;
        try
        {
            nowPlaying = MpcApi.ParseNowPlaying(payload);
        }
        catch (FormatException)
        {
            return; // A DVD or malformed payload carries no usable duration.
        }

        lock (_gate) _nowPlaying ??= nowPlaying;
        RaiseLoadedOnce();
    }

    /// <summary>Loaded needs both the Loaded state and the now-playing duration; MPC-HC sends the state first.</summary>
    private void RaiseLoadedOnce()
    {
        TimeSpan duration;
        TimeSpan? startAt;
        lock (_gate)
        {
            if (_loadedRaised || !_fileLoaded || _nowPlaying is null) return;
            _loadedRaised = true;
            duration = _nowPlaying.Duration;
            startAt = _startAt;
        }

        Raise(new PlaybackEvent.Loaded(duration));
        if (startAt is { } at) _ = CheckResumeAsync(at);
    }

    private async Task CheckResumeAsync(TimeSpan startAt)
    {
        await Task.Delay(ResumeCheckDelay).ConfigureAwait(false);
        var at = await GetPositionAsync(CancellationToken.None).ConfigureAwait(false);
        if (at is not null && (at.Value - startAt).Duration() > ResumeTolerance)
        {
            SendToPlayer(MpcApi.CMD_SETPOSITION, MpcApi.FormatSeconds(startAt));
        }
    }

    private void OnPlayState(MpcPlayState state)
    {
        lock (_gate)
        {
            if (_playState == state) return;
            _playState = state;
            if (state == MpcPlayState.Play)
            {
                _pollTimer ??= new Timer(_ => Poll());
                _pollTimer.Change(_pollInterval, _pollInterval);
            }
            else
            {
                _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        switch (state)
        {
            case MpcPlayState.Play:
                Raise(new PlaybackEvent.Playing());
                break;
            case MpcPlayState.Pause:
                Raise(new PlaybackEvent.Paused());
                break;
        }
    }

    private void Poll()
    {
        // A slow reply must not pile up timer callbacks.
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            lock (_gate)
            {
                if (_playState != MpcPlayState.Play) return;
            }

            SendToPlayer(MpcApi.CMD_GETCURRENTPOSITION, "");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void OnPosition(string payload)
    {
        if (!MpcApi.TryParseSeconds(payload, out var at)) return;

        TaskCompletionSource<TimeSpan>? reply;
        lock (_gate)
        {
            reply = _positionReply;
            _positionReply = null;
        }

        reply?.TrySetResult(at);
        Raise(new PlaybackEvent.Position(at));
    }

    private void OnProcessExited()
    {
        PlaybackEvent exit;
        lock (_gate)
        {
            if (_disposed) return;
            exit = _playerWindow == 0 && _sinceStart.Elapsed < EarlyExitWindow
                ? new PlaybackEvent.Failed(EarlyExitMessage)
                : new PlaybackEvent.Closed();
        }

        StopPolling();
        Raise(exit);
    }

    private void StopPolling()
    {
        lock (_gate) _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Sends outside the lock: MPC-HC may answer with a message of its own before Send returns.</summary>
    private void SendToPlayer(uint cmd, string payload)
    {
        IMpcTransport? transport;
        nint target;
        lock (_gate)
        {
            if (_finished || _disposed) return;
            (transport, target) = (_transport, _playerWindow);
        }

        if (transport is null || target == 0) return;
        transport.Send(target, cmd, payload);
    }

    private void Raise(PlaybackEvent e)
    {
        lock (_raiseGate)
        {
            if (_finished) return;
            if (e is PlaybackEvent.Closed or PlaybackEvent.Failed) _finished = true;
            Event?.Invoke(e);
        }
    }
}
