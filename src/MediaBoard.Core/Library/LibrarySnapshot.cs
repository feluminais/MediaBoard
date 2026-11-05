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
/// The library as it is at one moment: folders, files with their progress and episodes, titles and overrides,
/// read in a handful of queries (never one per row) and shared by the query methods.
/// </summary>
internal sealed class LibrarySnapshot
{
    internal const double MinProgressSeconds = 1.0;

    private readonly Dictionary<(TargetKind Kind, int Id), OverrideRow> _overrides;

    private LibrarySnapshot(
        LibraryBase library,
        IReadOnlyList<LibraryFolder> folders,
        List<FileEntry> files,
        Dictionary<int, int> fileCountByFolder,
        Dictionary<int, (int Shows, int Movies)> titleCountByFolder,
        Dictionary<int, TitleRow> titles,
        Dictionary<(TargetKind, int), OverrideRow> overrides,
        Dictionary<int, DateTime> continueDismissed,
        bool seesHidden)
    {
        Library = library;
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

    /// <summary>The base this snapshot was built from, so a cache made from it can tell when that base is gone.</summary>
    public LibraryBase Library { get; }

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

        return new LibrarySnapshot(library, folders, files, fileCountByFolder, titleCountByFolder, titles, overrides, dismissed, viewer.SeesHidden);
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

}
