using Dapper;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Storage;

public sealed class ProgressStoreTests : IDisposable
{
    private const int Owner = LibraryDatabase.OwnerId;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly FakeClock _clock = new();
    private readonly ProgressStore _store;
    private readonly int _fileId;

    public ProgressStoreTests()
    {
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _store = new ProgressStore(_db, _clock);
        using var connection = _db.Open();
        connection.Execute("INSERT INTO folders(path, enabled, added_at) VALUES ('X:\\Media', 1, '2026-01-01')");
        connection.Execute(
            "INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status) " +
            "VALUES (1, 'a.mkv', 1, 1, '2026-01-01', 0, '{}', 'matched')");
        _fileId = connection.ExecuteScalar<int>("SELECT id FROM files");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Save_StoresPositionDurationAndTime()
    {
        _store.Save(Owner, _fileId, 120.5, 3000);

        var saved = _store.Get(Owner, _fileId)!;
        Assert.Equal(new Progress(_fileId, 120.5, 3000, false, _clock.GetUtcNow().UtcDateTime), saved);
    }

    [Fact]
    public void Save_KeepsWatchedAndKnownDuration()
    {
        _store.Save(Owner, _fileId, 120, 3000);
        _store.SetWatched(Owner, _fileId, true);

        _store.Save(Owner, _fileId, 30, 0);

        var saved = _store.Get(Owner, _fileId)!;
        Assert.True(saved.Watched);
        Assert.Equal(30, saved.PositionS);
        Assert.Equal(3000, saved.DurationS);
    }

    [Fact]
    public void SetWatched_WithoutARow_CreatesOne()
    {
        _store.SetWatched(Owner, _fileId, true);

        var saved = _store.Get(Owner, _fileId)!;
        Assert.True(saved.Watched);
        Assert.Equal(0, saved.PositionS);
    }

    [Fact]
    public void SetWatched_True_KeepsThePosition()
    {
        _store.Save(Owner, _fileId, 120, 3000);
        _store.SetWatched(Owner, _fileId, true);

        Assert.Equal(120, _store.Get(Owner, _fileId)!.PositionS);
    }

    [Fact]
    public void SetWatched_False_ForgetsThePosition()
    {
        _store.Save(Owner, _fileId, 2900, 3000);
        _store.SetWatched(Owner, _fileId, true);

        _store.SetWatched(Owner, _fileId, false);

        var saved = _store.Get(Owner, _fileId)!;
        Assert.False(saved.Watched);
        Assert.Equal(0, saved.PositionS);
    }

    [Fact]
    public void SetWatched_False_WithoutARow_CreatesNothing()
    {
        _store.SetWatched(Owner, _fileId, false);

        Assert.Null(_store.Get(Owner, _fileId));
    }

    [Fact]
    public void SaveIfNewer_NoRow_Stores()
    {
        var at = _clock.GetUtcNow().AddMinutes(-1);

        Assert.True(_store.SaveIfNewer(Owner, _fileId, new ProgressEntry(120, 3000, true, at)));

        Assert.Equal(new Progress(_fileId, 120, 3000, true, at.UtcDateTime), _store.Get(Owner, _fileId));
    }

    [Fact]
    public void SaveIfNewer_NoWordOnWatched_NoRow_IsNotWatched()
    {
        _store.SaveIfNewer(Owner, _fileId, new ProgressEntry(120, 3000, null, _clock.GetUtcNow()));

        Assert.False(_store.Get(Owner, _fileId)!.Watched);
    }

    [Fact]
    public void SaveIfNewer_NewerEntry_Replaces()
    {
        var at = _clock.GetUtcNow().AddMinutes(-2);
        _store.SaveIfNewer(Owner, _fileId, new ProgressEntry(120, 3000, null, at));

        Assert.True(_store.SaveIfNewer(Owner, _fileId, new ProgressEntry(240, 3000, null, at.AddMinutes(1))));

        Assert.Equal(240, _store.Get(Owner, _fileId)!.PositionS);
    }

    [Fact]
    public void SaveIfNewer_OlderEntry_ChangesNothing()
    {
        var at = _clock.GetUtcNow().AddMinutes(-1);
        _store.SaveIfNewer(Owner, _fileId, new ProgressEntry(240, 3000, false, at));
        var before = _store.Get(Owner, _fileId);

        Assert.False(_store.SaveIfNewer(Owner, _fileId, new ProgressEntry(120, 1000, true, at.AddTicks(-1))));

        Assert.Equal(before, _store.Get(Owner, _fileId));
    }

    // A phone ahead of the server gets the same now twice when its reports land inside one tick of the clock.
    [Fact]
    public void SaveIfNewer_SameTime_TheLaterArrivalWins()
    {
        var at = _clock.GetUtcNow();
        _store.SaveIfNewer(Owner, _fileId, new ProgressEntry(120, 3000, null, at));

        Assert.True(_store.SaveIfNewer(Owner, _fileId, new ProgressEntry(240, 3000, null, at)));

        Assert.Equal(240, _store.Get(Owner, _fileId)!.PositionS);
    }

    [Fact]
    public void SaveIfNewer_FutureEntry_StampedNow_AndTheNextHonestEntryBeatsIt()
    {
        var now = _clock.GetUtcNow();
        _store.SaveIfNewer(Owner, _fileId, new ProgressEntry(1000, 3000, null, now.AddHours(1)));

        Assert.Equal(now.UtcDateTime, _store.Get(Owner, _fileId)!.LastPlayed);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_store.SaveIfNewer(Owner, _fileId, new ProgressEntry(50, 3000, null, _clock.GetUtcNow())));
        Assert.Equal(50, _store.Get(Owner, _fileId)!.PositionS);
    }

    [Fact]
    public void SaveIfNewer_OffsetTimes_CompareAsInstants()
    {
        var now = _clock.GetUtcNow();
        _store.SaveIfNewer(Owner, _fileId, new ProgressEntry(240, 3000, null, now.AddMinutes(-1)));

        // Its wall clock reads later than the saved one, but the moment is earlier.
        var earlier = now.AddMinutes(-2).ToOffset(TimeSpan.FromHours(5));

        Assert.False(_store.SaveIfNewer(Owner, _fileId, new ProgressEntry(120, 3000, null, earlier)));
        Assert.Equal(now.AddMinutes(-1).UtcDateTime, _store.Get(Owner, _fileId)!.LastPlayed);
    }

    [Fact]
    public void SaveIfNewer_KeepsWhatTheEntryDoesNotSay()
    {
        _store.Save(Owner, _fileId, 120, 3000);
        _store.SetWatched(Owner, _fileId, true);
        _clock.Advance(TimeSpan.FromMinutes(1));

        _store.SaveIfNewer(Owner, _fileId, new ProgressEntry(240, 0, null, _clock.GetUtcNow()));

        var saved = _store.Get(Owner, _fileId)!;
        Assert.Equal(3000, saved.DurationS);
        Assert.True(saved.Watched);
        Assert.Equal(240, saved.PositionS);
    }

    [Fact]
    public void SaveIfNewer_RowWithNoTime_Replaced()
    {
        using (var connection = _db.Open())
        {
            connection.Execute(
                "INSERT INTO progress(user_id, file_id, position_s, duration_s, watched, last_played) VALUES (@Owner, @fileId, 5, 10, 0, NULL)",
                new { Owner, fileId = _fileId });
        }

        Assert.True(_store.SaveIfNewer(Owner, _fileId, new ProgressEntry(240, 3000, null, _clock.GetUtcNow())));

        Assert.Equal(240, _store.Get(Owner, _fileId)!.PositionS);
    }

    [Fact]
    public void SaveIfNewer_IsPerUser()
    {
        using (var connection = _db.Open())
        {
            connection.Execute("INSERT INTO users(id, name, role, all_folders, created_at) VALUES (2, 'Sam', 'viewer', 1, '2026-01-01')");
        }

        var at = _clock.GetUtcNow();
        _store.SaveIfNewer(Owner, _fileId, new ProgressEntry(240, 3000, null, at));

        Assert.True(_store.SaveIfNewer(2, _fileId, new ProgressEntry(120, 3000, null, at.AddMinutes(-1))));

        Assert.Equal(240, _store.Get(Owner, _fileId)!.PositionS);
        Assert.Equal(120, _store.Get(2, _fileId)!.PositionS);
    }

    [Fact]
    public void SaveIfNewer_UnknownFile_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() => _store.SaveIfNewer(Owner, 99999, new ProgressEntry(1, 2, null, _clock.GetUtcNow())));

    [Fact]
    public void SetWatched_UnknownFile_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() => _store.SetWatched(Owner, 99999, true));

    [Fact]
    public void WatchedEpisodeIds_ListsEpisodesOfWatchedFilesOnly()
    {
        using (var connection = _db.Open())
        {
            connection.Execute("INSERT INTO titles(kind, name) VALUES ('show', 'Show')");
            connection.Execute("INSERT INTO episodes(title_id, season, number) VALUES (1, 1, 1), (1, 1, 2), (1, 1, 3)");
            connection.Execute("INSERT INTO file_episodes(file_id, episode_id) VALUES (@f, 1), (@f, 2)", new { f = _fileId });
        }

        _store.Save(Owner, _fileId, 100, 3000);
        Assert.Empty(_store.WatchedEpisodeIds(Owner));

        _store.SetWatched(Owner, _fileId, true);
        Assert.Equal([1, 2], _store.WatchedEpisodeIds(Owner));
    }
}
