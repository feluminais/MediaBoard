using System.Globalization;
using System.Text.RegularExpressions;
using CozyCouch.Core.Metadata;
using CozyCouch.Core.Parsing;

namespace CozyCouch.Core.Identification;

/// <summary>
/// Decides which TMDB episodes a file holds. TMDB's list is the reference: when the number in the file
/// name and TMDB disagree, the episode title wins.
/// </summary>
public static class EpisodeResolver
{
    private const string SegmentSeparator = " & ";
    private const string CommaSeparator = ", ";
    private const double TieTolerance = 1e-9;

    // A file's "Title. Part 1", "Title Part II", "Title - part two": the title and the part.
    private static readonly Regex FilePart = new(
        @"^(?<whole>.*?\S)[\s.,:;\-–—]*\bpart\s*(?<n>\w+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A TMDB name with a part or sequel number: "Title (2)", "Title Part 2", "Title: Part Two", "Title, Episode II",
    // "Title II".
    private static readonly Regex NumberedName = new(
        @"^(?<whole>.*?\S)[\s.,:;\-–—]+(?:\(\s*(?<n>[^)\s]+)\s*\)|(?:part|episode|chapter|pt|ep)\.?\s*#?\s*(?<n>\w+)|(?<n>[ivx]+|\d+))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // What TMDB calls an episode before it knows the name.
    private static readonly Regex Placeholder = new(
        @"^\s*(?:episode\s*#?\s*\d+|tba|tbd)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] NumberWords =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
    ];

    /// <summary>
    /// Tries the episode title (whole, then split on " &amp; " with every part matched to its own episode),
    /// then the parsed episode numbers. Returns the episodes ordered by number, or null when nothing fits.
    /// </summary>
    public static IReadOnlyList<EpisodeDetails>? Resolve(ParsedMedia parsed, SeasonDetails season, SeasonDetails? specials) =>
        ResolveInSeason(WithoutShowName(parsed), season, specials).Episodes;

    /// <summary>
    /// <see cref="Resolve"/>, then the whole show when the title names an episode the season does not hold at
    /// the file's number: a release numbered differently from TMDB (Amazon's Futurama "S04E13 Bend Her" is
    /// TMDB's S05E13), a file of segments TMDB lists one by one (SpongeBob "A &amp; B &amp; C"), or one part of
    /// a story ("Imaginationland. Part 2" is TMDB's "Imaginationland, Episode II"). A number the title agrees
    /// with is never replaced. When the title names another episode and none is found, the file gets no
    /// episode (an empty list) rather than the wrong one; so does a part of a film TMDB lists whole ("Bender's
    /// Big Score. Part 1"). Null means the episodes cannot be told: a number TMDB does not have with no title
    /// found, or a title that fits several episodes equally.
    /// <paramref name="season"/> returns null for a season the show does not have.
    /// <paramref name="seasonNumbers"/> is asked only when the other seasons are needed.
    /// </summary>
    public static async Task<IReadOnlyList<EpisodeDetails>?> ResolveAsync(
        ParsedMedia parsed, Func<int, Task<SeasonDetails?>> season, Func<Task<IReadOnlyList<int>>> seasonNumbers)
    {
        parsed = WithoutShowName(parsed);
        var number = parsed.Season ?? 1;
        var own = await season(number).ConfigureAwait(false);

        // Season 0 is read only to match an episode title, so a file without one never needs it.
        var specials = number != 0 && HasTitles(parsed) ? await season(0).ConfigureAwait(false) : null;

        if (own is null) return await ElsewhereAsync().ConfigureAwait(false);

        var (episodes, byTitle) = ResolveInSeason(parsed, own, specials);
        if (episodes is null)
        {
            // Numbers TMDB has, with a title that ties or fits nothing: no sign of a different numbering.
            return parsed.Episodes.All(n => own.Episodes.Any(e => e.Number == n))
                ? null
                : await ElsewhereAsync().ConfigureAwait(false);
        }

        if (byTitle || !TitleDisagrees(parsed, episodes)) return episodes;

        // The title names another episode: that one, or none rather than the wrong one at the number.
        return await ElsewhereAsync().ConfigureAwait(false) ?? [];

        // The title in the whole show: as it is, else segment by segment, else as one part of a story.
        async Task<IReadOnlyList<EpisodeDetails>?> ElsewhereAsync()
        {
            if (!HasTitles(parsed)) return null;

            var others = await OthersAsync().ConfigureAwait(false);
            return ResolveAcrossSeasons(parsed, others)
                   ?? ResolveSegments(parsed, own, others)
                   ?? ResolvePart(parsed, own is null ? others : [own, .. others]);
        }

        // Every season but the file's own: the specials, then the rest in TMDB's order.
        async Task<List<SeasonDetails>> OthersAsync()
        {
            var others = new List<SeasonDetails>();
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
    /// of segments ("A &amp; B") agrees when any segment does. Nothing can disagree unless both sides are real
    /// names to compare: a title that is only the show's name ("S24E01. South Park (2020)") or is not in Latin
    /// script (the client asks TMDB for English names) counts as no title, and TMDB's placeholder for a name it
    /// does not know yet ("Episode 5", "TBA") agrees with any title.
    /// </summary>
    public static bool TitleDisagrees(ParsedMedia parsed, IReadOnlyList<EpisodeDetails> episodes)
    {
        var titles = Titles(WithoutShowName(parsed)).Where(IsLatin).ToList();
        if (titles.Count == 0 || episodes.Any(e => Placeholder.IsMatch(e.Name))) return false;

        var names = titles
            .SelectMany(t => t.Split(SegmentSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(t))
            .ToList();
        return !episodes.Any(e => names.Any(n => Agrees(n, e.Name)));
    }

    /// <summary>
    /// Close enough, or one name holds the other word for word: a release adds words TMDB does not have
    /// ("Fire &amp; Ice (Extended Cut)" for "Fire &amp; Ice") and still names the same episode.
    /// </summary>
    private static bool Agrees(string title, string episodeName)
    {
        if (TitleSimilarity.Score(title, episodeName) >= ScoringWeights.EpisodeTitleDisagreeThreshold) return true;

        var a = $" {TitleNormalizer.Normalize(title)} ";
        var b = $" {TitleNormalizer.Normalize(episodeName)} ";
        return a.Trim().Length > 0 && b.Trim().Length > 0
               && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }

    /// <summary>
    /// The title and part number of a file's "Story. Part 2" ("Part II", "part two"), or null when the title
    /// does not end in a part number.
    /// </summary>
    public static (string Whole, int Number)? PartOf(string title) =>
        FilePart.Match(title) is { Success: true } m && ParseNumber(m.Groups["n"].Value) is { } n
            ? (m.Groups["whole"].Value, n)
            : null;

    private static List<string> Titles(ParsedMedia parsed) =>
        [.. parsed.EpisodeTitles.Where(t => !string.IsNullOrWhiteSpace(t))];

    private static bool HasTitles(ParsedMedia parsed) => Titles(parsed).Count > 0;

    /// <summary>The file without episode titles that are only the show's name: those name no episode.</summary>
    private static ParsedMedia WithoutShowName(ParsedMedia parsed)
    {
        var show = TitleNormalizer.Normalize(parsed.Title);
        return parsed.EpisodeTitles.Any(t => TitleNormalizer.Normalize(t) == show)
            ? parsed with { EpisodeTitles = [.. parsed.EpisodeTitles.Where(t => TitleNormalizer.Normalize(t) != show)] }
            : parsed;
    }

    /// <summary>Has letters, and every one of them is Latin.</summary>
    private static bool IsLatin(string title) =>
        title.Any(char.IsLetter)
        && title.Where(char.IsLetter).All(c => c <= 'ɏ' || c is >= 'Ḁ' and <= 'ỿ');

    /// <summary>
    /// Each segment of the title links the one episode its name matches: in the file's season, else in the
    /// others. A segment that finds nothing, or several, is left out. A ", " separates segments only when every
    /// piece is an episode name ("Back to the Past, The Bad Guy Club for Villains"). Null when the title has
    /// no segments or none finds anything.
    /// </summary>
    private static IReadOnlyList<EpisodeDetails>? ResolveSegments(
        ParsedMedia parsed, SeasonDetails? own, IReadOnlyList<SeasonDetails> others)
    {
        var segments = Titles(parsed)
            .SelectMany(t => t.Split(SegmentSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        if (segments.Count < 2 && !segments.Any(s => s.Contains(CommaSeparator, StringComparison.Ordinal))) return null;

        var found = new List<EpisodeDetails>();
        foreach (var segment in segments)
        {
            if (Find(segment) is { } episode)
            {
                found.Add(episode);
                continue;
            }

            var pieces = segment.Split(CommaSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var byPiece = pieces.Select(Find).ToList();
            if (pieces.Length > 1 && byPiece.All(e => e is not null)) found.AddRange(byPiece.OfType<EpisodeDetails>());
        }

        return found.Count == 0 ? null : Ordered([.. found.Distinct()]);

        EpisodeDetails? Find(string name)
        {
            if (own is not null)
            {
                var inSeason = SegmentMatches(name, [own]);
                if (inSeason.Count > 0) return inSeason.Count == 1 ? inSeason[0] : null;
            }

            var elsewhere = SegmentMatches(name, others);
            return elsewhere.Count == 1 ? elsewhere[0] : null;
        }
    }

    /// <summary>
    /// A file's "Story. Part N" in the whole show: the one episode with the same name and part number in any
    /// form ("Story (2)", "Story, Episode II"), or for part 1 the bare "Story" next to numbered later parts.
    /// When TMDB lists the story only whole and unnumbered (a film split into parts, like "Bender's Big
    /// Score"), the part gets no episode (empty), so it keeps its own row with its own progress. Null when the
    /// title is no part title or nothing fits.
    /// </summary>
    private static IReadOnlyList<EpisodeDetails>? ResolvePart(ParsedMedia parsed, IReadOnlyList<SeasonDetails> show)
    {
        if (Titles(parsed) is not [var title] || PartOf(title) is not { } part) return null;

        var sameStory = show
            .SelectMany(s => s.Episodes)
            .Select(e => (Episode: e, Name: NumberOf(e.Name)))
            .Where(x => TitleSimilarity.Score(part.Whole, x.Name.Whole) >= ScoringWeights.OtherSeasonTitleThreshold)
            .ToList();

        var samePart = sameStory.Where(x => x.Name.Number == part.Number).Select(x => x.Episode).ToList();
        if (samePart.Count > 0) return samePart.Count == 1 ? samePart : null;

        var whole = sameStory.Where(x => x.Name.Number is null).Select(x => x.Episode).ToList();
        if (part.Number == 1 && whole.Count == 1 && sameStory.Any(x => x.Name.Number > 1)) return whole;
        return whole.Count > 0 ? [] : null;
    }

    /// <summary>A TMDB name split into its story and part number; the number is null for a name without one.</summary>
    private static (string Whole, int? Number) NumberOf(string name) =>
        NumberedName.Match(name) is { Success: true } m && ParseNumber(m.Groups["n"].Value) is { } n
            ? (m.Groups["whole"].Value, n)
            : (name, null);

    /// <summary>Digits, a Roman numeral up to XX, or a number word up to twenty.</summary>
    private static int? ParseNumber(string text)
    {
        var token = text.Trim().ToLowerInvariant();
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var digits)) return digits;

        var word = Array.IndexOf(NumberWords, token);
        if (word > 0) return word;

        if (token.Length == 0 || token.Any(c => c is not ('i' or 'v' or 'x'))) return null;
        int total = 0, previous = 0;
        foreach (var value in token.Reverse().Select(c => c switch { 'i' => 1, 'v' => 5, _ => 10 }))
        {
            total += value < previous ? -value : value;
            previous = Math.Max(previous, value);
        }

        return total is > 0 and <= 20 ? total : null;
    }

    /// <summary>Episodes whose name is at least <see cref="ScoringWeights.OtherSeasonTitleThreshold"/> like <paramref name="title"/>.</summary>
    private static List<EpisodeDetails> CloseMatches(string title, IEnumerable<SeasonDetails> seasons) =>
    [
        .. seasons
            .SelectMany(s => s.Episodes)
            .Where(e => TitleSimilarity.Score(title, e.Name) >= ScoringWeights.OtherSeasonTitleThreshold),
    ];

    /// <summary>
    /// <see cref="CloseMatches"/> for one segment of a title. A one-word segment ("Gone", "Fire") needs the
    /// exact name: a near miss on a short common word is too often another episode.
    /// </summary>
    private static List<EpisodeDetails> SegmentMatches(string segment, IEnumerable<SeasonDetails> seasons)
    {
        if (!IsOneWord(segment)) return CloseMatches(segment, seasons);

        var name = TitleNormalizer.Normalize(segment);
        return [.. seasons.SelectMany(s => s.Episodes).Where(e => TitleNormalizer.Normalize(e.Name) == name)];
    }

    private static bool IsOneWord(string title)
    {
        var normalized = TitleNormalizer.Normalize(title);
        return normalized.Length > 0 && !normalized.Contains(' ', StringComparison.Ordinal);
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
            var pick = PickBest(part, parsedNumbers, pools, used, exact: IsOneWord(part));
            if (pick.Episode is null) return pick.Ambiguous ? TitleResult.Tie : TitleResult.None;
            used.Add(pick.Episode);
        }

        return new TitleResult(used, false);
    }

    /// <summary>
    /// Best episode for a title in the first pool that has a match at or above the threshold. A tie for the
    /// top score goes to the episode whose number was parsed from the file name, else the title is ambiguous
    /// and the file stays unresolved. With <paramref name="exact"/> (a one-word segment) only the same name matches.
    /// </summary>
    private static Pick PickBest(
        string title, IReadOnlyList<int> parsedNumbers, SeasonDetails[] pools, List<EpisodeDetails> exclude, bool exact = false)
    {
        foreach (var pool in pools)
        {
            var scored = pool.Episodes
                .Where(e => !exclude.Contains(e))
                .Select(e => (Episode: e, Score: exact
                    ? TitleNormalizer.Normalize(title) == TitleNormalizer.Normalize(e.Name) ? 1.0 : 0.0
                    : TitleSimilarity.Score(title, e.Name)))
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
