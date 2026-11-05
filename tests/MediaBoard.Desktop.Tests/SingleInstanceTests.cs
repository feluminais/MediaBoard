namespace MediaBoard.Desktop.Tests;

/// <summary>Two claims in one process behave as two processes do: the named event doesn't care who holds it.</summary>
public sealed class SingleInstanceTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-instance-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void First_Runs()
    {
        using var first = SingleInstance.For(_dir);

        Assert.True(first.ShouldRun([]));
    }

    [Fact]
    public async Task Second_Exits_AndAsksTheFirstToShowItsWindow()
    {
        using var first = SingleInstance.For(_dir);
        using var asked = new SemaphoreSlim(0);
        first.Listen(() => asked.Release());
        using var second = SingleInstance.For(_dir);

        Assert.False(second.ShouldRun([]));

        Assert.True(await asked.WaitAsync(Soon, Ct));
    }

    [Fact]
    public async Task Second_WithTray_ExitsWithoutAsking()
    {
        using var first = SingleInstance.For(_dir);
        using var asked = new SemaphoreSlim(0);
        first.Listen(() => asked.Release());
        using var second = SingleInstance.For(_dir);

        Assert.False(second.ShouldRun(["--tray"]));

        Assert.False(await asked.WaitAsync(TimeSpan.FromMilliseconds(300), Ct));
    }

    [Fact]
    public async Task AskedBeforeListening_IsHeardOnceListening()
    {
        using var first = SingleInstance.For(_dir);
        using var second = SingleInstance.For(_dir);
        Assert.False(second.ShouldRun([]));
        using var asked = new SemaphoreSlim(0);

        first.Listen(() => asked.Release());

        Assert.True(await asked.WaitAsync(Soon, Ct));
    }

    [Fact]
    public async Task EveryAsk_IsHeard()
    {
        using var first = SingleInstance.For(_dir);
        using var asked = new SemaphoreSlim(0);
        first.Listen(() => asked.Release());

        for (var ask = 0; ask < 3; ask++)
        {
            using var again = SingleInstance.For(_dir);
            Assert.False(again.ShouldRun([]));
            Assert.True(await asked.WaitAsync(Soon, Ct));
        }
    }

    [Fact]
    public void AfterTheFirstEnds_TheNextIsFirst()
    {
        SingleInstance.For(_dir).Dispose();

        using var next = SingleInstance.For(_dir);

        Assert.True(next.ShouldRun([]));
    }

    [Fact]
    public void Claim_PlainStart_DoesNotWait()
    {
        using var first = SingleInstance.For(_dir);

        using var second = SingleInstance.Claim(_dir, [], TimeSpan.FromMinutes(5));

        Assert.False(second.ShouldRun(["--tray"]));
    }

    // Without the wait the app simply vanishes on a restart: the new process would find the old one still stopping.
    [Fact]
    public async Task Claim_Restart_WaitsForTheProcessThatAskedForIt()
    {
        var first = SingleInstance.For(_dir);
        var claim = Task.Run(() => SingleInstance.Claim(_dir, [SingleInstance.RestartArgument], TimeSpan.FromSeconds(30)), Ct);
        await Task.Delay(200, Ct);
        Assert.False(claim.IsCompleted);

        first.Dispose();

        using var second = await claim.WaitAsync(Soon, Ct);
        Assert.True(second.ShouldRun([SingleInstance.RestartArgument]));
    }

    [Fact]
    public async Task Claim_Restart_OldProcessStaysPastTheLimit_HandsOver()
    {
        using var first = SingleInstance.For(_dir);
        using var asked = new SemaphoreSlim(0);
        first.Listen(() => asked.Release());

        using var second = SingleInstance.Claim(_dir, [SingleInstance.RestartArgument], TimeSpan.FromMilliseconds(100));

        Assert.False(second.ShouldRun([SingleInstance.RestartArgument]));
        Assert.True(await asked.WaitAsync(Soon, Ct));
    }

    [Fact]
    public void OtherDataDirectory_RunsBesideIt()
    {
        using var first = SingleInstance.For(_dir);
        using var other = SingleInstance.For(_dir + "-other");

        Assert.True(other.ShouldRun([]));
    }

    [Fact]
    public void SameDirectory_SpelledDifferently_IsTheSameClaim()
    {
        using var first = SingleInstance.For(_dir);
        using var spelled = SingleInstance.For(_dir.ToUpperInvariant() + Path.DirectorySeparatorChar);

        Assert.False(spelled.ShouldRun(["--tray"]));
    }
}
