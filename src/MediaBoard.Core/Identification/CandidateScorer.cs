using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Identification;

public static class CandidateScorer
{
    /// <summary>How well a TMDB candidate fits a parsed file, in 0..1.</summary>
    /// <param name="fileDurationS">Probed length of the file, when known.</param>
    /// <param name="candidateRuntimeMin">Runtime TMDB lists for the candidate, when known.</param>
    public static double Score(ParsedMedia parsed, SearchCandidate c, double? fileDurationS, int? candidateRuntimeMin)
    {
        var score = ScoringWeights.TitleWeight * TitleScore(parsed.Title, c)
                    + ScoringWeights.YearWeight * YearScore(parsed, c.Year)
                    + ScoringWeights.TypeWeight * (KindMatches(parsed.Kind, c.Kind) ? 1.0 : 0.0)
                    + ScoringWeights.PopularityWeight * PopularityScore(c.Popularity);

        if (DurationMismatches(fileDurationS, candidateRuntimeMin))
        {
            score -= ScoringWeights.DurationPenalty;
        }

        return Math.Clamp(score, 0.0, 1.0);
    }

    /// <summary>Title similarity of a candidate: the better of its name and its original name.</summary>
    public static double TitleScore(string title, SearchCandidate c)
    {
        var titleSim = TitleSimilarity.Score(title, c.Name);
        if (!string.IsNullOrWhiteSpace(c.OriginalName))
        {
            titleSim = Math.Max(titleSim, TitleSimilarity.Score(title, c.OriginalName));
        }

        return titleSim;
    }

    /// <summary>
    /// The year component. An episode's air year is no start year: it only rules out a show that started
    /// after it, which scores as a far year.
    /// </summary>
    private static double YearScore(ParsedMedia parsed, int? candidateYear)
    {
        if (candidateYear is not { } c) return ScoringWeights.YearUnknownScore;
        if (parsed.Year is not { } p)
        {
            return parsed.AirYear is { } aired && c > aired ? ScoringWeights.YearFarScore : ScoringWeights.YearUnknownScore;
        }

        var distance = Math.Abs(p - c);
        if (distance == 0) return ScoringWeights.YearExactScore;
        return distance <= ScoringWeights.YearOffByOneDistance ? ScoringWeights.YearOffByOneScore : ScoringWeights.YearFarScore;
    }

    private static bool KindMatches(MediaKind kind, TitleKind candidate) => kind switch
    {
        MediaKind.Movie => candidate == TitleKind.Movie,
        MediaKind.Episode => candidate == TitleKind.Show,
        _ => false,
    };

    private static double PopularityScore(double popularity) =>
        Math.Min(1.0, Math.Max(0.0, Math.Log10(1.0 + Math.Max(0.0, popularity)) / ScoringWeights.PopularityLogDivisor));

    private static bool DurationMismatches(double? fileDurationS, int? runtimeMin)
    {
        if (fileDurationS is not > 0 || runtimeMin is not > 0) return false;

        var runtime = runtimeMin.Value;
        var tolerance = Math.Max(ScoringWeights.DurationToleranceMinMinutes, ScoringWeights.DurationToleranceFraction * runtime);
        return Math.Abs(fileDurationS.Value / 60.0 - runtime) > tolerance;
    }
}
