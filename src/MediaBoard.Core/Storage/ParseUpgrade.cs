using System.Globalization;
using Dapper;
using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Storage;

/// <summary>
/// Brings stored parses up to <see cref="PathParser.Version"/>. A rescan re-parses only files whose folder
/// changed, so when the parser starts reading names differently every file is parsed again here, once. Each
/// file identification decided on its own is queued again (status pending), since the old parse may have
/// sent it to review or to the wrong title; a file the user matched, a file a user's rule matched, and a
/// file marked as not a movie or show keep their status.
/// </summary>
internal static class ParseUpgrade
{
    private const string VersionKey = "parser_version";

    private sealed record Row(long Id, long FolderId, string RelPath, long Missing, string ParsedJson);

    /// <summary>Does nothing when the stored parses are current. Returns how many files were queued again.</summary>
    public static int Run(LibraryDatabase db)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var stored = connection.ExecuteScalar<string?>(
            "SELECT value FROM meta WHERE key = @key", new { key = VersionKey }, transaction);
        if (int.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= PathParser.Version)
            return 0;

        var rows = connection.Query<Row>(
            "SELECT id, folder_id AS FolderId, rel_path AS RelPath, missing, parsed_json AS ParsedJson FROM files",
            transaction: transaction).ToList();

        // The scanner passes the other video files of the same folder; the rows that are still on disk are those.
        static string Dir(string relPath) => Path.GetDirectoryName(relPath) ?? "";
        var siblings = rows.Where(r => r.Missing == 0)
            .GroupBy(r => (r.FolderId, Dir(r.RelPath).ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.Select(r => Path.GetFileName(r.RelPath)).ToList());

        foreach (var row in rows)
        {
            var names = siblings.GetValueOrDefault((row.FolderId, Dir(row.RelPath).ToUpperInvariant())) ?? [];
            var name = Path.GetFileName(row.RelPath);
            var json = ParsedJson.Serialize(PathParser.Parse(
                row.RelPath, names.Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList()));
            if (json != row.ParsedJson)
                connection.Execute("UPDATE files SET parsed_json = @json WHERE id = @id", new { json, id = row.Id }, transaction);
        }

        var queued = connection.Execute(
            """
            UPDATE files SET status = 'pending'
            WHERE status IN ('matched', 'review', 'offline')
              AND NOT EXISTS (SELECT 1 FROM file_titles WHERE file_id = files.id AND source IN ('user', 'rule'))
            """,
            transaction: transaction);

        connection.Execute(
            "INSERT OR REPLACE INTO meta(key, value) VALUES (@key, @value)",
            new { key = VersionKey, value = PathParser.Version.ToString(CultureInfo.InvariantCulture) },
            transaction);
        transaction.Commit();
        return queued;
    }
}
