using Dapper;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Parsing;
using MediaBoard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Scanning;

/// <summary>
/// Walks a library folder and brings the files table in line with it: adds new videos, re-parses changed
/// ones, marks vanished ones missing, restores returned ones and relinks moved ones. It only reads the
/// library folder; every write goes to the database.
/// </summary>
public sealed class LibraryScanner(LibraryDatabase db, ActivityHub hub, TimeProvider? timeProvider = null)
{
    private const int ProgressStep = 25;

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private static readonly EnumerationOptions TopLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _unavailableGate = new();
    private readonly Dictionary<int, string> _unavailable = [];

    /// <summary>The folders whose last scan found them missing or unreadable.</summary>
    public IReadOnlyList<int> UnavailableFolderIds()
    {
        lock (_unavailableGate) return _unavailable.Keys.ToList();
    }

    public Task<ScanResult> ScanAsync(LibraryFolder folder, CancellationToken ct) =>
        Task.Run(() => Scan(folder, ct), ct);

    /// <summary>
    /// Drops what the scanner remembers about a folder that was removed or switched off, so the status bar stops
    /// listing it as unavailable.
    /// </summary>
    public void Forget(int folderId)
    {
        bool changed;
        lock (_unavailableGate) changed = _unavailable.Remove(folderId);
        if (changed) PublishUnavailable();
    }

    private ScanResult Scan(LibraryFolder folder, CancellationToken ct)
    {
        var name = DisplayName(folder.Path);
        try
        {
            // Inside the try: a status subscriber that throws here must not leave the folder "scanning" for good.
            hub.BeginScan(folder.Id, name);
            var listing = Enumerate(folder.Path, ct);
            if (listing is null) return MarkUnavailable(folder, name);

            SetAvailable(folder.Id);
            return Reconcile(folder, listing, ct);
        }
        finally
        {
            hub.EndScan(folder.Id);
        }
    }

    private static Listing? Enumerate(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return null;

        try
        {
            var directory = new DirectoryInfo(root);
            var videos = new List<DiskFile>();
            var foldersWithVideo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in directory.EnumerateFiles("*", Recursive))
            {
                ct.ThrowIfCancellationRequested();
                if (!MediaFileRules.IsVideo(file.Name)) continue;

                var relPath = Path.GetRelativePath(root, file.FullName);
                var separator = relPath.IndexOf(Path.DirectorySeparatorChar);
                if (separator > 0) foldersWithVideo.Add(relPath[..separator]);

                if (MediaFileRules.IsIgnored(relPath, file.Length)) continue;
                var mtime = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
                videos.Add(new DiskFile(relPath, file.Name, file.FullName, file.Length, mtime));
            }

            var ignored = directory.EnumerateDirectories("*", TopLevel)
                .Select(d => d.Name)
                .Where(n => !n.StartsWith('.') && !foldersWithVideo.Contains(n))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new Listing(videos, ignored);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private ScanResult Reconcile(LibraryFolder folder, Listing listing, CancellationToken ct)
    {
        var videos = listing.Videos;
        using var connection = db.Open();
        var rows = connection.Query<ExistingRow>(
            "SELECT id, rel_path AS RelPath, size, mtime, missing, parsed_json AS ParsedJson FROM files WHERE folder_id = @id",
            new { id = folder.Id }).ToList();
        var byPath = new Dictionary<string, ExistingRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) byPath.TryAdd(row.RelPath, row);

        var siblings = videos
            .GroupBy(v => Path.GetDirectoryName(v.RelPath) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(v => v.Name).ToList(), StringComparer.OrdinalIgnoreCase);
        ParsedMedia Parse(DiskFile file) => PathParser.Parse(
            file.RelPath,
            siblings[Path.GetDirectoryName(file.RelPath) ?? ""].Where(n => !n.Equals(file.Name, StringComparison.OrdinalIgnoreCase)).ToList());

        var seen = new HashSet<long>();
        var added = new List<DiskFile>();
        var locked = new List<string>();
        var lockedNew = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fresh = new HashSet<long>();
        int changed = 0, restored = 0, markedMissing = 0, relinked = 0;

        using var transaction = connection.BeginTransaction();
        hub.ReportScan(folder.Id, 0, videos.Count);
        var done = 0;
        foreach (var file in videos)
        {
            ct.ThrowIfCancellationRequested();
            if (byPath.TryGetValue(file.RelPath, out var row))
            {
                seen.Add(row.Id);
                var isChanged = row.Size != file.Size || row.Mtime != file.Mtime;
                if (isChanged && IsLocked(file.FullPath))
                {
                    locked.Add(file.RelPath);
                }
                else if (isChanged)
                {
                    var json = ParsedJson.Serialize(Parse(file));
                    UpdateFile(connection, transaction, row.Id, file.RelPath, file, json, json != row.ParsedJson);
                    fresh.Add(row.Id);
                    changed++;
                    if (row.Missing != 0) restored++;
                }
                else if (row.Missing != 0)
                {
                    Execute(connection, transaction, "UPDATE files SET missing = 0 WHERE id = @id", new { id = row.Id });
                    restored++;
                }
            }
            else if (IsLocked(file.FullPath))
            {
                locked.Add(file.RelPath);
                lockedNew.Add(file.RelPath);
            }
            else
            {
                added.Add(file);
            }

            done++;
            if (done % ProgressStep == 0 || done == videos.Count) hub.ReportScan(folder.Id, done, videos.Count);
        }

        // Rows the walk did not reach: a moved file shows up as one of these plus one new file.
        var gone = rows.Where(r => !seen.Contains(r.Id)).ToList();
        var goneByKey = gone.GroupBy(r => RelinkKey(Path.GetFileName(r.RelPath), r.Size))
            .ToDictionary(g => g.Key, g => g.ToList());
        var addedByKey = added.GroupBy(a => RelinkKey(a.Name, a.Size))
            .ToDictionary(g => g.Key, g => g.ToList());
        var relinkedIds = new HashSet<long>();
        var inserts = new List<DiskFile>();
        foreach (var file in added)
        {
            var key = RelinkKey(file.Name, file.Size);
            if (goneByKey.TryGetValue(key, out var oldRows) && oldRows.Count == 1 && addedByKey[key].Count == 1)
            {
                var old = oldRows[0];
                var json = ParsedJson.Serialize(Parse(file));
                UpdateFile(connection, transaction, old.Id, file.RelPath, file, json, json != old.ParsedJson);
                relinkedIds.Add(old.Id);
                relinked++;
                hub.Log(ActivityLevel.Info, $"Relinked: {old.RelPath} → {file.RelPath}");
            }
            else
            {
                inserts.Add(file);
            }
        }

        var now = FileStore.FormatTime(_time.GetUtcNow());
        foreach (var file in inserts)
        {
            Execute(connection, transaction,
                """
                INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status)
                VALUES (@folderId, @relPath, @size, @mtime, @now, 0, @json, 'pending')
                ON CONFLICT(folder_id, rel_path) DO UPDATE SET
                    size = excluded.size, mtime = excluded.mtime, missing = 0, parsed_json = excluded.parsed_json
                """,
                new { folderId = folder.Id, relPath = file.RelPath, size = file.Size, mtime = file.Mtime, now, json = ParsedJson.Serialize(Parse(file)) });
        }

        foreach (var row in gone.Where(r => r.Missing == 0 && !relinkedIds.Contains(r.Id)))
        {
            Execute(connection, transaction, "UPDATE files SET missing = 1 WHERE id = @id", new { id = row.Id });
            markedMissing++;
        }

        changed += ReparseChangedDirectories(connection, transaction, videos, rows, byPath, lockedNew, fresh, Parse);

        transaction.Commit();
        return new ScanResult(inserts.Count, changed, markedMissing, restored, relinked, listing.IgnoredFolders, locked, false);
    }

    /// <summary>
    /// A file's parse depends on its siblings (a bare "Show_01" is an episode only when other numbered files sit
    /// next to it), so when a directory's set of video names changed, every file in it is parsed again.
    /// Only a different result is written. The status goes back to pending unless the user matched the file or marked it not a movie or show.
    /// </summary>
    private static int ReparseChangedDirectories(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<DiskFile> videos,
        List<ExistingRow> rows,
        Dictionary<string, ExistingRow> byPath,
        HashSet<string> lockedNew,
        HashSet<long> fresh,
        Func<DiskFile, ParsedMedia> parse)
    {
        static string Dir(string relPath) => Path.GetDirectoryName(relPath) ?? "";
        static Dictionary<string, HashSet<string>> NameSets(IEnumerable<(string RelPath, string Name)> files) =>
            files.GroupBy(f => Dir(f.RelPath), StringComparer.OrdinalIgnoreCase).ToDictionary(
                g => g.Key,
                g => g.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var stored = NameSets(rows.Where(r => r.Missing == 0).Select(r => (r.RelPath, Path.GetFileName(r.RelPath))));
        var current = NameSets(videos.Where(v => !lockedNew.Contains(v.RelPath)).Select(v => (v.RelPath, v.Name)));
        var changedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, names) in current)
            if (!stored.TryGetValue(dir, out var before) || !before.SetEquals(names)) changedDirs.Add(dir);
        foreach (var dir in stored.Keys)
            if (!current.ContainsKey(dir)) changedDirs.Add(dir);
        if (changedDirs.Count == 0) return 0;

        var updated = 0;
        foreach (var file in videos)
        {
            if (!changedDirs.Contains(Dir(file.RelPath))
                || !byPath.TryGetValue(file.RelPath, out var row)
                || fresh.Contains(row.Id))
                continue;

            var json = ParsedJson.Serialize(parse(file));
            if (json == row.ParsedJson) continue;

            Execute(connection, transaction,
                """
                UPDATE files SET parsed_json = @json,
                                 status = CASE WHEN status = 'not_media'
                                                 OR EXISTS (SELECT 1 FROM file_titles WHERE file_id = files.id AND source = 'user')
                                               THEN status ELSE 'pending' END
                WHERE id = @id
                """,
                new { id = row.Id, json });
            updated++;
        }

        return updated;
    }

    private ScanResult MarkUnavailable(LibraryFolder folder, string name)
    {
        int marked;
        using (var connection = db.Open())
            marked = connection.Execute("UPDATE files SET missing = 1 WHERE folder_id = @id AND missing = 0", new { id = folder.Id });

        bool changed;
        lock (_unavailableGate) changed = _unavailable.TryAdd(folder.Id, name);
        if (changed)
        {
            PublishUnavailable();
            hub.Log(ActivityLevel.Warning, $"Folder unavailable: {name}", "Check that the drive is connected.");
        }

        return new ScanResult(0, 0, marked, 0, 0, [], [], true);
    }

    private void SetAvailable(int folderId)
    {
        bool changed;
        lock (_unavailableGate) changed = _unavailable.Remove(folderId);
        if (changed) PublishUnavailable();
    }

    private void PublishUnavailable()
    {
        string[] names;
        lock (_unavailableGate)
            names = _unavailable.Values.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        hub.SetUnavailableFolders(names);
    }

    private static void UpdateFile(
        SqliteConnection connection, SqliteTransaction transaction, long id, string relPath, DiskFile file, string json, bool parseChanged) =>
        Execute(connection, transaction,
            """
            UPDATE files SET rel_path = @relPath, size = @size, mtime = @mtime, missing = 0, parsed_json = @json,
                             status = CASE WHEN @reset = 1 AND status <> 'not_media' THEN 'pending' ELSE status END
            WHERE id = @id
            """,
            new { id, relPath, size = file.Size, mtime = file.Mtime, json, reset = parseChanged ? 1 : 0 });

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, object args) =>
        connection.Execute(sql, args, transaction);

    private static string RelinkKey(string fileName, long size) => $"{size}|{fileName.ToUpperInvariant()}";

    /// <summary>A download in progress holds its file open without sharing, so opening it for reading fails.</summary>
    private static bool IsLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path;
    }

    private sealed record DiskFile(string RelPath, string Name, string FullPath, long Size, long Mtime);

    private sealed record Listing(List<DiskFile> Videos, List<string> IgnoredFolders);

    private sealed class ExistingRow
    {
        public long Id { get; set; }
        public string RelPath { get; set; } = "";
        public long Size { get; set; }
        public long Mtime { get; set; }
        public long Missing { get; set; }
        public string ParsedJson { get; set; } = "";
    }
}
