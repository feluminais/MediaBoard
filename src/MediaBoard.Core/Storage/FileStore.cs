using System.Globalization;
using Dapper;
using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Storage;

/// <param name="RelPath">Path below the library folder, with backslashes.</param>
/// <param name="FirstSeen">When the scanner first saw the file, in UTC.</param>
public sealed record MediaFileRow(
    int Id,
    int FolderId,
    string RelPath,
    long Size,
    DateTime FirstSeen,
    bool Missing,
    ParsedMedia Parsed,
    string Status,
    double? DurationS);

/// <summary>Read access to the files table. The scanner owns the writes; only a delete from disk removes rows besides it.</summary>
public sealed class FileStore(LibraryDatabase db)
{
    private const string Select =
        "SELECT id, folder_id AS FolderId, rel_path AS RelPath, size, first_seen AS FirstSeen, missing, " +
        "parsed_json AS ParsedJson, status, duration_s AS DurationS FROM files";

    public MediaFileRow? Get(int id)
    {
        using var connection = db.Open();
        return connection.QuerySingleOrDefault<Row>($"{Select} WHERE id = @id", new { id })?.ToRow();
    }

    /// <summary>Every file with this status, missing ones included.</summary>
    public IReadOnlyList<MediaFileRow> ByStatus(string status)
    {
        using var connection = db.Open();
        return connection.Query<Row>($"{Select} WHERE status = @status ORDER BY id", new { status })
            .Select(r => r.ToRow()).ToList();
    }

    public IReadOnlyList<MediaFileRow> ByFolder(int folderId)
    {
        using var connection = db.Open();
        return connection.Query<Row>($"{Select} WHERE folder_id = @folderId ORDER BY id", new { folderId })
            .Select(r => r.ToRow()).ToList();
    }

    /// <summary>Stores the probed length; the identification service is the only caller.</summary>
    public void SetDuration(int id, double seconds)
    {
        using var connection = db.Open();
        connection.Execute("UPDATE files SET duration_s = @seconds WHERE id = @id", new { id, seconds });
    }

    /// <summary>
    /// Forgets files after they were sent to the Recycle Bin. By cascade their title and episode links, progress
    /// and review candidates go too. Nothing on disk is touched.
    /// </summary>
    public void Remove(IEnumerable<int> ids)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var id in ids) connection.Execute("DELETE FROM files WHERE id = @id", new { id }, transaction);
        transaction.Commit();
    }

    /// <summary>The file's absolute path on disk.</summary>
    public string FullPath(MediaFileRow file)
    {
        using var connection = db.Open();
        var folder = connection.QuerySingle<string>("SELECT path FROM folders WHERE id = @FolderId", new { file.FolderId });
        return Path.Combine(folder, file.RelPath);
    }

    internal static string FormatTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class Row
    {
        public long Id { get; set; }
        public long FolderId { get; set; }
        public string RelPath { get; set; } = "";
        public long Size { get; set; }
        public string FirstSeen { get; set; } = "";
        public long Missing { get; set; }
        public string ParsedJson { get; set; } = "";
        public string Status { get; set; } = "";
        public double? DurationS { get; set; }

        public MediaFileRow ToRow() => new(
            (int)Id, (int)FolderId, RelPath, Size, ParseTime(FirstSeen), Missing != 0,
            Storage.ParsedJson.Deserialize(ParsedJson), Status, DurationS);
    }
}
