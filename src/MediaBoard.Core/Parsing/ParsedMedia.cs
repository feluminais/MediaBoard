namespace MediaBoard.Core.Parsing;

/// <summary>What the folders and file name of one file say about it.</summary>
/// <param name="ShowFolder">Relative path of the show's root folder; null for movies and loose files.</param>
public sealed record ParsedMedia(
    MediaKind Kind,
    string Title,
    int? Year,
    int? Season,
    IReadOnlyList<int> Episodes,
    IReadOnlyList<string> EpisodeTitles,
    string? ShowFolder,
    int? TmdbId,
    string? ImdbId);
