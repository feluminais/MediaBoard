using Dapper;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Accounts;

public sealed class InviteStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly FakeClock _clock = new();
    private readonly InviteStore _invites;
    private readonly int _anna;

    public InviteStoreTests()
    {
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _invites = new InviteStore(_db, _clock);
        _anna = new AccountStore(_db, _clock).Create("Anna", null).Id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Create_Gives128BitCodes_ThatDiffer()
    {
        var first = _invites.Create(_anna);
        var second = _invites.Create(_anna);

        Assert.NotEqual(first, second);
        Assert.Equal(16, Convert.FromBase64String(first.Replace('-', '+').Replace('_', '/') + "==").Length);
    }

    [Fact]
    public void Code_IsStoredOnlyAsHash()
    {
        var code = _invites.Create(_anna);

        using var connection = _db.Open();
        var stored = connection.QuerySingle<string>("SELECT code_hash FROM invites");

        Assert.NotEqual(code, stored);
        Assert.Equal(64, stored.Length);
    }

    [Fact]
    public void Peek_DoesNotUseTheInvite()
    {
        var code = _invites.Create(_anna);

        Assert.Equal(_anna, _invites.Peek(code));
        Assert.Equal(_anna, _invites.Peek(code));
        Assert.Equal(_anna, _invites.Consume(code));
    }

    [Fact]
    public void UnknownCode_IsDead()
    {
        _invites.Create(_anna);

        Assert.Null(_invites.Peek("no-such-code"));
        Assert.Null(_invites.Consume("no-such-code"));
    }

    [Fact]
    public void Invite_ExpiresAfter7Days()
    {
        var code = _invites.Create(_anna);

        _clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));
        Assert.Equal(_anna, _invites.Peek(code));
        Assert.Single(_invites.Pending(_anna));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(_invites.Peek(code));
        Assert.Null(_invites.Consume(code));
        Assert.Empty(_invites.Pending(_anna));
    }

    [Fact]
    public void Invite_IsSingleUse()
    {
        var code = _invites.Create(_anna);

        Assert.Equal(_anna, _invites.Consume(code));

        Assert.Null(_invites.Consume(code));
        Assert.Null(_invites.Peek(code));
    }

    [Fact]
    public async Task Invite_IsSingleUse_WhenConsumedConcurrently()
    {
        for (var round = 0; round < 20; round++)
        {
            var code = _invites.Create(_anna);
            using var start = new Barrier(2);

            var results = await Task.WhenAll(
                Task.Run(() => ConsumeAt(start, code)),
                Task.Run(() => ConsumeAt(start, code)));

            Assert.Equal(1, results.Count(id => id == _anna));
            Assert.Equal(1, results.Count(id => id is null));
        }
    }

    private int? ConsumeAt(Barrier start, string code)
    {
        start.SignalAndWait();
        return _invites.Consume(code);
    }

    [Fact]
    public void Invite_Cancelled_IsDead()
    {
        var code = _invites.Create(_anna);
        var invite = _invites.Pending(_anna).Single();

        _invites.Cancel(invite.Id);

        Assert.Null(_invites.Peek(code));
        Assert.Null(_invites.Consume(code));
        Assert.Empty(_invites.Pending(_anna));
    }

    [Fact]
    public void Pending_ListsLiveInvitesOfThatPerson()
    {
        var bert = new AccountStore(_db, _clock).Create("Bert", null).Id;
        var used = _invites.Create(_anna);
        _invites.Create(_anna);
        _invites.Create(bert);
        _invites.Consume(used);

        var invite = Assert.Single(_invites.Pending(_anna));

        Assert.Equal(_anna, invite.UserId);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, invite.CreatedAt);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddDays(7), invite.ExpiresAt);
    }
}
