namespace MediaBoard.Core.Tests.Support;

/// <summary>
/// A <see cref="TimeProvider"/> where waiting costs nothing: a timer fires as soon as it is armed and moves
/// the clock forward by its due time. <see cref="Delays"/> records every wait so tests can assert on them.
/// </summary>
public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = [];

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long GetTimestamp() => _now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new InstantTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class InstantTimer(FakeClock clock, TimerCallback callback, object? state) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan) return true;

            clock.Delays.Add(dueTime);
            clock._now += dueTime;
            callback(state);
            return true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
