using CozyCouch.Core.Activity;
using CozyCouch.Core.Metadata;
using CozyCouch.Core.Tests.Support;

namespace CozyCouch.Core.Tests.Metadata;

public sealed class ConnectivityTests
{
    private readonly FakeClock _clock = new();
    private readonly ActivityHub _hub = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReportOffline_ProbesEveryMinute_UntilTheProbeSucceeds()
    {
        var answers = new Queue<bool>([false, false, true]);
        // The instant clock lets the retry loop run to the end on its own thread; hold its first probe until the
        // offline state has been checked.
        var checkedOffline = new TaskCompletionSource();
        using var connectivity = new Connectivity(
            async ct =>
            {
                await checkedOffline.Task.WaitAsync(ct);
                return answers.Dequeue();
            },
            _clock,
            _hub);
        var changes = new List<bool>();
        var restored = new TaskCompletionSource();
        connectivity.Changed += online =>
        {
            changes.Add(online);
            if (online) restored.TrySetResult();
        };

        connectivity.ReportOffline();
        Assert.False(connectivity.IsOnline);
        Assert.False(_hub.Current.Online);
        checkedOffline.SetResult();
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(connectivity.IsOnline);
        Assert.True(_hub.Current.Online);
        Assert.Equal([false, true], changes);
        Assert.Equal([TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)], _clock.Delays);
    }

    [Fact]
    public async Task ProbeThatThrows_CountsAsOffline()
    {
        var calls = 0;
        var restored = new TaskCompletionSource();
        using var connectivity = new Connectivity(
            _ => ++calls == 1 ? throw new HttpRequestException("no route") : Task.FromResult(true), _clock, _hub);
        connectivity.Changed += online =>
        {
            if (online) restored.TrySetResult();
        };

        connectivity.ReportOffline();
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(2, calls);
    }

    [Fact]
    public void ReportOffline_WhileAlreadyOffline_RaisesNoSecondEvent()
    {
        var gate = new TaskCompletionSource<bool>();
        using var connectivity = new Connectivity(ct => gate.Task.WaitAsync(ct), _clock, _hub);
        var changes = 0;
        connectivity.Changed += _ => changes++;

        connectivity.ReportOffline();
        connectivity.ReportOffline();

        Assert.Equal(1, changes);
    }
}
