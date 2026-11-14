namespace CozyCouch.Core.Parsing;

/// <summary>Fields read from a release-style file or folder name.</summary>
/// <param name="LeadingNumber">A bare number at the start or end of the name ("01. Title", "Title_01_").
/// The caller decides whether it is an episode number.</param>
/// <param name="YearAfterMarker">The year follows the episode marker ("S01E01 (2019)", "E01. Title (2002)"), so it
/// dates the episode; a year before the marker ("Show.2023.S02E01") names the show.</param>
public sealed record ParsedName(
    string Title,
    int? Year,
    int? Season,
    IReadOnlyList<int> Episodes,
    IReadOnlyList<string> EpisodeTitles,
    int? LeadingNumber,
    int? TmdbId,
    string? ImdbId,
    bool YearAfterMarker = false);
