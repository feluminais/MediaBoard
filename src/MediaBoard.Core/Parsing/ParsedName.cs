namespace MediaBoard.Core.Parsing;

/// <summary>Fields read from a release-style file or folder name.</summary>
/// <param name="LeadingNumber">A bare number at the start or end of the name ("01. Title", "Title_01_").
/// The caller decides whether it is an episode number.</param>
public sealed record ParsedName(
    string Title,
    int? Year,
    int? Season,
    IReadOnlyList<int> Episodes,
    IReadOnlyList<string> EpisodeTitles,
    int? LeadingNumber,
    int? TmdbId,
    string? ImdbId);
