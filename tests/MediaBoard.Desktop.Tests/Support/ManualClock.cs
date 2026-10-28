namespace MediaBoard.Desktop.Tests.Support;

/// <summary>
/// A <see cref="TimeProvider"/> that only moves when a test calls <see cref="Advance"/>: a timer fires, on the
/// calling thread, once the clock reaches its due time. Nothing waits for real time.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Timers that are armed and not yet due.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_timers) return _timers.Count(t => t.Due is not null);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_timers)
        {
            _ticks += by.Ticks;
            due = _timers.Where(t => t.Due <= _ticks).ToList();
        }

        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._ticks + dueTime.Ticks;
            return true;
        }

        public void Fire()
        {
            Due = null; // one-shot: the palette only delays
            callback(state);
        }

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
