using MediaBoard.Core.Identification;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Tests.Identification;

public class CandidateScorerTests
{
    private static ParsedMedia Parsed(string title, int? year = null, MediaKind kind = MediaKind.Movie) =>
        new(kind, title, year, null, [], [], null, null, null);

    private static SearchCandidate Candidate(
        string name, int? year, TitleKind kind = TitleKind.Movie, double popularity = 50, string? original = null) =>
        new(1, kind, name, original, year, popularity, null, null, null);

    private static double EpisodeYearPart(int? showYear, int? airYear, int candidateYear)
    {
        var parsed = new ParsedMedia(MediaKind.Episode, "Show", showYear, 1, [1], [], "Show", null, null, airYear);
        var candidate = Candidate("Show", candidateYear, TitleKind.Show);
        var noYear = Candidate("Show", null, TitleKind.Show);
        return (CandidateScorer.Score(parsed, candidate, null, null) - CandidateScorer.Score(parsed, noYear, null, null))
               / ScoringWeights.YearWeight + ScoringWeights.YearUnknownScore;
    }

    [Theory]
    [InlineData(2008, 2008, 1.0)] // exact
    [InlineData(2008, 2007, 1.0)] // one off either way: full credit for a show year
    [InlineData(2008, 2009, 1.0)]
    [InlineData(2013, 2008, 0.5)] // started earlier than a pack's year: like an air year, no penalty
    [InlineData(2008, 2011, 0.0)] // started later: a different show
    public void Scorer_EpisodeShowYear(int showYear, int candidateYear, double expected)
    {
        Assert.Equal(expected, EpisodeYearPart(showYear, null, candidateYear), 9);
    }

    [Theory]
    [InlineData(2004, 2004, 0.5)] // started that year or before: no evidence either way
    [InlineData(2004, 1999, 0.5)]
    [InlineData(2004, 2005, 0.7)] // a year after: a premiere date across New Year, off by one
    [InlineData(2004, 2006, 0.0)] // later still: cannot have aired in 2004
    public void Scorer_EpisodeAirYear(int airYear, int candidateYear, double expected)
    {
        Assert.Equal(expected, EpisodeYearPart(null, airYear, candidateYear), 9);
    }

    [Fact]
    public void Scorer_YearOffByOne_StillAccepts()
    {
        var score = CandidateScorer.Score(Parsed("The Gentlemen", 2019), Candidate("The Gentlemen", 2020), null, null);
        Assert.True(score >= ScoringWeights.AcceptThreshold, $"score was {score}");
    }

    [Fact]
    public void Scorer_ExactMatch_BeatsYearOffByOne()
    {
        var parsed = Parsed("The Gentlemen", 2019);
        Assert.True(
            CandidateScorer.Score(parsed, Candidate("The Gentlemen", 2019), null, null)
            > CandidateScorer.Score(parsed, Candidate("The Gentlemen", 2020), null, null));
    }

    [Fact]
    public void Scorer_YearTwoApart_ScoresNoYearCredit()
    {
        var parsed = Parsed("The Gentlemen", 2019);
        var far = CandidateScorer.Score(parsed, Candidate("The Gentlemen", 2021), null, null);
        var unknown = CandidateScorer.Score(parsed, Candidate("The Gentlemen", null), null, null);
        Assert.True(far < unknown);
    }

    [Fact]
    public void Scorer_WrongType_Penalised()
    {
        var parsed = Parsed("Narcos", 2015, MediaKind.Episode);
        var show = CandidateScorer.Score(parsed, Candidate("Narcos", 2015, TitleKind.Show), null, null);
        var movie = CandidateScorer.Score(parsed, Candidate("Narcos", 2015, TitleKind.Movie), null, null);
        Assert.True(show - movie >= ScoringWeights.TypeWeight - 1e-9);
    }

    [Fact]
    public void Scorer_UnknownKind_MatchesNeitherType()
    {
        var parsed = Parsed("Narcos", 2015, MediaKind.Unknown);
        var show = CandidateScorer.Score(parsed, Candidate("Narcos", 2015, TitleKind.Show), null, null);
        var movie = CandidateScorer.Score(parsed, Candidate("Narcos", 2015, TitleKind.Movie), null, null);
        Assert.Equal(show, movie);
    }

    [Fact]
    public void Scorer_DurationMismatch_Penalised()
    {
        var parsed = Parsed("Planes", 2013, MediaKind.Episode);
        var candidate = Candidate("Planes", 2013, TitleKind.Show);
        var without = CandidateScorer.Score(parsed, candidate, null, 22);
        var mismatch = CandidateScorer.Score(parsed, candidate, 6000, 22);
        var match = CandidateScorer.Score(parsed, candidate, 22 * 60 + 30, 22);
        Assert.Equal(without - ScoringWeights.DurationPenalty, mismatch, 9);
        Assert.Equal(without, match, 9);
    }

    [Fact]
    public void Scorer_DurationWithinTolerance_NotPenalised()
    {
        // 100 min runtime: tolerance is max(15, 35) = 35 min.
        var parsed = Parsed("Heat", 1995);
        var candidate = Candidate("Heat", 1995);
        Assert.Equal(
            CandidateScorer.Score(parsed, candidate, null, 100),
            CandidateScorer.Score(parsed, candidate, 130 * 60, 100),
            9);
        Assert.True(
            CandidateScorer.Score(parsed, candidate, 140 * 60, 100)
            < CandidateScorer.Score(parsed, candidate, null, 100));
    }

    [Fact]
    public void Scorer_OriginalNameCounts()
    {
        var parsed = Parsed("La casa de papel", 2017, MediaKind.Episode);
        var candidate = Candidate("Money Heist", 2017, TitleKind.Show, original: "La casa de papel");
        Assert.True(CandidateScorer.Score(parsed, candidate, null, null) >= ScoringWeights.AcceptThreshold);
    }

    [Fact]
    public void Scorer_UnrelatedTitle_StaysBelowThreshold()
    {
        var score = CandidateScorer.Score(Parsed("The Gentlemen", 2019), Candidate("Breaking Bad", 2019, popularity: 500), null, null);
        Assert.True(score < ScoringWeights.AcceptThreshold, $"score was {score}");
    }

    [Fact]
    public void Scorer_ResultIsClampedToUnitRange()
    {
        var best = CandidateScorer.Score(Parsed("X", 2000), Candidate("X", 2000, popularity: 1e9), null, null);
        var worst = CandidateScorer.Score(Parsed("Aaa", 2000, MediaKind.Episode), Candidate("Zzz", 1990, popularity: -5), 6000, 22);
        Assert.InRange(best, 0.0, 1.0);
        Assert.Equal(0.0, worst);
    }

    [Fact]
    public void Scorer_PopularityIsTieBreakOnly()
    {
        var parsed = Parsed("Heat", 1995);
        var popular = CandidateScorer.Score(parsed, Candidate("Heat", 1995, popularity: 900), null, null);
        var obscure = CandidateScorer.Score(parsed, Candidate("Heat", 1995, popularity: 0), null, null);
        Assert.True(popular > obscure);
        Assert.True(popular - obscure <= ScoringWeights.PopularityWeight + 1e-9);
    }
}
