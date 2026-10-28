using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Identification;

/// <summary>
/// Decides which TMDB episodes a file holds. TMDB's list is the reference: when the number in the file
/// name and TMDB disagree, the episode title wins.
/// </summary>
public static class EpisodeResolver
{
    private const string SegmentSeparator = " & ";
    private const double TieTolerance = 1e-9;

    /// <summary>
    /// Tries the episode title (whole, then split on " &amp; " with every part matched to its own episode),
    /// then the parsed episode numbers. Returns the episodes ordered by number, or null when nothing fits.
    /// </summary>
    public static IReadOnlyList<EpisodeDetails>? Resolve(ParsedMedia parsed, SeasonDetails season, SeasonDetails? specials)
    {
        SeasonDetails[] pools = specials is null ? [season] : [season, specials];

        var byTitle = ResolveByTitles(parsed, pools);
        if (byTitle.Ambiguous) return null;

        return byTitle.Episodes ?? ResolveByNumbers(parsed, season);
    }

    private static TitleResult ResolveByTitles(ParsedMedia parsed, SeasonDetails[] pools)
    {
        var titles = parsed.EpisodeTitles.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (titles.Count == 0) return TitleResult.None;

        var matched = new List<EpisodeDetails>();
        foreach (var title in titles)
        {
            var result = ResolveTitle(title, parsed.Episodes, pools);
            if (result.Episodes is null) return result;
            foreach (var episode in result.Episodes)
            {
                if (!matched.Contains(episode)) matched.Add(episode);
            }
        }

        return new TitleResult(Ordered(matched), false);
    }

    private static TitleResult ResolveTitle(string title, IReadOnlyList<int> parsedNumbers, SeasonDetails[] pools)
    {
        var whole = PickBest(title, parsedNumbers, pools, []);
        if (whole.Ambiguous) return TitleResult.Tie;
        if (whole.Episode is not null) return new TitleResult([whole.Episode], false);

        var parts = title.Split(SegmentSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return TitleResult.None;

        var used = new List<EpisodeDetails>();
        foreach (var part in parts)
        {
            var pick = PickBest(part, parsedNumbers, pools, used);
            if (pick.Episode is null) return pick.Ambiguous ? TitleResult.Tie : TitleResult.None;
            used.Add(pick.Episode);
        }

        return new TitleResult(used, false);
    }

    /// <summary>
    /// Best episode for a title in the first pool that has a match at or above the threshold. A tie for the
    /// top score goes to the episode whose number was parsed from the file name, else the title is ambiguous
    /// and the file stays unresolved.
    /// </summary>
    private static Pick PickBest(
        string title, IReadOnlyList<int> parsedNumbers, SeasonDetails[] pools, List<EpisodeDetails> exclude)
    {
        foreach (var pool in pools)
        {
            var scored = pool.Episodes
                .Where(e => !exclude.Contains(e))
                .Select(e => (Episode: e, Score: TitleSimilarity.Score(title, e.Name)))
                .Where(x => x.Score >= ScoringWeights.EpisodeTitleThreshold)
                .ToList();
            if (scored.Count == 0) continue;

            var top = scored.Max(x => x.Score);
            var best = scored.Where(x => top - x.Score <= TieTolerance).Select(x => x.Episode).ToList();
            if (best.Count == 1) return new Pick(best[0], false);

            var byNumber = best.Where(e => parsedNumbers.Contains(e.Number)).ToList();
            return byNumber.Count == 1 ? new Pick(byNumber[0], false) : Pick.Tie;
        }

        return Pick.None;
    }

    private static IReadOnlyList<EpisodeDetails>? ResolveByNumbers(ParsedMedia parsed, SeasonDetails season)
    {
        if (parsed.Episodes.Count == 0) return null;

        var found = new List<EpisodeDetails>();
        foreach (var number in parsed.Episodes.Distinct())
        {
            var episode = season.Episodes.FirstOrDefault(e => e.Number == number);
            if (episode is null) return null;
            found.Add(episode);
        }

        return Ordered(found);
    }

    private static IReadOnlyList<EpisodeDetails> Ordered(List<EpisodeDetails> episodes) =>
        episodes.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList();

    /// <summary>Titles matched (Episodes set), found nothing (None), or matched several episodes equally (Tie).</summary>
    private readonly record struct TitleResult(IReadOnlyList<EpisodeDetails>? Episodes, bool Ambiguous)
    {
        public static TitleResult None => new(null, false);
        public static TitleResult Tie => new(null, true);
    }

    private readonly record struct Pick(EpisodeDetails? Episode, bool Ambiguous)
    {
        public static Pick None => new(null, false);
        public static Pick Tie => new(null, true);
    }
}
