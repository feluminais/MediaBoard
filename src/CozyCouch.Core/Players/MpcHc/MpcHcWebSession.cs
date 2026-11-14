using System.Diagnostics;

namespace CozyCouch.Core.Players.MpcHc;

/// <summary>
/// Follows an MPC-HC (or MPC-BE) the viewer started without CozyCouch, through its web interface: the session polls
/// <c>variables.html</c> on 127.0.0.1 for the open file, the state and the position. MPC-HC accepts the app's own
/// connection (<c>/slave</c>) only at launch, which is why a player that is already running needs this instead.
/// The viewer turns the web interface on in the player's options; CozyCouch never changes the player's settings.
/// </summary>
/// <remarks>
/// <see cref="Event"/> is raised on a thread-pool thread, one at a time and in order, with no lock held. Handlers
/// must return quickly, must not block on this session, and must marshal to the UI thread with
/// <c>InvokeAsync</c>/<c>Post</c>. <see cref="PlaybackEvent.Closed"/> is final. Disposing the session stops the
/// polling and never closes the player. The web interface has no end-of-file message, so a file counts as ended when
/// MPC-HC goes to stopped after playing; one it closes at the end is seen as <see cref="PlaybackEvent.FileChanged"/>
/// to nothing, with the last position kept.
/// </remarks>
public sealed class MpcHcWebSession : IAttachableSession
{
    private const int MissesBeforeClosed = 2;

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private static readonly MpcWebClient SharedClient = new();

    private readonly string _name;
    private readonly int _port;
    private readonly MpcWebFetcher _fetch;
    private readonly TimeSpan _pollInterval;

    private readonly Lock _gate = new();
    private readonly EventPump _pump;
    private readonly CancellationTokenSource _stop = new();

    private bool _started;
    private bool _disposed;
    private string? _lastFile;

    // Touched only by the poll loop.
    private string? _currentFile;
    private bool _loadedRaised;
    private bool _sawPlaying;
    private bool _endedRaised;
    private MpcWebState? _lastState;
    private int _misses;

    /// <param name="name">"MPC-HC" or "MPC-BE", for the status bar.</param>
    /// <param name="port">The port of the player's web interface.</param>
    /// <param name="fetch">Asks the web interface for <c>variables.html</c>.</param>
    /// <param name="pollInterval">How often the page is asked for (default 2 s).</param>
    public MpcHcWebSession(string name, int port, MpcWebFetcher fetch, TimeSpan? pollInterval = null)
    {
        _name = name;
        _port = port;
        _fetch = fetch;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _pump = new EventPump(name, () => Event);
    }

    /// <summary>A session wired to the real web interface of a running MPC-HC or MPC-BE.</summary>
    public static MpcHcWebSession CreateAttached(string name, int port) => new(name, port, SharedClient.FetchAsync);

    public string PlayerName => _name;

    public bool TracksProgress => true;

    public event Action<PlaybackEvent>? Event;

    public async Task<bool> AttachAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("A session attaches once; create a new session.");
            _started = true;
        }

        // Anything else answering on the port is no MPC-HC of the viewer's.
        if (await TryFetchAsync(ct).ConfigureAwait(false) is null) return false;

        _ = PollAsync(_stop.Token);
        return true;
    }

    public Task StartAsync(string filePath, TimeSpan? startAt, CancellationToken ct) =>
        throw new NotSupportedException("An attached MPC-HC is followed, not started. Use AttachAsync.");

    /// <summary>Asks the web interface for the position right now; null when it does not answer or no file plays.</summary>
    public async Task<TimeSpan?> GetPositionAsync(CancellationToken ct)
    {
        string? file;
        lock (_gate)
        {
            if (_pump.IsFinished) return null;
            file = _lastFile;
        }

        var status = await TryFetchAsync(ct).ConfigureAwait(false);
        return status is { State: not (null or MpcWebState.Stopped) } playing && !string.IsNullOrEmpty(playing.FilePath) && PlayerPaths.Same(playing.FilePath, file)
            ? playing.Position
            : null;
    }

    public void Detach()
    {
        // Nothing to let go of: the player was never started by this session.
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _pump.Stop();
        await _stop.CancelAsync().ConfigureAwait(false);
    }

    private async Task PollAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (await TryFetchAsync(stop).ConfigureAwait(false) is { } status)
                {
                    _misses = 0;
                    Apply(status);
                }
                else if (++_misses >= MissesBeforeClosed)
                {
                    // Nobody watches a process here, so the page not answering any more is how its end shows.
                    _pump.Raise(new PlaybackEvent.Closed());
                    return;
                }

                await Task.Delay(_pollInterval, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (Exception e)
        {
            Trace.TraceError($"{_name} session: polling failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The status, or null when MPC-HC did not answer or the page was not its variables page.</summary>
    private async Task<MpcWebStatus?> TryFetchAsync(CancellationToken ct)
    {
        try
        {
            var html = await _fetch(_port, ct).ConfigureAwait(false);
            return MpcWebStatus.TryParse(html, out var status) ? status : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private void Apply(MpcWebStatus status)
    {
        var file = string.IsNullOrEmpty(status.FilePath) ? null : status.FilePath;
        if (!PlayerPaths.Same(file, _currentFile))
        {
            _currentFile = file;
            lock (_gate) _lastFile = file;
            (_loadedRaised, _sawPlaying, _endedRaised, _lastState) = (false, false, false, null);
            _pump.Raise(new PlaybackEvent.FileChanged(file));
        }

        if (file is null || status.State is not { } state) return;

        if (!_loadedRaised && state != MpcWebState.Stopped && status.Duration > TimeSpan.Zero)
        {
            _loadedRaised = true;
            _pump.Raise(new PlaybackEvent.Loaded(status.Duration));
        }

        var changed = _lastState != state;
        _lastState = state;
        switch (state)
        {
            case MpcWebState.Playing:
                (_sawPlaying, _endedRaised) = (true, false);
                if (changed) _pump.Raise(new PlaybackEvent.Playing());
                _pump.Raise(new PlaybackEvent.Position(status.Position));
                break;
            case MpcWebState.Paused:
                (_sawPlaying, _endedRaised) = (true, false);
                if (changed)
                {
                    _pump.Raise(new PlaybackEvent.Paused());
                    _pump.Raise(new PlaybackEvent.Position(status.Position));
                }

                break;
            case MpcWebState.Stopped:
                // Stopped is also what MPC-HC says before it has started the file; only a stop after playing is the end.
                if (_sawPlaying && !_endedRaised)
                {
                    _endedRaised = true;
                    _pump.Raise(new PlaybackEvent.Ended());
                }

                break;
        }
    }
}
