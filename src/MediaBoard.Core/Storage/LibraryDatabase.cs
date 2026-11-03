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
    private const int CurrentVersion = 3;
    private const string SchemaResource = "Schema.sql";

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
        }

        Execute(connection, $"PRAGMA user_version = {CurrentVersion}");
        transaction.Commit();
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
