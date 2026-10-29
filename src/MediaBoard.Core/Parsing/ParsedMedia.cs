namespace MediaBoard.Core.Parsing;

/// <summary>What the folders and file name of one file say about it.</summary>
/// <param name="Year">A movie's release year; for an episode, the year the show started, read from a folder name.</param>
/// <param name="ShowFolder">Relative path of the show's root folder; null for movies and loose files.</param>
/// <param name="AirYear">An episode's year that is not the show's start: from the file name, or from a folder that
/// names a later season. The show started that year or earlier. Null for movies.</param>
public sealed record ParsedMedia(
    MediaKind Kind,
    string Title,
    int? Year,
    int? Season,
    IReadOnlyList<int> Episodes,
    IReadOnlyList<string> EpisodeTitles,
    string? ShowFolder,
    int? TmdbId,
    string? ImdbId,
    int? AirYear = null);
