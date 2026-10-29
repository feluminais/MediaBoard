using System.Text.RegularExpressions;
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
    private const string CommaSeparator = ", ";
    private const double TieTolerance = 1e-9;

    // "Title. Part 1", "Title Part II", "Title - Part Two": the title before a trailing part number.
    private static readonly Regex PartSuffix = new(
        @"^(?<whole>.*?\S)[\s.,:;\-–—]*\bpart\s*(?:\d+|[ivx]+|one|two|three|four|five|six|seven|eight|nine|ten)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Tries the episode title (whole, then split on " &amp; " with every part matched to its own episode),
    /// then the parsed episode numbers. Returns the episodes ordered by number, or null when nothing fits.
    /// </summary>
    public static IReadOnlyList<EpisodeDetails>? Resolve(ParsedMedia parsed, SeasonDetails season, SeasonDetails? specials) =>
        ResolveInSeason(parsed, season, specials).Episodes;

    /// <summary>
    /// <see cref="Resolve"/>, then the show's other seasons for a title the season does not hold. A release
    /// numbered differently from TMDB (Amazon's Futurama "S04E13 Bend Her" is TMDB's S05E13), a file of
    /// segments TMDB lists one by one (SpongeBob "A &amp; B &amp; C") and a film split into parts ("Bender's
    /// Big Score. Part 1", TMDB's special "Bender's Big Score") are all found by title. When the title names
    /// another episode than the one at the file's number and none is found, the file gets no episode (an empty
    /// list) rather than the wrong one. Null means the episodes cannot be told: a number TMDB does not have with
    /// no title found, or a title that fits several episodes equally.
    /// <paramref name="season"/> returns null for a season the show does not have.
    /// <paramref name="seasonNumbers"/> is asked only when the other seasons are needed.
    /// </summary>
    public static async Task<IReadOnlyList<EpisodeDetails>?> ResolveAsync(
        ParsedMedia parsed, Func<int, Task<SeasonDetails?>> season, Func<Task<IReadOnlyList<int>>> seasonNumbers)
    {
        var number = parsed.Season ?? 1;
        var own = await season(number).ConfigureAwait(false);

        // Season 0 is read only to match an episode title, so a file without one never needs it.
        var specials = number != 0 && HasTitles(parsed) ? await season(0).ConfigureAwait(false) : null;
        List<SeasonDetails>? others = null;

        if (own is null) return await ElsewhereAsync().ConfigureAwait(false);

        var (episodes, byTitle) = ResolveInSeason(parsed, own, specials);
        if (episodes is null)
        {
            // Numbers TMDB has, with a title that ties or fits nothing: no sign of a different numbering.
            return parsed.Episodes.All(n => own.Episodes.Any(e => e.Number == n))
                ? null
                : await ElsewhereAsync().ConfigureAwait(false);
        }

        if (byTitle) return episodes;

        // The numbers decided. A title naming another episode is looked up in the other seasons. So are segments
        // that did not all match in the season, even when one fits the number: a file holding three of TMDB's
        // segments links each of them, wherever TMDB numbers it.
        var found = IsSegmented(parsed) || TitleDisagrees(parsed, episodes) ? await ElsewhereAsync().ConfigureAwait(false) : null;
        if (found is not null) return KeepsWholeRange(found, parsed, own) ? episodes : found;

        // A title that names another episode, found nowhere: no episode rather than the wrong one at the number.
        return TitleDisagrees(parsed, episodes) ? [] : episodes;

        async Task<IReadOnlyList<EpisodeDetails>?> ElsewhereAsync()
        {
            if (!HasTitles(parsed)) return null;
            if (IsSegmented(parsed)) return await BySegmentsAsync().ConfigureAwait(false);

            var seasons = await OthersAsync().ConfigureAwait(false);
            return ResolveAcrossSeasons(parsed, seasons) ?? ResolveWithoutPart(parsed, own, seasons);
        }

        // Each segment links the one episode its name matches closely: in the file's season, else in any other.
        // A segment that finds nothing, or several, is left out. Null when no segment finds anything.
        async Task<IReadOnlyList<EpisodeDetails>?> BySegmentsAsync()
        {
            var found = new List<EpisodeDetails>();
            foreach (var segment in Segments(parsed))
            {
                if (await FindAsync(segment).ConfigureAwait(false) is { } episode)
                {
                    found.Add(episode);
                    continue;
                }

                // "Back to the Past, The Bad Guy Club for Villains": a comma separates segments only when every
                // piece is an episode name; otherwise it belongs to the name ("Yours, Mine and Mine").
                var pieces = segment.Split(CommaSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (pieces.Length < 2) continue;

                var byPiece = new List<EpisodeDetails>();
                foreach (var piece in pieces)
                {
                    if (await FindAsync(piece).ConfigureAwait(false) is not { } match) break;
                    byPiece.Add(match);
                }

                if (byPiece.Count == pieces.Length) found.AddRange(byPiece);
            }

            return found.Count == 0 ? null : Ordered([.. found.Distinct()]);
        }

        async Task<EpisodeDetails?> FindAsync(string name)
        {
            if (own is not null)
            {
                var inSeason = CloseMatches(name, [own]);
                if (inSeason.Count > 0) return inSeason.Count == 1 ? inSeason[0] : null;
            }

            var elsewhere = CloseMatches(name, await OthersAsync().ConfigureAwait(false));
            return elsewhere.Count == 1 ? elsewhere[0] : null;
        }

        // Every season but the file's own: the specials, then the rest in TMDB's order. Read once, when first needed.
        async Task<List<SeasonDetails>> OthersAsync()
        {
            if (others is not null) return others;

            others = [];
            if (specials is not null) others.Add(specials);
            foreach (var other in await seasonNumbers().ConfigureAwait(false))
            {
                if (other == 0 || other == number) continue;
                try
                {
                    if (await season(other).ConfigureAwait(false) is { } details) others.Add(details);
                }
                catch (MetadataUnavailableException e) when (e.Reason == UnavailableReason.ServerError)
                {
                    // A season TMDB lists but cannot serve is left out.
                }
            }

            return others;
        }
    }

    /// <summary>
    /// True when the file has an episode title and it is far from every one of <paramref name="episodes"/>
    /// (below <see cref="ScoringWeights.EpisodeTitleDisagreeThreshold"/>): it names some other episode. A title
    /// of segments ("A &amp; B") agrees when any segment does. A title that is only the show's name
    /// ("S24E01. South Park (2020)") is no episode title.
    /// </summary>
    public static bool TitleDisagrees(ParsedMedia parsed, IReadOnlyList<EpisodeDetails> episodes)
    {
        var show = TitleNormalizer.Normalize(parsed.Title);
        var titles = parsed.EpisodeTitles
            .Where(t => !string.IsNullOrWhiteSpace(t) && TitleNormalizer.Normalize(t) != show)
            .ToList();
        if (titles.Count == 0) return false;

        var names = titles
            .SelectMany(t => t.Split(SegmentSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(t))
            .ToList();
        return !episodes.Any(e => names.Any(n => TitleSimilarity.Score(n, e.Name) >= ScoringWeights.EpisodeTitleDisagreeThreshold));
    }

    /// <summary>
    /// The title without a trailing part number ("Bender's Big Score. Part 1", "... Part II" give "Bender's
    /// Big Score"), or null when it has none.
    /// </summary>
    public static string? WithoutPart(string title) =>
        PartSuffix.Match(title) is { Success: true } m ? m.Groups["whole"].Value : null;

    private static bool HasTitles(ParsedMedia parsed) => parsed.EpisodeTitles.Any(t => !string.IsNullOrWhiteSpace(t));

    /// <summary>The episode titles split on " &amp; ", each piece a segment.</summary>
    private static List<string> Segments(ParsedMedia parsed) =>
    [
        .. parsed.EpisodeTitles
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => t.Split(SegmentSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
    ];

    /// <summary>More than one segment, or one with a comma that may separate episode names.</summary>
    private static bool IsSegmented(ParsedMedia parsed)
    {
        var segments = Segments(parsed);
        return segments.Count > 1 || segments.Any(s => s.Contains(CommaSeparator, StringComparison.Ordinal));
    }

    /// <summary>Episodes whose name is at least <see cref="ScoringWeights.OtherSeasonTitleThreshold"/> like <paramref name="title"/>.</summary>
    private static List<EpisodeDetails> CloseMatches(string title, IEnumerable<SeasonDetails> seasons) =>
    [
        .. seasons
            .SelectMany(s => s.Episodes)
            .Where(e => TitleSimilarity.Score(title, e.Name) >= ScoringWeights.OtherSeasonTitleThreshold),
    ];

    /// <summary>
    /// A film split into parts that TMDB lists whole: "Bender's Big Score. Part 1" whose full title matched
    /// nothing is retried as "Bender's Big Score" in the whole show, specials included. Every part then links
    /// the same episode. Null unless exactly one episode matches.
    /// </summary>
    private static IReadOnlyList<EpisodeDetails>? ResolveWithoutPart(
        ParsedMedia parsed, SeasonDetails? own, IReadOnlyList<SeasonDetails> others)
    {
        var titles = parsed.EpisodeTitles.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (titles.Count != 1 || WithoutPart(titles[0]) is not { } whole) return null;

        var matches = CloseMatches(whole, own is null ? others : others.Prepend(own));
        return matches.Count == 1 ? matches : null;
    }

    /// <summary>
    /// The episode title (whole, then every segment) in the season and its specials, else the numbers.
    /// ByTitle tells which of the two decided.
    /// </summary>
    private static (IReadOnlyList<EpisodeDetails>? Episodes, bool ByTitle) ResolveInSeason(
        ParsedMedia parsed, SeasonDetails season, SeasonDetails? specials)
    {
        SeasonDetails[] pools = specials is null ? [season] : [season, specials];

        var byTitle = ResolveByTitles(parsed, pools);
        if (byTitle.Ambiguous) return (null, false);

        if (byTitle.Episodes is null) return (ResolveByNumbers(parsed, season), false);

        return KeepsWholeRange(byTitle.Episodes, parsed, season)
            ? (ResolveByNumbers(parsed, season), true)
            : (byTitle.Episodes, true);
    }

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
            var matches = CloseMatches(title, seasons);
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
