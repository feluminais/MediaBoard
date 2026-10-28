using System.Diagnostics;

namespace MediaBoard.Core.Players;

/// <summary>
/// Opens a file in any program (the file path is its one argument) and reports only when that program closes.
/// There is no progress: <see cref="TracksProgress"/> is false, and the session raises only
/// <see cref="PlaybackEvent.Closed"/> or <see cref="PlaybackEvent.Failed"/>.
/// </summary>
/// <remarks>
/// A program that cannot be started, or that exits with an error code within a few seconds, is a failed launch
/// ("&lt;name&gt; closed right after starting"). Any other exit is a close, whatever the code: many programs return
/// non-zero after a normal session, and some hand the file to a running instance and exit at once with zero.
/// <see cref="Event"/> is raised on a thread-pool thread, with no lock held; handlers must return quickly and
/// marshal to the UI thread with <c>InvokeAsync</c>/<c>Post</c>. A handler that throws is traced.
/// </remarks>
public sealed class LaunchOnlySession : IPlayerSession
{
    private readonly PlayerInfo _player;
    private readonly Func<ProcessStartInfo, IPlayerProcess> _start;
    private readonly TimeProvider _time;
    private readonly TimeSpan _closeTimeout;

    private readonly Lock _gate = new();
    private readonly EventPump _pump;

    private IPlayerProcess? _process;
    private long _startStamp;
    private bool _started;
    private bool _disposed;

    /// <param name="player">The program to run; <see cref="PlayerInfo.ExePath"/> is launched and <see cref="PlayerInfo.Name"/> names it in messages.</param>
    /// <param name="start">Starts the program's process.</param>
    /// <param name="time">The clock for judging an early exit.</param>
    /// <param name="closeTimeout">How long disposal waits for the program to close before killing it (default 3 s).</param>
    public LaunchOnlySession(
        PlayerInfo player,
        Func<ProcessStartInfo, IPlayerProcess> start,
        TimeProvider? time = null,
        TimeSpan? closeTimeout = null)
    {
        _player = player;
        _start = start;
        _time = time ?? TimeProvider.System;
        _closeTimeout = closeTimeout ?? PlayerLaunch.DefaultCloseTimeout;
        _pump = new EventPump(player.Name, () => Event);
    }

    /// <summary>A session wired to a real process.</summary>
    public static LaunchOnlySession Create(PlayerInfo player) => new(player, PlayerProcess.Start);

    public bool TracksProgress => false;

    public event Action<PlaybackEvent>? Event;

    /// <summary>Launches the program with the file. <paramref name="startAt"/> is ignored: the program is not asked to seek.</summary>
    public Task StartAsync(string filePath, TimeSpan? startAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("A session plays one file; create a new session.");
            _started = true;
        }

        var startInfo = new ProcessStartInfo(_player.ExePath) { UseShellExecute = false };
        startInfo.ArgumentList.Add(filePath);

        _startStamp = _time.GetTimestamp();
        var process = PlayerLaunch.TryStart(_start, startInfo, _player.Name, _player.ExePath, out var failure);
        if (process is null)
        {
            _pump.Raise(new PlaybackEvent.Failed(failure));
            return Task.CompletedTask;
        }

        lock (_gate) _process = process;
        _ = ObserveExitAsync(process);
        return Task.CompletedTask;
    }

    public Task<TimeSpan?> GetPositionAsync(CancellationToken ct) => Task.FromResult<TimeSpan?>(null);

    /// <summary>Closes the program if this session started it and it is still running (asks first, kills after the close timeout). Raises no events.</summary>
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
        if (process is not null) await PlayerLaunch.CloseAsync(process, _closeTimeout);
    }

    private async Task ObserveExitAsync(IPlayerProcess process)
    {
        await process.Exited.ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed) return;
        }

        var exitCode = PlayerLaunch.ExitCodeOrZero(process);
        var quickFailure = exitCode != 0 && _time.GetElapsedTime(_startStamp) < PlayerLaunch.EarlyExitWindow;
        _pump.Raise(quickFailure ? new PlaybackEvent.Failed($"{_player.Name} closed right after starting") : new PlaybackEvent.Closed());
    }
}
