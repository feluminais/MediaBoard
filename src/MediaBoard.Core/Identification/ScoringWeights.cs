namespace MediaBoard.Core.Identification;

/// <summary>Every weight and threshold behind title similarity and candidate scoring.</summary>
public static class ScoringWeights
{
    // Auto-accept: best score at or above the threshold, and ahead of the runner-up by the margin.
    public const double AcceptThreshold = 0.80;
    public const double AcceptMargin = 0.10;

    // With no year to tell them apart, a leader that ties the runners-up on title is still accepted when it has
    // this many times their votes, and at least VoteLeadMinimum votes.
    public const int VoteLeadFactor = 10;
    public const int VoteLeadMinimum = 100;

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

    // A retry with a shortened title loses this much per word dropped from the parsed title.
    public const double ShortenedWordPenalty = 0.03;

    // Duration check: a file this far from the runtime is probably a different title.
    public const double DurationPenalty = 0.25;
    public const double DurationToleranceMinMinutes = 15.0;
    public const double DurationToleranceFraction = 0.35;

    // Title similarity = TitleSortShare * token-sort ratio + TitleSetShare * token-set ratio.
    public const double TitleSortShare = 0.8;
    public const double TitleSetShare = 0.2;

    // Title similarity loses this much when the titles carry different sequel or part numbers ("Open Season 2"
    // and "Open Season 3"), or only one of them has one.
    public const double NumberMismatchPenalty = 0.20;

    // An episode title from a file name matches a TMDB episode name at or above this similarity.
    public const double EpisodeTitleThreshold = 0.85;
}
