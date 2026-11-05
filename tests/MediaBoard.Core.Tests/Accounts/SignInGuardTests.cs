using MediaBoard.Core.Accounts;
using MediaBoard.Core.Tests.Support;

namespace MediaBoard.Core.Tests.Accounts;

public sealed class SignInGuardTests
{
    private readonly FakeClock _clock = new();
    private readonly SignInGuard _guard;

    public SignInGuardTests() => _guard = new SignInGuard(_clock);

    private void Fail(string name, string address, int times)
    {
        for (var i = 0; i < times; i++) _guard.RecordFailure(name, address);
    }

    [Fact]
    public void Guard_LocksNameAfter5_For15Minutes()
    {
        Fail("anna", "10.0.0.1", 4);
        Assert.False(_guard.IsLocked("anna", "10.0.0.2"));

        _guard.RecordFailure("anna", "10.0.0.1");
        Assert.True(_guard.IsLocked("ANNA", "10.0.0.2"));
        Assert.Equal(["anna"], _guard.LockedNames());
        Assert.False(_guard.IsLocked("bob", "10.0.0.2"));

        _clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True(_guard.IsLocked("anna", "10.0.0.2"));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(_guard.IsLocked("anna", "10.0.0.2"));
        Assert.Empty(_guard.LockedNames());
    }

    [Fact]
    public void Guard_LocksAddressAfter20_ForAnHour()
    {
        for (var i = 0; i < 19; i++) _guard.RecordFailure("name" + i, "10.0.0.1");
        Assert.False(_guard.IsLocked("fresh", "10.0.0.1"));

        _guard.RecordFailure("name19", "10.0.0.1");
        Assert.True(_guard.IsLocked("fresh", "10.0.0.1"));
        Assert.False(_guard.IsLocked("fresh", "10.0.0.2"));

        _clock.Advance(TimeSpan.FromMinutes(59));
        Assert.True(_guard.IsLocked("fresh", "10.0.0.1"));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(_guard.IsLocked("fresh", "10.0.0.1"));
    }

    [Fact]
    public void Guard_SuccessClearsName_NotAddress()
    {
        Fail("anna", "10.0.0.1", 4);
        _guard.RecordSuccess("anna");
        _guard.RecordFailure("anna", "10.0.0.2");
        Assert.False(_guard.IsLocked("anna", "10.0.0.2"));

        // The address kept its 4 plus the 5th below; 16 more reach 20 without any name locking.
        for (var i = 0; i < 15; i++) _guard.RecordFailure("n" + i, "10.0.0.1");
        Assert.False(_guard.IsLocked("other", "10.0.0.1"));
        _guard.RecordFailure("n99", "10.0.0.1");
        Assert.True(_guard.IsLocked("other", "10.0.0.1"));
    }

    [Fact]
    public void Success_DoesNotLiftActiveLock()
    {
        Fail("anna", "10.0.0.1", 5);

        _guard.RecordSuccess("anna");

        Assert.True(_guard.IsLocked("anna", "10.0.0.9"));
        Assert.Equal(["anna"], _guard.LockedNames());
    }

    [Fact]
    public void Guard_CapsEntries_KeepsLockedOnes()
    {
        Fail("anna", "10.0.0.1", 5);

        for (var i = 0; i < SignInGuard.MaxEntries * 2; i++) _guard.RecordFailure("name" + i, "addr" + i);

        Assert.True(_guard.IsLocked("anna", "fresh"));
        Assert.Equal(["anna"], _guard.LockedNames());
        // Room was made for newcomers, so a new name can still be counted and locked.
        Fail("late", "addr-late", 5);
        Assert.True(_guard.IsLocked("late", "fresh"));
    }

    [Fact]
    public void Guard_ForgetsOldFailures()
    {
        Fail("anna", "10.0.0.1", 4);
        _clock.Advance(TimeSpan.FromMinutes(16));

        _guard.RecordFailure("anna", "10.0.0.1");

        Assert.False(_guard.IsLocked("anna", "10.0.0.1"));
    }
}
