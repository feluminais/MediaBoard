using System.Globalization;
using Dapper;
using MediaBoard.Core.Parsing;
using MediaBoard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Storage;

public sealed class LibraryDatabaseTests : IDisposable
{
    private static readonly string[] ExpectedTables =
    [
        "continue_dismissed", "episodes", "file_episodes", "file_titles", "files", "folders",
        "match_rules", "meta", "overrides", "progress", "review_candidates", "titles",
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
            Assert.Equal(3, first.ExecuteScalar<int>("PRAGMA user_version"));
            first.Execute("INSERT INTO folders(path, enabled, added_at) VALUES ('X:\\Media', 1, '2026-01-01')");
        }

        using var second = db.Open();
        Assert.Equal(3, second.ExecuteScalar<int>("PRAGMA user_version"));
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
        Assert.Equal(3, again.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(1, again.ExecuteScalar<int>("SELECT COUNT(*) FROM folders"));
    }

    [Fact]
    public void Open_NewDatabase_RecordsTheCurrentParserVersion()
    {
        using var conn = new LibraryDatabase(DbPath).Open();

        Assert.Equal(PathParser.Version.ToString(CultureInfo.InvariantCulture),
            conn.ExecuteScalar<string>("SELECT value FROM meta WHERE key = 'parser_version'"));
    }

    [Fact]
    public void Open_VersionOneDatabase_MigratesKeepingItsData_AndLeavesTheParserVersionUnknown()
    {
        using (var conn = new LibraryDatabase(DbPath).Open())
        {
            var folderId = conn.ExecuteScalar<long>(InsertFolder);
            conn.Execute(
                "INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status) " +
                "VALUES (@folderId, 'a.mkv', 1, 1, '2026-01-01', 0, '{}', 'matched')", new { folderId });
            // What a database written before the parser version existed looks like.
            conn.Execute("DROP TABLE meta; DROP TABLE continue_dismissed; PRAGMA user_version = 1;");
        }

        SqliteConnection.ClearAllPools();
        using var again = new LibraryDatabase(DbPath).Open();

        Assert.Equal(3, again.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal("matched", again.ExecuteScalar<string>("SELECT status FROM files WHERE rel_path = 'a.mkv'"));
        Assert.Null(again.ExecuteScalar<string?>("SELECT value FROM meta WHERE key = 'parser_version'"));
    }

    [Fact]
    public void Open_VersionTwoDatabase_MigratesKeepingItsData_AndAddsTheDismissedTable()
    {
        using (var conn = new LibraryDatabase(DbPath).Open())
        {
            conn.Execute(InsertFolder);
            conn.Execute("DROP TABLE continue_dismissed; PRAGMA user_version = 2;");
        }

        SqliteConnection.ClearAllPools();
        using var again = new LibraryDatabase(DbPath).Open();

        Assert.Equal(3, again.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(1, again.ExecuteScalar<int>("SELECT COUNT(*) FROM folders"));
        Assert.Equal(0, again.ExecuteScalar<int>("SELECT COUNT(*) FROM continue_dismissed"));
    }

    [Fact]
    public async Task Open_ConcurrentFirstOpen_AppliesSchemaOnce()
    {
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            using var conn = new LibraryDatabase(DbPath).Open();
            Assert.Equal(3, conn.ExecuteScalar<int>("PRAGMA user_version"));
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

    // overrides(target_kind, target_id) has no foreign key, so a reused id would inherit a stale override.
    [Theory]
    [InlineData("folders", "INSERT INTO folders(path, enabled, added_at) VALUES (@n, 1, '2026-01-01')")]
    [InlineData("titles", "INSERT INTO titles(kind, name) VALUES ('movie', @n)")]
    [InlineData("files", "INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status) " +
        "VALUES ((SELECT id FROM folders LIMIT 1), @n, 1, 1, '2026-01-01', 0, '{}', 'pending')")]
    [InlineData("episodes", "INSERT INTO episodes(title_id, season, number) " +
        "VALUES ((SELECT id FROM titles LIMIT 1), 1, CAST(@n AS INTEGER))")]
    public void Ids_AreNeverReused(string table, string insert)
    {
        using var conn = new LibraryDatabase(DbPath).Open();
        // Parents that the child tables need; they stay in place for the whole test.
        if (table == "files") conn.Execute(InsertFolder);
        if (table == "episodes") conn.Execute("INSERT INTO titles(kind, name) VALUES ('show', 'Parent')");

        conn.Execute(insert, new { n = "1" });
        var first = conn.ExecuteScalar<long>($"SELECT MAX(id) FROM {table}");
        conn.Execute($"DELETE FROM {table} WHERE id = @first", new { first });
        conn.Execute(insert, new { n = "2" });
        var second = conn.ExecuteScalar<long>($"SELECT MAX(id) FROM {table}");

        Assert.True(second > first, $"{table}: new id {second} reuses or precedes deleted id {first}");
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
