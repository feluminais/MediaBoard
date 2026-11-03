using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Library;

/// <summary>A poster-grid entry.</summary>
/// <param name="PosterPath">The TMDB poster path; the UI resolves it through the image cache. Null for a title TMDB does not know.</param>
/// <param name="Offline">The title has no TMDB match (yet): it is named after the file and shows as "Not identified (offline)".</param>
/// <param name="Missing">Every file of the title is missing from disk.</param>
/// <param name="DateAdded">When the most recently added file of the title was first seen, in UTC.</param>
public sealed record TitleCard(
    int TitleId,
    TitleKind Kind,
    string Name,
    int? Year,
    int? EndYear,
    string? PosterPath,
    double? Rating,
    bool Offline,
    bool Hidden,
    bool Missing,
    DateTime DateAdded);

/// <summary>A Continue watching entry: a file left half-way, or the episode that follows a finished one.</summary>
/// <param name="EpisodeLabel">"S01E04", or "S01E01-E02" for a file that holds several; null for a movie or a file with no episode.</param>
/// <param name="IsNext">The file has not been started: it follows an episode that was finished.</param>
/// <param name="LastPlayed">When the title was last played, in UTC.</param>
public sealed record ContinueItem(
    int TitleId,
    int FileId,
    TitleKind Kind,
    string TitleName,
    string? EpisodeLabel,
    string? EpisodeName,
    string? PosterPath,
    double PositionS,
    double DurationS,
    bool IsNext,
    bool Missing,
    DateTime LastPlayed);

public sealed record TitleDetailsView(
    int TitleId,
    TitleKind Kind,
    string Name,
    int? TmdbId,
    int? Year,
    int? EndYear,
    string? Overview,
    double? Rating,
    string? PosterPath,
    bool Offline,
    bool Hidden,
    bool Missing,
    IReadOnlyList<SeasonView> Seasons,
    IReadOnlyList<FileView> Files);

/// <param name="Id">The target id for <see cref="TargetKind.Season"/> edits.</param>
public sealed record SeasonView(int Id, int Number, string Name, bool Hidden, IReadOnlyList<EpisodeRow> Episodes);

/// <param name="EpisodeId">The target id for <see cref="TargetKind.Episode"/> edits.</param>
/// <param name="FileId">The file that holds the episode; null when the library has no file for it.</param>
/// <param name="Watched">A file that holds the episode is watched (one file can hold several episodes).</param>
public sealed record EpisodeRow(
    int EpisodeId,
    int Season,
    int Number,
    string Name,
    string? AirDate,
    int? RuntimeMin,
    bool Hidden,
    int? FileId,
    bool Watched,
    double PositionS,
    double DurationS,
    bool Missing);

/// <summary>A file of the title that is not linked to an episode: a movie, or an episode nobody could place.</summary>
public sealed record FileView(
    int FileId,
    string Name,
    string RelPath,
    bool Hidden,
    bool Watched,
    double PositionS,
    double DurationS,
    bool Missing);

public sealed record ReviewCandidate(int TmdbId, TitleKind Kind, string Name, int? Year);

/// <param name="Candidates">The best TMDB matches found for the file, best first.</param>
/// <param name="ShowFolder">Relative path of the show root folder for an episode inside one; null otherwise. A pick for one file covers the others of that folder.</param>
public sealed record ReviewItem(
    int FileId,
    string FileName,
    string RelPath,
    string ParsedTitle,
    int? Year,
    MediaKind Kind,
    int? Season,
    IReadOnlyList<int> Episodes,
    bool Missing,
    IReadOnlyList<ReviewCandidate> Candidates,
    string? ShowFolder = null);

/// <param name="Unavailable">The folder could not be read (a drive that is not connected).</param>
/// <param name="IgnoredFolders">Top-level folders with no video below them ("Ignored: no video"), as relative paths.</param>
/// <param name="ShowCount">Shows the folder's files belong to; a show spread over several folders counts in each.</param>
/// <param name="MovieCount">Movies the folder's files belong to.</param>
public sealed record FolderRow(
    int Id,
    string Path,
    bool Enabled,
    bool Hidden,
    bool Unavailable,
    int FileCount,
    IReadOnlyList<string> IgnoredFolders,
    int ShowCount = 0,
    int MovieCount = 0);

/// <param name="MayDeletePermanently">
/// The file is not on a fixed local disk (network or removable drive), where Windows may delete for good instead of
/// using the Recycle Bin. The confirmation should say so.
/// </param>
public sealed record DeleteFile(int FileId, string Path, long Size, bool MayDeletePermanently = false);

public sealed record DeletePreview(int TitleId, string TitleName, IReadOnlyList<DeleteFile> Files)
{
    public long TotalSize => Files.Sum(f => f.Size);

    public bool AnyMayDeletePermanently => Files.Any(f => f.MayDeletePermanently);
}

/// <param name="Declined">Windows asked whether to delete the file for good (no Recycle Bin for it) and the answer was no.</param>
public sealed record DeleteFailure(string Path, string Reason, bool Declined = false);

/// <param name="Deleted">Paths sent to the Recycle Bin. Their rows are removed from the library at once.</param>
/// <param name="Failed">Files that stayed where they were, and why. Their rows stay in the library.</param>
public sealed record DeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<DeleteFailure> Failed);

/// <summary>Overrides point at one id, so a season (which has no row of its own) gets one made of its title and number.</summary>
public static class LibraryIds
{
    private const int SeasonsPerTitle = 1000;

    public static int Season(int titleId, int season)
    {
        if (season is < 0 or >= SeasonsPerTitle) throw new ArgumentOutOfRangeException(nameof(season));
        return checked(titleId * SeasonsPerTitle + season);
    }

    internal static (int TitleId, int Season) SplitSeason(int id) => (id / SeasonsPerTitle, id % SeasonsPerTitle);
}
