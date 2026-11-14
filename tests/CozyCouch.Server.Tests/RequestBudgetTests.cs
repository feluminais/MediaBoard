using Microsoft.Extensions.Time.Testing;

namespace CozyCouch.Server.Tests;

public sealed class RequestBudgetTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void AnAccount_GetsItsShare_AndNoMore()
    {
        var budget = new RequestBudget(2, Minute, _time);

        Assert.True(budget.TryTake(1));
        Assert.True(budget.TryTake(1));
        Assert.False(budget.TryTake(1));
    }

    [Fact]
    public void OneAccountsUse_LeavesAnothersAlone()
    {
        var budget = new RequestBudget(1, Minute, _time);
        budget.TryTake(1);

        Assert.True(budget.TryTake(2));
    }

    [Fact]
    public void TheShare_ComesBack_WhenTheStretchEnds()
    {
        var budget = new RequestBudget(1, Minute, _time);
        budget.TryTake(1);
        Assert.False(budget.TryTake(1));

        _time.Advance(Minute);

        Assert.True(budget.TryTake(1));
    }
}
