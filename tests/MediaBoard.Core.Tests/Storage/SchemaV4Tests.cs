using Dapper;
using MediaBoard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Storage;

public sealed class SchemaV4Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "library.db");

    private string BackupPath => Path.Combine(_dir, "library.v3.bak");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Turns an open current-schema database into what a v3 file looked like: the two per-user tables without a user
    /// column, none of the sharing tables, and user_version 3.
    /// </summary>
    internal static void DowngradeToV3(SqliteConnection connection) => connection.Execute(
        """
        DROP TABLE progress;
        DROP TABLE continue_dismissed;
        DROP TABLE sharing_log;
        DROP TABLE invites;
        DROP TABLE devices;
        DROP TABLE user_folders;
        DROP TABLE users;
        CREATE TABLE progress (
            file_id     INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
            position_s  REAL    NOT NULL DEFAULT 0,
            duration_s  REAL    NOT NULL DEFAULT 0,
            watched     INTEGER NOT NULL DEFAULT 0,
            last_played TEXT
        );
        CREATE INDEX ix_progress_last_played ON progress(last_played);
        CREATE TABLE continue_dismissed (
            title_id  INTEGER PRIMARY KEY REFERENCES titles(id) ON DELETE CASCADE,
            played_at TEXT    NOT NULL
        );
        PRAGMA user_version = 3;
        """);

    private void MakeV3File()
    {
        using (var connection = new LibraryDatabase(DbPath).Open())
        {
            connection.Execute("INSERT INTO folders(path, enabled, added_at) VALUES ('X:\\Media', 1, '2026-01-01')");
            for (var i = 1; i <= 3; i++)
            {
                connection.Execute(
                    "INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status) " +
                    "VALUES (1, @name, 1, 1, '2026-01-01', 0, '{}', 'matched')", new { name = $"{i}.mkv" });
            }

            connection.Execute("INSERT INTO titles(kind, name) VALUES ('show', 'Show')");
            DowngradeToV3(connection);
            connection.Execute(
                """
                INSERT INTO progress(file_id, position_s, duration_s, watched, last_played) VALUES
                    (1, 10.5, 100, 0, '2026-02-01T10:00:00.0000000Z'),
                    (2, 0, 200, 1, '2026-02-02T10:00:00.0000000Z'),
                    (3, 55, 300, 0, NULL);
                INSERT INTO continue_dismissed(title_id, played_at) VALUES (1, '2026-02-03T10:00:00.0000000Z');
                """);
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void NewDatabase_HasOwnerRow()
    {
        using var connection = new LibraryDatabase(DbPath).Open();

        var owner = connection.QuerySingle<(long Id, string Role, string? PasswordHash, long AllFolders, long Disabled)>(
            "SELECT id, role, password_hash, all_folders, disabled FROM users");

        Assert.Equal(1, LibraryDatabase.OwnerId);
        Assert.Equal((LibraryDatabase.OwnerId, "owner", null, 1L, 0L), owner);
        Assert.Equal(4, connection.ExecuteScalar<int>("PRAGMA user_version"));
    }

    [Fact]
    public void V3Database_MovesProgressToOwner()
    {
        MakeV3File();

        using var connection = new LibraryDatabase(DbPath).Open();

        Assert.Equal(4, connection.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM users"));
        var progress = connection.Query<(long UserId, long FileId, double PositionS, double DurationS, long Watched, string? LastPlayed)>(
            "SELECT user_id, file_id, position_s, duration_s, watched, last_played FROM progress ORDER BY file_id").ToList();
        Assert.Equal(
            [
                (1L, 1L, 10.5, 100.0, 0L, "2026-02-01T10:00:00.0000000Z"),
                (1L, 2L, 0.0, 200.0, 1L, "2026-02-02T10:00:00.0000000Z"),
                (1L, 3L, 55.0, 300.0, 0L, null),
            ],
            progress);
        var dismissed = connection.QuerySingle<(long UserId, long TitleId, string PlayedAt)>(
            "SELECT user_id, title_id, played_at FROM continue_dismissed");
        Assert.Equal((1L, 1L, "2026-02-03T10:00:00.0000000Z"), dismissed);
        Assert.Equal(1, connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_progress_last_played'"));
    }

    [Fact]
    public void V3Database_LeavesBackup()
    {
        MakeV3File();
        var original = File.ReadAllBytes(DbPath);

        new LibraryDatabase(DbPath).Open().Dispose();

        Assert.True(File.Exists(BackupPath));
        Assert.Equal(original, File.ReadAllBytes(BackupPath));

        // A later open must keep the first backup, not copy the migrated file over it.
        SqliteConnection.ClearAllPools();
        new LibraryDatabase(DbPath).Open().Dispose();
        Assert.Equal(original, File.ReadAllBytes(BackupPath));
    }

    [Fact]
    public void NewDatabase_LeavesNoBackup()
    {
        new LibraryDatabase(DbPath).Open().Dispose();

        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void V4_RemovingUserCascades()
    {
        using var connection = new LibraryDatabase(DbPath).Open();
        connection.Execute("INSERT INTO folders(path, enabled, added_at) VALUES ('X:\\Media', 1, '2026-01-01')");
        connection.Execute(
            "INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status) " +
            "VALUES (1, 'a.mkv', 1, 1, '2026-01-01', 0, '{}', 'matched')");
        connection.Execute("INSERT INTO titles(kind, name) VALUES ('show', 'Show')");
        connection.Execute("INSERT INTO users(name, role, created_at) VALUES ('Sam', 'viewer', '2026-01-01')");
        var viewer = connection.ExecuteScalar<long>("SELECT id FROM users WHERE name = 'sam'");
        connection.Execute(
            """
            INSERT INTO progress(user_id, file_id, position_s) VALUES (@viewer, 1, 5), (1, 1, 7);
            INSERT INTO continue_dismissed(user_id, title_id, played_at) VALUES (@viewer, 1, '2026-01-01'), (1, 1, '2026-01-01');
            INSERT INTO user_folders(user_id, folder_id) VALUES (@viewer, 1);
            INSERT INTO devices(user_id, name, token_hash, created_at) VALUES (@viewer, 'PC', 'h', '2026-01-01');
            INSERT INTO invites(user_id, code_hash, created_at, expires_at) VALUES (@viewer, 'c', '2026-01-01', '2026-01-08');
            INSERT INTO sharing_log(at, user_id, kind, detail) VALUES ('2026-01-01', @viewer, 'sign-in', '');
            """, new { viewer });

        connection.Execute("DELETE FROM users WHERE id = @viewer", new { viewer });

        foreach (var table in new[] { "progress", "continue_dismissed" })
        {
            Assert.Equal(1, connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(1, connection.ExecuteScalar<int>($"SELECT user_id FROM {table}"));
        }

        foreach (var table in new[] { "user_folders", "devices", "invites" })
            Assert.Equal(0, connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}"));

        // The log outlives the account it names.
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sharing_log WHERE user_id IS NULL"));
    }

    [Fact]
    public void V4_ProgressKeyIsPerUser()
    {
        using var connection = new LibraryDatabase(DbPath).Open();
        connection.Execute("INSERT INTO folders(path, enabled, added_at) VALUES ('X:\\Media', 1, '2026-01-01')");
        connection.Execute(
            "INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status) " +
            "VALUES (1, 'a.mkv', 1, 1, '2026-01-01', 0, '{}', 'matched')");
        connection.Execute("INSERT INTO users(name, role, created_at) VALUES ('Sam', 'viewer', '2026-01-01')");

        connection.Execute("INSERT INTO progress(user_id, file_id) VALUES (1, 1), (2, 1)");

        Assert.Throws<SqliteException>(() => connection.Execute("INSERT INTO progress(user_id, file_id) VALUES (1, 1)"));
    }

    [Fact]
    public void V3Migration_FailureLeavesV3Intact()
    {
        MakeV3File();
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            // Makes the rebuild fail after the sharing tables were already created in the same transaction.
            connection.Open();
            connection.Execute("CREATE TABLE progress_v4 (x INTEGER)");
        }

        SqliteConnection.ClearAllPools();
        Assert.ThrowsAny<SqliteException>(() => new LibraryDatabase(DbPath).Open());

        SqliteConnection.ClearAllPools();
        using var after = new SqliteConnection($"Data Source={DbPath}");
        after.Open();
        Assert.Equal(3, after.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(0, after.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'users'"));
        Assert.Equal(3, after.ExecuteScalar<int>("SELECT COUNT(*) FROM progress"));
        Assert.Equal(0, after.ExecuteScalar<int>("SELECT COUNT(*) FROM pragma_table_info('progress') WHERE name = 'user_id'"));
    }
}
