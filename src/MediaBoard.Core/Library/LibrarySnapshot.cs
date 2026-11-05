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

    /// <summary>The same file for another reader: episodes and hide kept, no progress, so one viewer's can't reach another's.</summary>
    public FileEntry CopyWithoutProgress()
    {
        var copy = new FileEntry
        {
            Id = Id, FolderId = FolderId, RelPath = RelPath, Size = Size, Status = Status, FirstSeen = FirstSeen,
            Missing = Missing, TitleId = TitleId, Hidden = Hidden,
        };
        copy.Episodes.AddRange(Episodes);
        return copy;
    }
}

/// <summary>
/// What a snapshot holds that no viewer's own data changes: the folders, files with their episodes and hides, titles
/// and overrides. Read-only once built. Progress and the dismissed list are the viewer's and never come from here.
/// </summary>
internal sealed class LibraryBase
{
    private LibraryBase(
        IReadOnlyList<LibraryFolder> folders, List<FileEntry> files, Dictionary<int, TitleRow> titles,
        Dictionary<(TargetKind, int), OverrideRow> overrides)
    {
        Folders = folders;
        Files = files;
        Titles = titles;
        Overrides = overrides;
    }

    public IReadOnlyList<LibraryFolder> Folders { get; }

    public List<FileEntry> Files { get; }

    public Dictionary<int, TitleRow> Titles { get; }

    public Dictionary<(TargetKind, int), OverrideRow> Overrides { get; }

    public static LibraryBase Read(LibraryDatabase db)
    {
        using var connection = db.Open();
        var folders = connection.Query<LibrarySnapshot.FolderRowDto>("SELECT id, path, enabled FROM folders ORDER BY id")
            .Select(r => new LibraryFolder((int)r.Id, r.Path, r.Enabled != 0)).ToList();
        var overrides = new OverrideStore(db).All().ToDictionary(o => (o.Kind, o.TargetId));

        var files = connection.Query<LibrarySnapshot.FileDto>(
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
                FirstSeen = LibrarySnapshot.ParseTime(r.FirstSeen),
                Missing = r.Missing != 0,
                TitleId = (int?)r.TitleId,
            })
            .ToList();
        var titles = connection.Query<LibrarySnapshot.TitleDto>(
                """
                SELECT id, tmdb_id AS TmdbId, kind, name, year, end_year AS EndYear, overview, rating, poster_path AS PosterPath
                FROM titles
                """)
            .Select(r => new TitleRow(
                (int)r.Id, (int?)r.TmdbId, r.Kind == "show" ? TitleKind.Show : TitleKind.Movie, r.Name, (int?)r.Year,
                (int?)r.EndYear, r.Overview, r.Rating, r.PosterPath, null))
            .ToDictionary(t => t.Id);

        var byId = files.ToDictionary(f => f.Id);
        foreach (var row in connection.Query<LibrarySnapshot.FileEpisodeDto>(
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

        return new LibraryBase(folders, files, titles, overrides);
    }
}

/// <summary>
/// A <see cref="LibraryBase"/> kept between requests, for the sharing server, where five viewers ask for the same
/// 5,000 titles. It is dropped on every change the library reports, and after <see cref="MaxAge"/> for a write that
/// reported nothing.
/// </summary>
internal sealed class SharedLibraryBase(LibraryDatabase db, TimeProvider time)
{
    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly Lock _build = new();
    private LibraryBase? _kept;
    private long _builtAt;
    private int _version;

    public void Invalidate()
    {
        lock (_gate)
        {
            _version++;
            _kept = null;
        }
    }

    public LibraryBase Get()
    {
        if (Kept() is { } kept) return kept;

        // One build at a time: viewers arriving together wait for it instead of each reading the library.
        lock (_build)
        {
            if (Kept() is { } built) return built;

            int version;
            lock (_gate) version = _version;
            var fresh = LibraryBase.Read(db);
            lock (_gate)
            {
                // A change that landed during the read outdates it; the next request reads again.
                if (version == _version)
                {
                    _kept = fresh;
                    _builtAt = time.GetTimestamp();
                }
            }

            return fresh;
        }
    }

    private LibraryBase? Kept()
    {
        lock (_gate) return _kept is not null && time.GetElapsedTime(_builtAt) < MaxAge ? _kept : null;
    }
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
        Dictionary<int, DateTime> continueDismissed,
        bool seesHidden)
    {
        SeesHidden = seesHidden;
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

    /// <summary>False for a viewer who may not see hidden items: the files a hide covers are not in this snapshot at all.</summary>
    public bool SeesHidden { get; }

    public IReadOnlyList<LibraryFolder> Folders { get; }

    /// <summary>
    /// Every file that is media (not marked "not a movie or show"), linked to a title or not, in the folders that are
    /// switched on and the viewer may reach. A disabled folder's files are not here, so none of the queries see them; progress and edits stay
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

    /// <param name="shared">The base kept for the sharing server, or null to read it fresh.</param>
    public static LibrarySnapshot Load(LibraryDatabase db, Viewer viewer, SharedLibraryBase? shared = null)
    {
        var library = shared?.Get() ?? LibraryBase.Read(db);
        var allFolders = library.Folders;
        // Only the owner sees every folder, switched off ones included; a viewer's list holds what they can play.
        var folders = viewer.IsOwner ? allFolders : allFolders.Where(f => f.Enabled && viewer.CanReach(f.Id)).ToList();
        var overrides = library.Overrides;
        var titles = library.Titles;
        var files = library.Files.Where(f => viewer.CanReach(f.FolderId)).Select(f => f.CopyWithoutProgress()).ToList();

        var (fileCountByFolder, titleCountByFolder) = CountByFolder(files, titles);

        var disabled = allFolders.Where(f => !f.Enabled).Select(f => f.Id).ToHashSet();
        if (disabled.Count > 0) files = files.Where(f => !disabled.Contains(f.FolderId)).ToList();
        var byId = files.ToDictionary(f => f.Id);

        using var connection = db.Open();
        foreach (var row in connection.Query<ProgressDto>(
                     "SELECT file_id AS FileId, position_s AS PositionS, duration_s AS DurationS, watched, last_played AS LastPlayed " +
                     "FROM progress WHERE user_id = @UserId", new { viewer.UserId }))
        {
            if (!byId.TryGetValue((int)row.FileId, out var file)) continue;
            file.Progress = new Progress(
                file.Id, row.PositionS, row.DurationS, row.Watched != 0,
                row.LastPlayed is null ? DateTime.MinValue : ParseTime(row.LastPlayed));
        }

        if (!viewer.SeesHidden)
        {
            var folderHidden = folders.Where(f => overrides.TryGetValue((TargetKind.Folder, f.Id), out var o) && o.Hidden)
                .Select(f => f.Id).ToHashSet();
            // A hide at any level blocks the viewer, so the queries never see the file and agree with ViewerAccess.
            files = files.Where(f => !f.Hidden && !IsHiddenByTitleSeasonOrEpisode(f, overrides)).ToList();
            folders = folders.Where(f => !folderHidden.Contains(f.Id)).ToList();
            (fileCountByFolder, titleCountByFolder) = CountByFolder(files, titles);
        }

        var dismissed = connection.Query<DismissedDto>("SELECT title_id AS TitleId, played_at AS PlayedAt " +
                                                         "FROM continue_dismissed WHERE user_id = @UserId", new { viewer.UserId })
            .ToDictionary(r => (int)r.TitleId, r => ParseTime(r.PlayedAt));

        return new LibrarySnapshot(folders, files, fileCountByFolder, titleCountByFolder, titles, overrides, dismissed, viewer.SeesHidden);
    }

    private static bool IsHiddenByTitleSeasonOrEpisode(FileEntry file, Dictionary<(TargetKind, int), OverrideRow> overrides)
    {
        bool Hidden(TargetKind kind, int id) => overrides.TryGetValue((kind, id), out var row) && row.Hidden;

        if (file.TitleId is { } titleId && Hidden(TargetKind.Title, titleId)) return true;
        foreach (var episode in file.Episodes)
        {
            if (Hidden(TargetKind.Episode, episode.EpisodeId)) return true;
            if (file.TitleId is { } id && LibraryIds.TrySeason(id, episode.Season) is { } season && Hidden(TargetKind.Season, season)) return true;
        }

        return false;
    }

    private static (Dictionary<int, int> Files, Dictionary<int, (int Shows, int Movies)> Titles) CountByFolder(
        List<FileEntry> files, Dictionary<int, TitleRow> titles)
    {
        var fileCountByFolder = files.GroupBy(f => f.FolderId).ToDictionary(g => g.Key, g => g.Count());
        var titleCountByFolder = new Dictionary<int, (int Shows, int Movies)>();
        foreach (var group in files.Where(f => f.TitleId is { } id && titles.ContainsKey(id)).GroupBy(f => f.FolderId))
        {
            var kinds = group.Select(f => f.TitleId!.Value).Distinct().Select(id => titles[id].Kind).ToList();
            titleCountByFolder[group.Key] = (kinds.Count(k => k == TitleKind.Show), kinds.Count(k => k == TitleKind.Movie));
        }

        return (fileCountByFolder, titleCountByFolder);
    }

    internal static DateTime ParseTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal sealed class FolderRowDto
    {
        public long Id { get; set; }
        public string Path { get; set; } = "";
        public long Enabled { get; set; }
    }

    internal sealed class FileDto
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

    internal sealed class FileEpisodeDto
    {
        public long FileId { get; set; }
        public long EpisodeId { get; set; }
        public long Season { get; set; }
        public long Number { get; set; }
        public string? Name { get; set; }
    }

    internal sealed class TitleDto
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
