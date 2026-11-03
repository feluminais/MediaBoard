using System.Globalization;
using Dapper;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Library;

internal readonly record struct EpisodeRef(int EpisodeId, int Season, int Number, string? Name)
{
    /// <summary>Orders episodes by season and number, with specials (season 0) after the regular seasons.</summary>
    public long Key => EpisodeKey(Season, Number);

    public static long EpisodeKey(int season, int number) => (season == 0 ? 1_000L : season) * 100_000L + number;
}

internal sealed class FileEntry
{
    public required int Id { get; init; }
    public required int FolderId { get; init; }
    public required string RelPath { get; init; }
    public required long Size { get; init; }
    public required string Status { get; init; }
    public required DateTime FirstSeen { get; init; }
    public required bool Missing { get; init; }
    public int? TitleId { get; init; }

    public Progress? Progress { get; set; }

    public List<EpisodeRef> Episodes { get; } = [];

    /// <summary>The file itself or its folder is hidden.</summary>
    public bool Hidden { get; set; }

    public bool Watched => Progress is { Watched: true };

    /// <summary>Played, not finished, and past the first second (a row left by a player that opened and closed at once is not).</summary>
    public bool InProgress => Progress is { Watched: false, PositionS: > LibrarySnapshot.MinProgressSeconds };

    public bool WasPlayed => InProgress || Watched;

    public string Name => Path.GetFileNameWithoutExtension(RelPath);

    public long FirstKey => Episodes.Count == 0 ? long.MaxValue : Episodes.Min(e => e.Key);

    public long LastKey => Episodes.Count == 0 ? long.MaxValue : Episodes.Max(e => e.Key);

    public bool OnlySpecials => Episodes.Count > 0 && Episodes.All(e => e.Season == 0);
}

/// <summary>
/// The library as it is at one moment: folders, files with their progress and episodes, titles and overrides,
/// read in a handful of queries (never one per row) and shared by the query methods.
/// </summary>
internal sealed class LibrarySnapshot
{
    internal const double MinProgressSeconds = 1.0;

    private readonly Dictionary<(TargetKind Kind, int Id), OverrideRow> _overrides;

    private LibrarySnapshot(
        IReadOnlyList<LibraryFolder> folders,
        List<FileEntry> files,
        Dictionary<int, int> fileCountByFolder,
        Dictionary<int, (int Shows, int Movies)> titleCountByFolder,
        Dictionary<int, TitleRow> titles,
        Dictionary<(TargetKind, int), OverrideRow> overrides,
        Dictionary<int, DateTime> continueDismissed)
    {
        ContinueDismissed = continueDismissed;
        Folders = folders;
        Files = files;
        FileCountByFolder = fileCountByFolder;
        TitleCountByFolder = titleCountByFolder;
        _overrides = overrides;
        FilesById = files.ToDictionary(f => f.Id);

        var byTitle = new Dictionary<int, List<FileEntry>>();
        foreach (var file in files)
        {
            if (file.TitleId is not { } titleId || !titles.ContainsKey(titleId)) continue;
            if (!byTitle.TryGetValue(titleId, out var list)) byTitle[titleId] = list = [];
            list.Add(file);
        }

        FilesByTitle = byTitle;
        Titles = titles.Where(t => byTitle.ContainsKey(t.Key)).ToDictionary(t => t.Key, t => t.Value);
    }

    public IReadOnlyList<LibraryFolder> Folders { get; }

    /// <summary>
    /// Every file that is media (not marked "not a movie or show"), linked to a title or not, in the folders that are
    /// switched on. A disabled folder's files are not here, so none of the queries see them; progress and edits stay
    /// in the database and everything is back when the folder is enabled.
    /// </summary>
    public List<FileEntry> Files { get; }

    /// <summary>Media files per folder, disabled folders included (the Folders page still counts them).</summary>
    public Dictionary<int, int> FileCountByFolder { get; }

    /// <summary>Distinct shows and movies the media files of each folder belong to, disabled folders included.</summary>
    public Dictionary<int, (int Shows, int Movies)> TitleCountByFolder { get; }

    public Dictionary<int, FileEntry> FilesById { get; }

    /// <summary>Only titles that have at least one file.</summary>
    public Dictionary<int, TitleRow> Titles { get; }

    public Dictionary<int, List<FileEntry>> FilesByTitle { get; }

    /// <summary>Titles removed from Continue watching, with the last_played they showed then.</summary>
    public Dictionary<int, DateTime> ContinueDismissed { get; }

    public string? NameOverride(TargetKind kind, int id) =>
        _overrides.TryGetValue((kind, id), out var row) ? row.DisplayName : null;

    public bool IsHidden(TargetKind kind, int id) => _overrides.TryGetValue((kind, id), out var row) && row.Hidden;

    /// <summary>Display name precedence: the user's name, then TMDB's, then the name made from the file (provisional titles).</summary>
    public string TitleName(TitleRow title) => NameOverride(TargetKind.Title, title.Id) ?? title.Name;

    /// <summary>Hidden itself, or every file of it is hidden (its folder is, or the file is).</summary>
    public bool IsTitleHidden(int titleId) =>
        IsHidden(TargetKind.Title, titleId) || FilesByTitle[titleId].All(f => f.Hidden);

    public static LibrarySnapshot Load(LibraryDatabase db)
    {
        using var connection = db.Open();
        var folders = connection.Query<FolderRowDto>("SELECT id, path, enabled FROM folders ORDER BY id")
            .Select(r => new LibraryFolder((int)r.Id, r.Path, r.Enabled != 0)).ToList();
        var overrides = new OverrideStore(db).All().ToDictionary(o => (o.Kind, o.TargetId));

        var files = connection.Query<FileDto>(
                """
                SELECT f.id, f.folder_id AS FolderId, f.rel_path AS RelPath, f.size, f.first_seen AS FirstSeen,
                       f.missing, f.status, ft.title_id AS TitleId
                FROM files f LEFT JOIN file_titles ft ON ft.file_id = f.id
                WHERE f.status <> 'not_media'
                """)
            .Select(r => new FileEntry
            {
                Id = (int)r.Id,
                FolderId = (int)r.FolderId,
                RelPath = r.RelPath,
                Size = r.Size,
                Status = r.Status,
                FirstSeen = ParseTime(r.FirstSeen),
                Missing = r.Missing != 0,
                TitleId = (int?)r.TitleId,
            })
            .ToList();
        var titles = connection.Query<TitleDto>(
                """
                SELECT id, tmdb_id AS TmdbId, kind, name, year, end_year AS EndYear, overview, rating, poster_path AS PosterPath
                FROM titles
                """)
            .Select(r => new TitleRow(
                (int)r.Id, (int?)r.TmdbId, r.Kind == "show" ? TitleKind.Show : TitleKind.Movie, r.Name, (int?)r.Year,
                (int?)r.EndYear, r.Overview, r.Rating, r.PosterPath, null))
            .ToDictionary(t => t.Id);

        var fileCountByFolder = files.GroupBy(f => f.FolderId).ToDictionary(g => g.Key, g => g.Count());
        var titleCountByFolder = new Dictionary<int, (int Shows, int Movies)>();
        foreach (var group in files.Where(f => f.TitleId is { } id && titles.ContainsKey(id)).GroupBy(f => f.FolderId))
        {
            var kinds = group.Select(f => f.TitleId!.Value).Distinct().Select(id => titles[id].Kind).ToList();
            titleCountByFolder[group.Key] = (kinds.Count(k => k == TitleKind.Show), kinds.Count(k => k == TitleKind.Movie));
        }

        var disabled = folders.Where(f => !f.Enabled).Select(f => f.Id).ToHashSet();
        if (disabled.Count > 0) files = files.Where(f => !disabled.Contains(f.FolderId)).ToList();
        var byId = files.ToDictionary(f => f.Id);

        foreach (var row in connection.Query<ProgressDto>(
                     "SELECT file_id AS FileId, position_s AS PositionS, duration_s AS DurationS, watched, last_played AS LastPlayed FROM progress"))
        {
            if (!byId.TryGetValue((int)row.FileId, out var file)) continue;
            file.Progress = new Progress(
                file.Id, row.PositionS, row.DurationS, row.Watched != 0,
                row.LastPlayed is null ? DateTime.MinValue : ParseTime(row.LastPlayed));
        }

        foreach (var row in connection.Query<FileEpisodeDto>(
                     """
                     SELECT fe.file_id AS FileId, e.id AS EpisodeId, e.season, e.number, e.name
                     FROM file_episodes fe JOIN episodes e ON e.id = fe.episode_id
                     """))
        {
            if (byId.TryGetValue((int)row.FileId, out var file))
                file.Episodes.Add(new EpisodeRef((int)row.EpisodeId, (int)row.Season, (int)row.Number, row.Name));
        }

        var folderHidden = folders.Where(f => overrides.TryGetValue((TargetKind.Folder, f.Id), out var o) && o.Hidden)
            .Select(f => f.Id).ToHashSet();
        foreach (var file in files)
        {
            file.Hidden = folderHidden.Contains(file.FolderId)
                          || (overrides.TryGetValue((TargetKind.File, file.Id), out var o) && o.Hidden);
        }

        var dismissed = connection.Query<DismissedDto>("SELECT title_id AS TitleId, played_at AS PlayedAt FROM continue_dismissed")
            .ToDictionary(r => (int)r.TitleId, r => ParseTime(r.PlayedAt));

        return new LibrarySnapshot(folders, files, fileCountByFolder, titleCountByFolder, titles, overrides, dismissed);
    }

    private static DateTime ParseTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class FolderRowDto
    {
        public long Id { get; set; }
        public string Path { get; set; } = "";
        public long Enabled { get; set; }
    }

    private sealed class FileDto
    {
        public long Id { get; set; }
        public long FolderId { get; set; }
        public string RelPath { get; set; } = "";
        public long Size { get; set; }
        public string FirstSeen { get; set; } = "";
        public long Missing { get; set; }
        public string Status { get; set; } = "";
        public long? TitleId { get; set; }
    }

    private sealed class ProgressDto
    {
        public long FileId { get; set; }
        public double PositionS { get; set; }
        public double DurationS { get; set; }
        public long Watched { get; set; }
        public string? LastPlayed { get; set; }
    }

    private sealed class DismissedDto
    {
        public long TitleId { get; set; }
        public string PlayedAt { get; set; } = "";
    }

    private sealed class FileEpisodeDto
    {
        public long FileId { get; set; }
        public long EpisodeId { get; set; }
        public long Season { get; set; }
        public long Number { get; set; }
        public string? Name { get; set; }
    }

    private sealed class TitleDto
    {
        public long Id { get; set; }
        public long? TmdbId { get; set; }
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public long? Year { get; set; }
        public long? EndYear { get; set; }
        public string? Overview { get; set; }
        public double? Rating { get; set; }
        public string? PosterPath { get; set; }
    }
}
