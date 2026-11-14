using Avalonia.Headless.XUnit;
using CozyCouch.Core.Activity;
using CozyCouch.Desktop.Tests.Support;
using CozyCouch.Desktop.ViewModels;

namespace CozyCouch.Desktop.Tests;

/// <summary>What an awaited <see cref="BackgroundQuery.RunAsync{T}"/> promises when a newer one replaces it.</summary>
public class BackgroundQueryTests
{
    private readonly ActivityHub _hub = new();

    [AvaloniaFact]
    public async Task SupersededRun_CompletesOnlyAfterTheNewerRunApplied()
    {
        var query = new BackgroundQuery(_hub, "the folders");
        var applied = new List<int>();
        using var release = new ManualResetEventSlim();

        var older = query.RunAsync(() => 1, applied.Add);
        var newer = query.RunAsync(() =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return 2;
        }, applied.Add);
        await Task.Delay(200); // the older query is back, and stale
        Ui.Flush();

        try
        {
            // Awaiting the older run means "the page shows current data", so it can't finish while the newer one runs.
            Assert.False(older.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await older.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([2], applied);
        Assert.True(newer.IsCompleted);
    }

    [AvaloniaFact]
    public async Task SupersededRun_WhoseQueryFails_StillWaitsForTheNewerRun()
    {
        var query = new BackgroundQuery(_hub, "the folders");
        var applied = new List<int>();
        using var release = new ManualResetEventSlim();

        var older = query.RunAsync<int>(() => throw new IOException("drive asleep"), applied.Add);
        _ = query.RunAsync(() =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return 2;
        }, applied.Add);
        await Task.Delay(200);
        Ui.Flush();

        try
        {
            Assert.False(older.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await older.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([2], applied);
        Assert.DoesNotContain(_hub.Recent, e => e.Message.Contains("drive asleep")); // a stale failure is not news
    }

    [AvaloniaFact]
    public async Task DiscardedRun_CompletesWithoutApplying()
    {
        var query = new BackgroundQuery(_hub, "search");
        var applied = new List<int>();

        var run = query.RunAsync(() => 1, applied.Add);
        query.Discard();

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(applied);
    }
}
