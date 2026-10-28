using Dapper;
using MediaBoard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Storage;

public sealed class LibraryDatabaseTests : IDisposable
{
    private static readonly string[] ExpectedTables =
    [
        "episodes", "file_episodes", "file_titles", "files", "folders",
        "match_rules", "overrides", "progress", "review_candidates", "titles",
    ];

    private const string InsertFolder =
        "INSERT INTO folders(path, enabled, added_at) VALUES ('X:\\Media', 1, '2026-01-01'); SELECT last_insert_rowid()";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "library.db");

    public LibraryDatabaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Pooled connections keep the file locked on Windows.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Open_CreatesSchemaOnce()
    {
        var db = new LibraryDatabase(DbPath);

        using (var first = db.Open())
        {
            Assert.Equal(1, first.ExecuteScalar<int>("PRAGMA user_version"));
            first.Execute("INSERT INTO folders(path, enabled, added_at) VALUES ('X:\\Media', 1, '2026-01-01')");
        }

        using var second = db.Open();
        Assert.Equal(1, second.ExecuteScalar<int>("PRAGMA user_version"));
        // A second open must not re-run the schema.
        Assert.Equal(1, second.ExecuteScalar<int>("SELECT COUNT(*) FROM folders"));

        var tables = second.Query<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name").ToList();
        Assert.Equal(ExpectedTables, tables);
    }

    [Fact]
    public void Open_NewInstanceOnExistingFile_KeepsSchemaAndData()
    {
        using (var conn = new LibraryDatabase(DbPath).Open())
            conn.Execute("INSERT INTO folders(path, enabled, added_at) VALUES ('X:\\Media', 1, '2026-01-01')");

        using var again = new LibraryDatabase(DbPath).Open();
        Assert.Equal(1, again.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(1, again.ExecuteScalar<int>("SELECT COUNT(*) FROM folders"));
    }

    [Fact]
    public async Task Open_ConcurrentFirstOpen_AppliesSchemaOnce()
    {
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            using var conn = new LibraryDatabase(DbPath).Open();
            Assert.Equal(1, conn.ExecuteScalar<int>("PRAGMA user_version"));
        })));
    }

    [Fact]
    public void Open_EnablesWalAndForeignKeys()
    {
        using var conn = new LibraryDatabase(DbPath).Open();

        Assert.Equal("wal", conn.ExecuteScalar<string>("PRAGMA journal_mode"));
        Assert.Equal(1, conn.ExecuteScalar<int>("PRAGMA foreign_keys"));
        Assert.Throws<SqliteException>(() => conn.Execute(
            "INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status) " +
            "VALUES (999, 'a.mkv', 1, 1, '2026-01-01', 0, '{}', 'pending')"));
    }

    [Fact]
    public void Open_StoresUnicode()
    {
        const string relPath = "Цікава | Ідея’s";
        using var conn = new LibraryDatabase(DbPath).Open();
        var folderId = conn.ExecuteScalar<long>(InsertFolder);

        conn.Execute(
            "INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status) " +
            "VALUES (@folderId, @relPath, 10, 20, '2026-01-01', 0, '{}', 'pending')",
            new { folderId, relPath });

        Assert.Equal(relPath, conn.ExecuteScalar<string>("SELECT rel_path FROM files"));
    }

    [Fact]
    public void Files_RejectsUnknownStatus()
    {
        using var conn = new LibraryDatabase(DbPath).Open();
        var folderId = conn.ExecuteScalar<long>(InsertFolder);

        Assert.Throws<SqliteException>(() => conn.Execute(
            "INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status) " +
            "VALUES (@folderId, 'a.mkv', 1, 1, '2026-01-01', 0, '{}', 'bogus')", new { folderId }));
    }
}
