using CozyCouch.Core.Players;

namespace CozyCouch.Core.Tests.Players;

/// <summary>The delivery rules every player session relies on, tested once here instead of once per backend.</summary>
public class EventPumpTests
{
    private readonly List<PlaybackEvent> _seen = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private List<PlaybackEvent> Seen
    {
        get { lock (_seen) return [.. _seen]; }
    }

    /// <summary>A pump whose handler logs every event, then runs <paramref name="then"/>.</summary>
    private EventPump Create(Action<PlaybackEvent>? then = null) => new("test", () => e =>
    {
        lock (_seen) _seen.Add(e);
        then?.Invoke(e);
    });

    [Fact]
    public void Raise_DeliversEventsInOrder()
    {
        var pump = Create();

        pump.Raise(new PlaybackEvent.Playing());
        pump.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(5)));
        pump.Raise(new PlaybackEvent.Paused());

        Assert.Equal([new PlaybackEvent.Playing(), new PlaybackEvent.Position(TimeSpan.FromSeconds(5)), new PlaybackEvent.Paused()], Seen);
        Assert.False(pump.IsFinished);
    }

    [Theory]
    [InlineData(false)] // Closed
    [InlineData(true)] // Failed
    public void FinalEvent_IsLast(bool failed)
    {
        var pump = Create();
        PlaybackEvent final = failed ? new PlaybackEvent.Failed("boom") : new PlaybackEvent.Closed();

        pump.Raise(final);
        pump.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(9)));
        pump.Raise(new PlaybackEvent.Closed());

        Assert.Equal([final], Seen);
        Assert.True(pump.IsFinished);
    }

    [Fact]
    public void Handler_Throwing_DoesNotStopLaterEvents()
    {
        var pump = Create(e =>
        {
            if (e is PlaybackEvent.Playing) throw new InvalidOperationException("handler bug");
        });

        pump.Raise(new PlaybackEvent.Playing());
        pump.Raise(new PlaybackEvent.Closed());

        Assert.Equal([new PlaybackEvent.Playing(), new PlaybackEvent.Closed()], Seen);
    }

    [Fact]
    public void Handler_ThrowingOnTheFinalEvent_DoesNotEscapeRaise()
    {
        var pump = Create(_ => throw new InvalidOperationException("handler bug"));

        pump.Raise(new PlaybackEvent.Failed("boom"));

        Assert.Equal([new PlaybackEvent.Failed("boom")], Seen);
        Assert.True(pump.IsFinished);
    }

    [Fact]
    public void Handler_RaisingAgain_DoesNotRecurse_AndKeepsOrder()
    {
        var depth = 0;
        var deepest = 0;
        EventPump pump = null!;
        pump = Create(e =>
        {
            deepest = Math.Max(deepest, ++depth);
            if (e is PlaybackEvent.Playing) pump.Raise(new PlaybackEvent.Paused());
            depth--;
        });

        pump.Raise(new PlaybackEvent.Playing());

        Assert.Equal([new PlaybackEvent.Playing(), new PlaybackEvent.Paused()], Seen);
        Assert.Equal(1, deepest);
    }

    [Fact]
    public void Stop_DropsLaterEvents_AndFinishesThePump()
    {
        var pump = Create();
        pump.Raise(new PlaybackEvent.Playing());

        pump.Stop();
        pump.Raise(new PlaybackEvent.Closed());

        Assert.Equal([new PlaybackEvent.Playing()], Seen);
        Assert.True(pump.IsFinished);
    }

    [Fact]
    public async Task Handler_BlockingUntilStop_DoesNotDeadlock()
    {
        using var handlerEntered = new ManualResetEventSlim();
        using var stopCalled = new ManualResetEventSlim();
        var handlerSawStop = false;
        var pump = Create(e =>
        {
            if (e is not PlaybackEvent.Playing) return;
            handlerEntered.Set();
            // Like a synchronous Dispatcher.Invoke onto a UI thread that is busy in DisposeAsync.
            handlerSawStop = stopCalled.Wait(TimeSpan.FromSeconds(2));
        });

        var raise = Task.Run(() => pump.Raise(new PlaybackEvent.Playing()), Ct);
        Assert.True(handlerEntered.Wait(Waiting.Timeout, Ct));
        var stop = Task.Run(pump.Stop, Ct);
        await stop.WaitAsync(Waiting.Timeout, Ct);
        stopCalled.Set();

        await raise.WaitAsync(Waiting.Timeout, Ct);
        Assert.True(handlerSawStop, "Stop blocked while a handler was running");
    }

    [Fact]
    public async Task ConcurrentRaises_AreDeliveredOneAtATime_AndAllArrive()
    {
        var active = 0;
        var overlapped = false;
        var pump = Create(_ =>
        {
            if (Interlocked.Increment(ref active) > 1) overlapped = true;
            Thread.SpinWait(200);
            Interlocked.Decrement(ref active);
        });

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++) pump.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(i)));
        }, Ct)));

        await Waiting.Until(() => Seen.Count == 800);
        Assert.False(overlapped);
    }
}
