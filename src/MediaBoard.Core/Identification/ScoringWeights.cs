namespace MediaBoard.Core.Identification;

/// <summary>Every weight and threshold behind title similarity and candidate scoring.</summary>
public static class ScoringWeights
{
    // Auto-accept: best score at or above the threshold, and ahead of the runner-up by the margin.
    public const double AcceptThreshold = 0.80;
    public const double AcceptMargin = 0.10;

    // Candidate score = sum of weight * component, then penalties, clamped to [0, 1].
    public const double TitleWeight = 0.60;
    public const double YearWeight = 0.20;
    public const double TypeWeight = 0.15;
    public const double PopularityWeight = 0.05;

    // Year component.
    public const double YearUnknownScore = 0.5;
    public const double YearExactScore = 1.0;
    public const double YearOffByOneScore = 0.7;
    public const double YearFarScore = 0.0;
    public const int YearOffByOneDistance = 1;

    // Popularity component: log10(1 + popularity) / PopularityLogDivisor, capped at 1.
    public const double PopularityLogDivisor = 3.0;

    // Duration check: a file this far from the runtime is probably a different title.
    public const double DurationPenalty = 0.25;
    public const double DurationToleranceMinMinutes = 15.0;
    public const double DurationToleranceFraction = 0.35;

    // Title similarity = TitleSortShare * token-sort ratio + TitleSetShare * token-set ratio.
    public const double TitleSortShare = 0.8;
    public const double TitleSetShare = 0.2;

    // An episode title from a file name matches a TMDB episode name at or above this similarity.
    public const double EpisodeTitleThreshold = 0.85;
}
