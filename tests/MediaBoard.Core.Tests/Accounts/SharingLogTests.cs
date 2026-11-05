using Dapper;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Accounts;

public sealed class SharingLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly FakeClock _clock = new();
    private readonly SharingLog _log;

    public SharingLogTests()
    {
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _log = new SharingLog(_db, _clock);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private int Rows()
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sharing_log");
    }

    [Fact]
    public void Recent_ReturnsNewestFirst_WithTimeKindAndDetail()
    {
        _log.Add(SharingEvent.Joined, LibraryDatabase.OwnerId, "first");
        _clock.Advance(TimeSpan.FromMinutes(1));
        _log.Add(SharingEvent.SignInLocked, null, "second");

        var entries = _log.Recent(10);

        Assert.Equal(["second", "first"], entries.Select(e => e.Detail));
        Assert.Equal(SharingEvent.SignInLocked, entries[0].Kind);
        Assert.Null(entries[0].UserId);
        Assert.Equal(LibraryDatabase.OwnerId, entries[1].UserId);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, entries[0].At);
        Assert.Single(_log.Recent(1));
    }

    [Fact]
    public void AddOncePerDay_KeepsOneEntryForAnAccountAndDetail_UntilADayHasPassed()
    {
        _log.AddOncePerDay(SharingEvent.FinishedWatching, LibraryDatabase.OwnerId, "Heat");
        _log.AddOncePerDay(SharingEvent.FinishedWatching, LibraryDatabase.OwnerId, "Heat");
        _log.AddOncePerDay(SharingEvent.FinishedWatching, LibraryDatabase.OwnerId, "Ronin");
        Assert.Equal(2, Rows());

        _clock.Advance(TimeSpan.FromHours(24));
        _log.AddOncePerDay(SharingEvent.FinishedWatching, LibraryDatabase.OwnerId, "Heat");

        Assert.Equal(3, Rows());
    }

    [Fact]
    public void Recent_SkipsAnUnknownKind()
    {
        _log.Add(SharingEvent.Joined, null, "known");
        using (var connection = _db.Open())
            connection.Execute("INSERT INTO sharing_log(at, kind, detail) VALUES ('2026-01-01T00:00:00Z', 'FromTheFuture', 'odd')");
        _log.Add(SharingEvent.SignedIn, null, "also known");

        Assert.Equal(["also known", "known"], _log.Recent(10).Select(e => e.Detail));
    }

    [Fact]
    public void Log_CapsAt5000()
    {
        for (var i = 1; i <= 5_200; i++) _log.Add(SharingEvent.SignedIn, null, "entry " + i);

        // Trimming runs every 100th insert, so the table may sit up to 99 rows over the cap.
        Assert.InRange(Rows(), 5_000, 5_099);
        Assert.Equal("entry 5200", _log.Recent(1)[0].Detail);
    }

    [Fact]
    public void Add_ForAnAccountThatIsGone_KeepsTheEntryWithNoUser()
    {
        _log.Add(SharingEvent.SignInLocked, 999, "");

        var entry = Assert.Single(_log.Recent(10));
        Assert.Null(entry.UserId);
        Assert.Equal(SharingEvent.SignInLocked, entry.Kind);
    }

    [Fact]
    public void Add_ForALiveAccount_KeepsTheUser()
    {
        _log.Add(SharingEvent.SignedIn, LibraryDatabase.OwnerId, "");

        Assert.Equal(LibraryDatabase.OwnerId, Assert.Single(_log.Recent(10)).UserId);
    }

    [Fact]
    public void Log_TrimsOnTheFirstInsertAfterARestart()
    {
        for (var i = 0; i < 5_050; i++) _log.Add(SharingEvent.SignedIn, null, "old");
        var restarted = new SharingLog(_db, _clock);

        restarted.Add(SharingEvent.SignedIn, null, "new");

        Assert.Equal(5_000, Rows());
    }
}
