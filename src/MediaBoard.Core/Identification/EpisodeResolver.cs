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

        if (byTitle.Episodes is null) return ResolveByNumbers(parsed, season);

        return KeepsWholeRange(byTitle.Episodes, parsed, season)
            ? ResolveByNumbers(parsed, season)
            : byTitle.Episodes;
    }

    /// <summary>
    /// <see cref="Resolve"/> with the show's other seasons as a fallback, for a release numbered differently
    /// from TMDB (Amazon's Futurama "S04E13 Bend Her" is TMDB's S05E13). The file's episode title is looked up
    /// in the other seasons (<see cref="ResolveAcrossSeasons"/>) when its number does not exist on TMDB, or when
    /// the episode at its number has a clearly different name; with no strong match there, a number that exists
    /// is kept. <paramref name="season"/> returns null for a season the show does not have.
    /// <paramref name="seasonNumbers"/> is asked only when the other seasons are needed.
    /// </summary>
    public static async Task<IReadOnlyList<EpisodeDetails>?> ResolveAsync(
        ParsedMedia parsed, Func<int, Task<SeasonDetails?>> season, Func<Task<IReadOnlyList<int>>> seasonNumbers)
    {
        var number = parsed.Season ?? 1;
        var own = await season(number).ConfigureAwait(false);
        if (own is null) return await InOtherSeasonsAsync(null).ConfigureAwait(false);

        // Season 0 is read only to match an episode title, so a file without one never needs it.
        var specials = number != 0 && HasTitles(parsed) ? await season(0).ConfigureAwait(false) : null;

        var episodes = Resolve(parsed, own, specials);
        if (episodes is null)
        {
            // Numbers TMDB has, with a title that ties or fits nothing: no sign of a different numbering.
            return parsed.Episodes.All(n => own.Episodes.Any(e => e.Number == n))
                ? null
                : await InOtherSeasonsAsync(specials).ConfigureAwait(false);
        }

        return TitleDisagrees(parsed, episodes)
            ? await InOtherSeasonsAsync(specials).ConfigureAwait(false) ?? episodes
            : episodes;

        async Task<IReadOnlyList<EpisodeDetails>?> InOtherSeasonsAsync(SeasonDetails? fetchedSpecials)
        {
            if (!HasTitles(parsed)) return null;

            var seasons = new List<SeasonDetails>();
            if (fetchedSpecials is not null) seasons.Add(fetchedSpecials);
            foreach (var other in await seasonNumbers().ConfigureAwait(false))
            {
                if (other == 0 || other == number) continue;
                try
                {
                    if (await season(other).ConfigureAwait(false) is { } details) seasons.Add(details);
                }
                catch (MetadataUnavailableException e) when (e.Reason == UnavailableReason.ServerError)
                {
                    // A season TMDB lists but cannot serve is left out.
                }
            }

            return ResolveAcrossSeasons(parsed, seasons);
        }
    }

    /// <summary>
    /// True when the file has an episode title and it is far from every one of <paramref name="episodes"/>
    /// (below <see cref="ScoringWeights.EpisodeTitleDisagreeThreshold"/>): it names some other episode. A title
    /// of segments ("A &amp; B") agrees when any segment does.
    /// </summary>
    public static bool TitleDisagrees(ParsedMedia parsed, IReadOnlyList<EpisodeDetails> episodes)
    {
        var titles = parsed.EpisodeTitles.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (titles.Count == 0) return false;

        var names = titles
            .SelectMany(t => t.Split(SegmentSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(t))
            .ToList();
        return !episodes.Any(e => names.Any(n => TitleSimilarity.Score(n, e.Name) >= ScoringWeights.EpisodeTitleDisagreeThreshold));
    }

    private static bool HasTitles(ParsedMedia parsed) => parsed.EpisodeTitles.Any(t => !string.IsNullOrWhiteSpace(t));

    /// <summary>
    /// A title that names only part of a parsed range ("S01E01-E02 Pilot") does not disagree with the numbers:
    /// the file holds the whole range. True when every title match sits in the requested season, inside the
    /// parsed numbers, and every parsed number exists in that season.
    /// </summary>
    private static bool KeepsWholeRange(IReadOnlyList<EpisodeDetails> matched, ParsedMedia parsed, SeasonDetails season) =>
        matched.All(e => e.Season == season.Season && parsed.Episodes.Contains(e.Number))
        && parsed.Episodes.All(n => season.Episodes.Any(e => e.Number == n));

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

    /// <summary>
    /// For a file whose episode number TMDB does not have (a release numbered differently, such as Amazon's
    /// Futurama): each episode title must match exactly one episode in <paramref name="seasons"/> at
    /// <see cref="ScoringWeights.OtherSeasonTitleThreshold"/> or above. Returns the episodes ordered, or null.
    /// </summary>
    public static IReadOnlyList<EpisodeDetails>? ResolveAcrossSeasons(ParsedMedia parsed, IReadOnlyList<SeasonDetails> seasons)
    {
        var titles = parsed.EpisodeTitles.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (titles.Count == 0) return null;

        var found = new List<EpisodeDetails>();
        foreach (var title in titles)
        {
            var matches = seasons
                .SelectMany(s => s.Episodes)
                .Where(e => TitleSimilarity.Score(title, e.Name) >= ScoringWeights.OtherSeasonTitleThreshold)
                .ToList();
            if (matches.Count != 1) return null;
            if (!found.Contains(matches[0])) found.Add(matches[0]);
        }

        return Ordered(found);
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
