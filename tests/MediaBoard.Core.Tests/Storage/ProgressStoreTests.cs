using Dapper;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Storage;

public sealed class ProgressStoreTests : IDisposable
{
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
    public void Get_NothingSaved_IsNull() => Assert.Null(_store.Get(_fileId));

    [Fact]
    public void Save_StoresPositionDurationAndTime()
    {
        _store.Save(_fileId, 120.5, 3000);

        var saved = _store.Get(_fileId)!;
        Assert.Equal(new Progress(_fileId, 120.5, 3000, false, _clock.GetUtcNow().UtcDateTime), saved);
    }

    [Fact]
    public void Save_KeepsWatchedAndKnownDuration()
    {
        _store.Save(_fileId, 120, 3000);
        _store.SetWatched(_fileId, true);

        _store.Save(_fileId, 30, 0);

        var saved = _store.Get(_fileId)!;
        Assert.True(saved.Watched);
        Assert.Equal(30, saved.PositionS);
        Assert.Equal(3000, saved.DurationS);
    }

    [Fact]
    public void SetWatched_WithoutARow_CreatesOne()
    {
        _store.SetWatched(_fileId, true);

        var saved = _store.Get(_fileId)!;
        Assert.True(saved.Watched);
        Assert.Equal(0, saved.PositionS);
    }

    [Fact]
    public void SetWatched_True_KeepsThePosition()
    {
        _store.Save(_fileId, 120, 3000);
        _store.SetWatched(_fileId, true);

        Assert.Equal(120, _store.Get(_fileId)!.PositionS);
    }

    [Fact]
    public void SetWatched_False_ForgetsThePosition()
    {
        _store.Save(_fileId, 2900, 3000);
        _store.SetWatched(_fileId, true);

        _store.SetWatched(_fileId, false);

        var saved = _store.Get(_fileId)!;
        Assert.False(saved.Watched);
        Assert.Equal(0, saved.PositionS);
    }

    [Fact]
    public void SetWatched_False_WithoutARow_CreatesNothing()
    {
        _store.SetWatched(_fileId, false);

        Assert.Null(_store.Get(_fileId));
    }

    [Fact]
    public void WatchedEpisodeIds_ListsEpisodesOfWatchedFilesOnly()
    {
        using (var connection = _db.Open())
        {
            connection.Execute("INSERT INTO titles(kind, name) VALUES ('show', 'Show')");
            connection.Execute("INSERT INTO episodes(title_id, season, number) VALUES (1, 1, 1), (1, 1, 2), (1, 1, 3)");
            connection.Execute("INSERT INTO file_episodes(file_id, episode_id) VALUES (@f, 1), (@f, 2)", new { f = _fileId });
        }

        _store.Save(_fileId, 100, 3000);
        Assert.Empty(_store.WatchedEpisodeIds());

        _store.SetWatched(_fileId, true);
        Assert.Equal([1, 2], _store.WatchedEpisodeIds());
    }
}
