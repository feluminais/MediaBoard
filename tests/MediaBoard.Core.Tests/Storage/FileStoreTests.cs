using Dapper;
using MediaBoard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Storage;

public sealed class FileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly FileStore _store;

    public FileStoreTests()
    {
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _store = new FileStore(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void FindByPath_FindsAFileBelowALibraryFolder_IgnoringCase()
    {
        var id = AddFile(AddFolder(@"X:\Media"), @"Show\S01E01.mkv");

        Assert.Equal(id, _store.FindByPath(@"X:\Media\Show\S01E01.mkv")?.Id);
        Assert.Equal(id, _store.FindByPath(@"x:\media\SHOW\s01e01.MKV")?.Id);
        Assert.Equal(id, _store.FindByPath(@"X:\Media\Show\.\S01E01.mkv")?.Id);
        Assert.Equal(id, _store.FindByPath("X:/Media/Show/S01E01.mkv")?.Id);

        // A folder path stored with a trailing slash still matches.
        var slashed = AddFile(AddFolder(@"Y:\Shows\"), "a.mkv");
        Assert.Equal(slashed, _store.FindByPath(@"Y:\Shows\a.mkv")?.Id);
    }

    [Fact]
    public void FindByPath_NestedFolders_UsesTheFolderThatHoldsTheFile()
    {
        AddFile(AddFolder(@"X:\Media"), @"Anime\a.mkv");
        var inner = AddFile(AddFolder(@"X:\Media\Anime"), "a.mkv");

        Assert.Equal(inner, _store.FindByPath(@"X:\Media\Anime\a.mkv")?.Id);
    }

    [Fact]
    public void FindByPath_FolderSwitchedOff_IsNotFound()
    {
        var folder = AddFolder(@"X:\Media");
        AddFile(folder, "a.mkv");
        using (var connection = _db.Open()) connection.Execute("UPDATE folders SET enabled = 0 WHERE id = @folder", new { folder });

        Assert.Null(_store.FindByPath(@"X:\Media\a.mkv"));
    }

    [Theory]
    [InlineData(@"X:\Media\unknown.mkv")]
    [InlineData(@"X:\Media2\a.mkv")] // shares a prefix with a folder, but is not below it
    [InlineData(@"D:\Elsewhere\a.mkv")]
    [InlineData("")]
    [InlineData("not a path | at all?")]
    public void FindByPath_FileNotInTheLibrary_IsNull(string path)
    {
        AddFile(AddFolder(@"X:\Media"), "a.mkv");

        Assert.Null(_store.FindByPath(path));
    }

    private int AddFolder(string path)
    {
        using var connection = _db.Open();
        connection.Execute("INSERT INTO folders(path, enabled, added_at) VALUES (@path, 1, '2026-01-01')", new { path });
        return connection.ExecuteScalar<int>("SELECT last_insert_rowid()");
    }

    private int AddFile(int folderId, string relPath)
    {
        using var connection = _db.Open();
        connection.Execute(
            "INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status) " +
            "VALUES (@folderId, @relPath, 1, 1, '2026-01-01', 0, '{}', 'matched')",
            new { folderId, relPath });
        return connection.ExecuteScalar<int>("SELECT last_insert_rowid()");
    }
}
