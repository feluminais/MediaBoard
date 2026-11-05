using System.Globalization;
using MediaBoard.Core.Parsing;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Storage;

/// <summary>
/// Opens connections to the library database and brings its schema up to date.
/// Every store takes one of these and calls <see cref="Open"/> per operation.
/// </summary>
public sealed class LibraryDatabase
{
    private const int CurrentVersion = 4;
    private const string SchemaResource = "Schema.sql";

    /// <summary>The person at this PC. The migration creates the row, and it is never removed.</summary>
    public const int OwnerId = 1;

    private readonly string _connectionString;
    private readonly object _initLock = new();
    private bool _initialized;

    public LibraryDatabase(string path)
    {
        FilePath = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }

    public string FilePath { get; }

    /// <summary>Opens a new connection with WAL and foreign keys on. The caller disposes it.</summary>
    public SqliteConnection Open()
    {
        EnsureInitialized();
        return OpenConnection();
    }

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized)) return;
        lock (_initLock)
        {
            if (_initialized) return;
            ApplySchema();
            Volatile.Write(ref _initialized, true);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            // WAL is stored in the file, but setting it is cheap and keeps a copied or reset file honest.
            Execute(connection, "PRAGMA journal_mode = WAL");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void ApplySchema()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var connection = OpenConnection();
        BackUpV3(connection);
        // Not deferred: BEGIN IMMEDIATE takes the write lock up front, so two instances
        // opening a new file cannot both apply the schema.
        using var transaction = connection.BeginTransaction(deferred: false);
        var version = ReadVersion(connection);
        if (version >= CurrentVersion) return;

        if (version == 0)
        {
            Execute(connection, LoadSchema());
            // A new database holds no parses yet, so none of them is out of date.
            Execute(connection,
                $"INSERT INTO meta(key, value) VALUES ('parser_version', '{PathParser.Version.ToString(CultureInfo.InvariantCulture)}')");
        }
        else
        {
            if (version < 2)
            {
                // v1 to v2: the meta table. No parser version is recorded, so the stored parses are redone once.
                Execute(connection, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
            }

            if (version < 3)
            {
                // v2 to v3: titles removed from Continue watching.
                Execute(connection,
                    "CREATE TABLE continue_dismissed (" +
                    "title_id INTEGER PRIMARY KEY REFERENCES titles(id) ON DELETE CASCADE, played_at TEXT NOT NULL)");
            }

            if (version < 4) MigrateToV4(connection);
        }

        Execute(connection, $"PRAGMA user_version = {CurrentVersion}");
        transaction.Commit();
    }

    /// <summary>
    /// v3 to v4 moves every progress row to another table, so the database is backed up first, once, and the backup
    /// is never replaced. The SQLite backup API reads through the connection, so commits that are still only in the
    /// WAL are included. It writes to a .tmp name and moves it into place, so an interrupted backup is never mistaken
    /// for a finished one. A failure here stops the migration, which then retries on the next launch.
    /// </summary>
    private void BackUpV3(SqliteConnection connection)
    {
        var version = ReadVersion(connection);
        if (version is < 1 or > 3) return;

        var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(FilePath))!, "library.v3.bak");
        if (File.Exists(backup)) return;

        var temp = backup + ".tmp";
        File.Delete(temp);
        using (var destination = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
        {
            destination.Open();
            connection.BackupDatabase(destination);
        }

        File.Move(temp, backup);
    }

    private static void MigrateToV4(SqliteConnection connection)
    {
        // SQLite can't change a primary key in place, so progress and continue_dismissed are rebuilt:
        // create, copy, drop, rename. The owner row comes first because the copies point at it.
        Execute(connection,
            """
            CREATE TABLE users (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                name          TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                password_hash TEXT,
                role          TEXT    NOT NULL CHECK (role IN ('owner', 'viewer')),
                disabled      INTEGER NOT NULL DEFAULT 0,
                all_folders   INTEGER NOT NULL DEFAULT 1,
                created_at    TEXT    NOT NULL,
                last_seen     TEXT
            );
            INSERT INTO users(id, name, role, created_at) VALUES (1, 'Owner', 'owner', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

            CREATE TABLE user_folders (
                user_id   INTEGER NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
                folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                PRIMARY KEY (user_id, folder_id)
            );
            CREATE INDEX ix_user_folders_folder ON user_folders(folder_id);

            CREATE TABLE devices (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id      INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                name         TEXT    NOT NULL,
                token_hash   TEXT    NOT NULL UNIQUE,
                created_at   TEXT    NOT NULL,
                last_seen    TEXT,
                last_address TEXT
            );
            CREATE INDEX ix_devices_user ON devices(user_id);

            CREATE TABLE invites (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                code_hash  TEXT    NOT NULL UNIQUE,
                created_at TEXT    NOT NULL,
                expires_at TEXT    NOT NULL,
                used_at    TEXT
            );
            CREATE INDEX ix_invites_user ON invites(user_id);

            CREATE TABLE sharing_log (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                at      TEXT    NOT NULL,
                user_id INTEGER REFERENCES users(id) ON DELETE SET NULL,
                kind    TEXT    NOT NULL,
                detail  TEXT    NOT NULL
            );

            CREATE TABLE progress_v4 (
                user_id     INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                position_s  REAL    NOT NULL DEFAULT 0,
                duration_s  REAL    NOT NULL DEFAULT 0,
                watched     INTEGER NOT NULL DEFAULT 0,
                last_played TEXT,
                PRIMARY KEY (user_id, file_id)
            );
            INSERT INTO progress_v4(user_id, file_id, position_s, duration_s, watched, last_played)
                SELECT 1, file_id, position_s, duration_s, watched, last_played FROM progress;
            DROP TABLE progress;
            ALTER TABLE progress_v4 RENAME TO progress;
            CREATE INDEX ix_progress_last_played ON progress(last_played);
            CREATE INDEX ix_progress_file ON progress(file_id);

            CREATE TABLE continue_dismissed_v4 (
                user_id   INTEGER NOT NULL REFERENCES users(id)  ON DELETE CASCADE,
                title_id  INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
                played_at TEXT    NOT NULL,
                PRIMARY KEY (user_id, title_id)
            );
            INSERT INTO continue_dismissed_v4(user_id, title_id, played_at)
                SELECT 1, title_id, played_at FROM continue_dismissed;
            DROP TABLE continue_dismissed;
            ALTER TABLE continue_dismissed_v4 RENAME TO continue_dismissed;
            CREATE INDEX ix_continue_dismissed_title ON continue_dismissed(title_id);
            """);
    }

    private static long ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string LoadSchema()
    {
        using var stream = typeof(LibraryDatabase).Assembly.GetManifestResourceStream(SchemaResource)
            ?? throw new InvalidOperationException($"Embedded resource {SchemaResource} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
