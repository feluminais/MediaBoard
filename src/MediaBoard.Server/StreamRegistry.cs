using MediaBoard.Core.Library;
using MediaBoard.Core.Remote;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaBoard.Server;

/// <summary>One row of Watching now.</summary>
/// <param name="StartedAt">In UTC.</param>
public sealed record StreamSession(
    Guid Id, int UserId, string UserName, int DeviceId, string DeviceName,
    int FileId, string Title, double PositionS, double DurationS, double MbitPerSecond, DateTime StartedAt);

/// <summary>A stream that may not start. <see cref="Code"/> is what the client is told.</summary>
public sealed class StreamRefusedException(ApiErrorCode code) : Exception($"The stream was refused: {code}.")
{
    public ApiErrorCode Code { get; } = code;
}

/// <summary>One request's place in a session. Disposing it ends the request's hold, not the session.</summary>
public sealed class StreamLease(Guid session, CancellationTokenSource stop) : IDisposable
{
    public Guid Session { get; } = session;

    public void Dispose() => stop.Dispose();
}

/// <summary>
/// The live playbacks. A player opens overlapping requests and reconnects on every seek, so a session is one device
/// playing one file, whatever number of requests that takes. It ends after <see cref="Quiet"/> with no bytes and no
/// heartbeat, and a request still open ends with it: a stalled client could hold its socket forever, keeping the PC
/// awake for nobody.
/// </summary>
/// <param name="time">The clock.</param>
/// <param name="sleep">Held while any session exists.</param>
/// <param name="log">Where failed checks and subscribers go, or null for nowhere.</param>
public sealed partial class StreamRegistry(TimeProvider time, ISleepBlocker sleep, ILogger? log = null)
{
    private const int MaxPerAccount = 8;

    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BlockFor = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(5);

    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Live> _live = [];
    private readonly Dictionary<(int DeviceId, int FileId), long> _blockedSince = [];
    private ITimer? _expiry;

    /// <summary>Raised off the lock when a row appears, goes or gets a new position. Rates move without it.</summary>
    public event Action? Changed;

    public IReadOnlyList<StreamSession> Current
    {
        get
        {
            lock (_gate)
            {
                var now = time.GetTimestamp();
                return [.. _live.Values.Select(live => live.ToSession(time, now)).OrderBy(session => session.StartedAt)];
            }
        }
    }

    /// <summary>Joins this device's session on this file, or starts one.</summary>
    /// <param name="who">Who asks.</param>
    /// <param name="file">What they asked for, already checked with <see cref="ViewerAccess"/>.</param>
    /// <param name="requestAborted">The request's own token.</param>
    /// <param name="stop">Fires when the session ends or this request is aborted. Live until the lease is disposed.</param>
    /// <exception cref="StreamRefusedException">The owner stopped this stream a moment ago, or the account is at its limit.</exception>
    public StreamLease Open(RequestViewer who, ReachableFile file, CancellationToken requestAborted, out CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(file);

        StreamLease lease;
        bool started;
        lock (_gate)
        {
            var now = time.GetTimestamp();
            if (IsBlocked(who.Device.Id, file.FileId)) throw new StreamRefusedException(ApiErrorCode.StreamStopped);

            var live = Find(who.Device.Id, file.FileId);
            started = live is null;
            if (live is null)
            {
                if (_live.Values.Count(other => other.UserId == who.Account.Id) >= MaxPerAccount)
                    throw new StreamRefusedException(ApiErrorCode.TooManyStreams);

                live = new Live(who, file, time.GetUtcNow().UtcDateTime, now);
                Add(live);
            }

            live.LastActivity = now;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(live.Stop.Token, requestAborted);
            stop = linked.Token;
            lease = new StreamLease(live.Id, linked);
        }

        if (started) RaiseChanged();
        return lease;
    }

    public void AddBytes(Guid session, long count)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(session, out var live)) return;

            var now = time.GetTimestamp();
            live.LastActivity = now;
            live.Sent.Enqueue((now, count));
            live.SentBytes += count;
            live.ForgetOldBytes(time, now);
        }
    }

    /// <summary>A player's report. It also keeps a paused playback's session, which sends no bytes.</summary>
    /// <param name="deviceId">The device playing.</param>
    /// <param name="fileId">The file it plays.</param>
    /// <param name="positionS">Where the player is.</param>
    /// <param name="durationS">The file's length; 0 when the player doesn't know it.</param>
    public void Heartbeat(int deviceId, int fileId, double positionS, double durationS)
    {
        lock (_gate)
        {
            if (Find(deviceId, fileId) is not { } live) return;

            live.LastActivity = time.GetTimestamp();
            live.PositionS = positionS;
            if (durationS > 0) live.DurationS = durationS;
        }

        RaiseChanged();
    }

    /// <summary>The owner's stop: ends the session and keeps that device off that file for a minute.</summary>
    public void Stop(Guid session)
    {
        Live? stopped;
        lock (_gate)
        {
            if (!_live.TryGetValue(session, out stopped)) return;

            Remove(stopped);
            ForgetOldBlocks();
            _blockedSince[(stopped.DeviceId, stopped.FileId)] = time.GetTimestamp();
        }

        End([stopped]);
    }

    /// <summary>Ends a session with no block on opening it again.</summary>
    public void Close(Guid session) => Close(live => live.Id == session);

    public void CloseAccount(int userId) => Close(live => live.UserId == userId);

    public void CloseDevice(int deviceId) => Close(live => live.DeviceId == deviceId);

    public void CloseAll() => Close(_ => true);

    /// <summary>
    /// Ends every session the viewer can no longer reach, and any it can't check: a stream nobody can vouch for doesn't
    /// play on. One failed check leaves the others to run. Blocks nothing, so access given back works at once.
    /// </summary>
    /// <param name="reachable">Takes a user id and a file id. It can be slow, so it runs outside the lock.</param>
    public void CloseUnreachable(Func<int, int, bool> reachable)
    {
        ArgumentNullException.ThrowIfNull(reachable);

        List<Live> open;
        lock (_gate) open = [.. _live.Values];

        var gone = open.Where(live => !StillReachable(live, reachable)).ToHashSet();
        if (gone.Count > 0) Close(gone.Contains);
    }

    private bool StillReachable(Live live, Func<int, int, bool> reachable)
    {
        try
        {
            return reachable(live.UserId, live.FileId);
        }
        catch (Exception e)
        {
            // The type alone: a message can hold a path, and no path goes in a log.
            LogCheckFailed(_log, e.GetType().Name);
            return false;
        }
    }

    // A subscriber's failure must not take the rest with it. Off the timer, it would take the whole process.
    private void RaiseChanged()
    {
        if (Changed is not { } handlers) return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception e)
            {
                LogListenerFailed(_log, e.GetType().Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A stream ended because its access check failed: {failure}")]
    private static partial void LogCheckFailed(ILogger logger, string failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "A listener to stream changes failed: {failure}")]
    private static partial void LogListenerFailed(ILogger logger, string failure);

    private void Close(Func<Live, bool> which)
    {
        List<Live> closed;
        lock (_gate)
        {
            closed = [.. _live.Values.Where(which)];
            closed.ForEach(Remove);
        }

        End(closed);
    }

    // Off the lock: cancelling runs each request's callbacks right here.
    private void End(List<Live> ended)
    {
        if (ended.Count == 0) return;

        foreach (var live in ended)
        {
            live.Stop.Cancel();
            live.Stop.Dispose();
        }

        RaiseChanged();
    }

    private void EndQuietOnes()
    {
        List<Live> quiet;
        lock (_gate)
        {
            var now = time.GetTimestamp();
            quiet = [.. _live.Values.Where(live => time.GetElapsedTime(live.LastActivity, now) >= Quiet)];
            quiet.ForEach(Remove);

            if (_live.Count > 0)
            {
                // Activity doesn't rearm the timer, so the timer often finds nothing and only moves on to the next due time.
                var longestQuiet = _live.Values.Max(live => time.GetElapsedTime(live.LastActivity, now));
                _expiry?.Change(Quiet - longestQuiet, Timeout.InfiniteTimeSpan);
            }
        }

        End(quiet);
    }

    private void Add(Live live)
    {
        if (_live.Count == 0)
        {
            sleep.Hold();
            _expiry = time.CreateTimer(_ => EndQuietOnes(), null, Quiet, Timeout.InfiniteTimeSpan);
        }

        _live.Add(live.Id, live);
    }

    private void Remove(Live live)
    {
        _live.Remove(live.Id);
        if (_live.Count > 0) return;

        sleep.Release();
        _expiry?.Dispose();
        _expiry = null;
    }

    private Live? Find(int deviceId, int fileId) =>
        _live.Values.FirstOrDefault(live => live.DeviceId == deviceId && live.FileId == fileId);

    private bool IsBlocked(int deviceId, int fileId)
    {
        ForgetOldBlocks();
        return _blockedSince.ContainsKey((deviceId, fileId));
    }

    private void ForgetOldBlocks()
    {
        foreach (var (key, since) in _blockedSince)
        {
            if (time.GetElapsedTime(since) >= BlockFor) _blockedSince.Remove(key);
        }
    }

    /// <summary>A session's state. <see cref="_gate"/> guards every field that changes.</summary>
    private sealed class Live(RequestViewer who, ReachableFile file, DateTime startedAt, long started)
    {
        public Guid Id { get; } = Guid.NewGuid();

        public int UserId { get; } = who.Account.Id;

        public int DeviceId { get; } = who.Device.Id;

        public int FileId { get; } = file.FileId;

        public CancellationTokenSource Stop { get; } = new();

        public long LastActivity { get; set; }

        public double PositionS { get; set; }

        public double DurationS { get; set; } = file.DurationS ?? 0;

        public Queue<(long At, long Count)> Sent { get; } = new();

        public long SentBytes { get; set; }

        public void ForgetOldBytes(TimeProvider time, long now)
        {
            while (Sent.TryPeek(out var oldest) && time.GetElapsedTime(oldest.At, now) > RateWindow)
            {
                Sent.Dequeue();
                SentBytes -= oldest.Count;
            }
        }

        public StreamSession ToSession(TimeProvider time, long now)
        {
            ForgetOldBytes(time, now);

            // A session a moment old has sent for less than the window, and dividing by the whole window would undersell it.
            var seconds = Math.Clamp(time.GetElapsedTime(started, now).TotalSeconds, 1, RateWindow.TotalSeconds);
            var title = file.EpisodeLabel is { } label ? $"{file.TitleName} {label}" : file.TitleName;
            return new StreamSession(
                Id, UserId, who.Account.Name, DeviceId, who.Device.Name, FileId, title,
                PositionS, DurationS, SentBytes * 8 / 1_000_000.0 / seconds, startedAt);
        }
    }
}
