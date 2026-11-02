namespace MediaBoard.Core.Metadata;

public enum TitleKind { Movie, Show }

public sealed record SearchCandidate(
    int TmdbId,
    TitleKind Kind,
    string Name,
    string? OriginalName,
    int? Year,
    double Popularity,
    double? Rating,
    string? PosterPath,
    string? Overview,
    int VoteCount = 0);

public sealed record TitleDetails(
    int TmdbId,
    TitleKind Kind,
    string Name,
    int? Year,
    int? EndYear,
    string? Overview,
    double? Rating,
    string? PosterPath,
    int? RuntimeMin,
    IReadOnlyList<int> SeasonNumbers);

public sealed record SeasonDetails(int Season, IReadOnlyList<EpisodeDetails> Episodes);

/// <param name="Rating">TMDB viewers' average, or null while nobody voted on the episode.</param>
/// <param name="StillPath">A TMDB image path for the episode's still frame.</param>
public sealed record EpisodeDetails(
    int Season,
    int Number,
    string Name,
    int? RuntimeMin,
    DateOnly? AirDate,
    string? Overview = null,
    double? Rating = null,
    int VoteCount = 0,
    string? StillPath = null);
