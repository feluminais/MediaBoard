using Dapper;

namespace MediaBoard.Core.Storage;

public sealed record LibraryFolder(int Id, string Path, bool Enabled);

/// <summary>The library folders the user added. Touches only the database, never the folders themselves.</summary>
public sealed class FolderStore(LibraryDatabase db, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Adds a folder, or returns the existing entry when the path is already there.</summary>
    public LibraryFolder Add(string path)
    {
        var normalized = Normalize(path);
        using var connection = db.Open();
        connection.Execute(
            "INSERT INTO folders(path, enabled, added_at) VALUES (@path, 1, @addedAt) ON CONFLICT(path) DO NOTHING",
            new { path = normalized, addedAt = FileStore.FormatTime(_time.GetUtcNow()) });
        return connection.QuerySingle<Row>(
            "SELECT id, path, enabled FROM folders WHERE path = @path", new { path = normalized }).ToFolder();
    }

    public IReadOnlyList<LibraryFolder> All()
    {
        using var connection = db.Open();
        return connection.Query<Row>("SELECT id, path, enabled FROM folders ORDER BY id")
            .Select(r => r.ToFolder()).ToList();
    }

    public void SetEnabled(int id, bool enabled)
    {
        using var connection = db.Open();
        connection.Execute("UPDATE folders SET enabled = @enabled WHERE id = @id", new { id, enabled = enabled ? 1 : 0 });
    }

    /// <summary>Forgets the folder and, by cascade, its file rows, progress and matches. Nothing on disk is touched.</summary>
    public void Remove(int id)
    {
        using var connection = db.Open();
        connection.Execute("DELETE FROM folders WHERE id = @id", new { id });
    }

    /// <summary>Points the folder at a new location (a moved or re-lettered drive). File rows keep their ids.</summary>
    public void Repoint(int id, string newPath)
    {
        using var connection = db.Open();
        connection.Execute("UPDATE folders SET path = @path WHERE id = @id", new { id, path = Normalize(newPath) });
    }

    private static string Normalize(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full);
        return full == root ? full : full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public string Path { get; set; } = "";
        public long Enabled { get; set; }

        public LibraryFolder ToFolder() => new((int)Id, Path, Enabled != 0);
    }
}
