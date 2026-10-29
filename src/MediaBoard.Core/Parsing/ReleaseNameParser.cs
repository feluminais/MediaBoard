using System.Text.RegularExpressions;

namespace MediaBoard.Core.Parsing;

/// <summary>
/// Reads title, year, season, episodes and episode titles from a torrent-style name
/// (extension already removed). Path-level decisions belong to the caller.
/// </summary>
public static class ReleaseNameParser
{
    private const string Separators = " -.,_";
    private const string TokenStart = "(?<![A-Za-z0-9])";
    private const string TokenEnd = "(?![A-Za-z0-9])";

    // Release tokens that end a title. Dots and underscores were turned into spaces
    // before matching on names without spaces, so "H.265" may also arrive as "H 265".
    private const string NoiseTokens =
        TokenStart + "(?:" +
        @"\d{3,4}[pi]|[48]k" +                                          // resolution
        @"|BD ?Rip|BDRemux|Remux|WEB[- ]?DL|WEB[- ]?Rip|SAT ?Rip|DVD ?Rip|HDTV ?Rip|HDTV|HDRip|Blu[- ]?Ray" + // source
        @"|H[. ]?26[45]|x26[45]|AVC|HEVC" +                              // codec
        @"|HDR(?:10\+?)?|SDR|Dolby[. ]?Vision" +                         // dynamic range
        @"|(?:\d+x)?(?:Ukr|Eng)(?:[._ ]?(?:Ukr|Eng))*" +                 // language
        @"|AMZN|ATVP|NF|DSNP|HMAX|HULU|PCOK" +                           // streaming service
        ")" + TokenEnd;

    // A square-bracket group is always noise; a parenthesis group only when it holds a noise token.
    private static readonly Regex Noise = new(
        @"\[[^\]]*\]|\((?=[^)]*" + NoiseTokens + @")[^)]*\)|" + NoiseTokens,
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TmdbTag = new(
        @"[\[{(]?tmdb(?:id)?[-=](?<id>\d+)[\]})]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImdbTag = new(
        @"[\[{(]?imdb(?:id)?[-=](?<id>tt\d+)[\]})]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex[] EpisodeMarkers =
    [
        // S01E01, S01E01-E02, S01E01E02, S01E01-02
        new(TokenStart + @"S(?<s>\d{1,2})E(?<e>\d{1,3})(?:-?E?(?<e2>\d{1,3}))?" + TokenEnd,
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // 1x02, 6x23-24 and 6x23-6x24 for a double episode, 8x15a / 8x15b for the two halves of a split episode
        new(TokenStart + @"(?<s>\d{1,2})x(?<e>\d{1,3})(?:[ab]|-(?:\d{1,2}x)?(?<e2>\d{1,3}))?" + TokenEnd,
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // E02 at the start of the name
        new(@"^E(?<e>\d{1,3})" + TokenEnd, RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static readonly Regex BracketedYear = new(
        @"[(\[](?<y>(?:19|20)\d{2})[)\]]", RegexOptions.Compiled);

    private static readonly Regex BareYear = new(
        TokenStart + @"(?<y>(?:19|20)\d{2})" + TokenEnd, RegexOptions.Compiled);

    private static readonly Regex LeadingNumberPrefix = new(@"^(?<n>\d{1,3})\.\s+", RegexOptions.Compiled);
    private static readonly Regex TrailingNumber = new(@"^(?<rest>.*\S)\s+(?<n>\d{1,3})$", RegexOptions.Compiled);
    private static readonly Regex LeadingGroups = new(@"^\s*(?:\[[^\]]*\]\s*)+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@" {2,}", RegexOptions.Compiled);

    public static ParsedName Parse(string nameWithoutExtension)
    {
        var text = nameWithoutExtension.Trim();
        text = ExtractTmdbId(text, out var tmdbId);
        text = ExtractImdbId(text, out var imdbId);

        int? leadingNumber = null;
        var prefix = LeadingNumberPrefix.Match(text);
        if (prefix.Success)
        {
            leadingNumber = int.Parse(prefix.Groups["n"].Value);
            text = text[prefix.Length..];
        }

        if (!text.Contains(' '))
            text = text.Replace('.', ' ').Replace('_', ' ');

        var marker = FindEpisodeMarker(text);
        if (marker is null)
        {
            var movie = ParseWithoutMarker(text, leadingNumber);
            return movie with { TmdbId = tmdbId, ImdbId = imdbId };
        }

        var episode = ParseWithMarker(text[..marker.Index], text[(marker.Index + marker.Length)..], marker);
        return episode with { TmdbId = tmdbId, ImdbId = imdbId };
    }

    private static ParsedName ParseWithoutMarker(string text, int? leadingNumber)
    {
        text = LeadingGroups.Replace(text, "");

        var year = FindYear(text, allowAtStart: false);
        if (year is not null)
            return new ParsedName(Clean(text[..year.Index]), year.Value, null, [], [], leadingNumber, null, null);

        var title = Clean(CutAtNoise(text));
        var trailing = TrailingNumber.Match(title);
        if (leadingNumber is null && trailing.Success)
        {
            leadingNumber = int.Parse(trailing.Groups["n"].Value);
            title = trailing.Groups["rest"].Value;
        }
        return new ParsedName(title, null, null, [], [], leadingNumber, null, null);
    }

    private static ParsedName ParseWithMarker(string before, string after, EpisodeMarker marker)
    {
        before = LeadingGroups.Replace(before, "");

        var title = Clean(CutAtNoise(before));
        int? year = null;
        var yearInTitle = FindYear(before, allowAtStart: false);
        if (yearInTitle is not null)
        {
            year = yearInTitle.Value;
            title = Clean(CutAtNoise(before[..yearInTitle.Index]));
        }

        string? episodeTitle;
        var yearInTail = year is null ? FindYear(after, allowAtStart: true) : null;
        if (yearInTail is null)
        {
            episodeTitle = Clean(CutAtNoise(after));
        }
        else
        {
            year = yearInTail.Value;
            var between = Clean(CutAtNoise(after[..yearInTail.Index]));
            var tail = after[(yearInTail.Index + yearInTail.Length)..];
            (title, episodeTitle) = SplitAroundTailYear(title, between, tail);
        }

        IReadOnlyList<string> episodeTitles = string.IsNullOrEmpty(episodeTitle) ? [] : [episodeTitle];
        return new ParsedName(title, year, marker.Season, marker.Episodes, episodeTitles, null, null, null, yearInTail is not null);
    }

    /// <summary>
    /// Places the text around a year that follows the episode marker.
    /// With a title already known, the text before the year is the episode title,
    /// or the text after it when nothing sits before.
    /// Without a title, "A (year). B" reads as show A with episode B and "A (year) release" as episode A.
    /// </summary>
    private static (string Title, string? EpisodeTitle) SplitAroundTailYear(string title, string between, string tail)
    {
        if (title.Length > 0)
            return (title, between.Length > 0 ? between : Clean(CutAtNoise(tail)));

        if (between.Length == 0)
            return ("", Clean(CutAtNoise(tail)));

        var trimmedTail = tail.TrimStart();
        if (trimmedTail.StartsWith('.'))
        {
            var after = Clean(CutAtNoise(trimmedTail[1..]));
            if (after.Length > 0)
                return (between, after);
        }
        return ("", between);
    }

    private static string ExtractTmdbId(string text, out int? id)
    {
        var match = TmdbTag.Match(text);
        id = match.Success ? int.Parse(match.Groups["id"].Value) : null;
        return match.Success ? text.Remove(match.Index, match.Length).Trim() : text;
    }

    private static string ExtractImdbId(string text, out string? id)
    {
        var match = ImdbTag.Match(text);
        id = match.Success ? match.Groups["id"].Value.ToLowerInvariant() : null;
        return match.Success ? text.Remove(match.Index, match.Length).Trim() : text;
    }

    private static EpisodeMarker? FindEpisodeMarker(string text)
    {
        EpisodeMarker? best = null;
        foreach (var regex in EpisodeMarkers)
        {
            var match = regex.Match(text);
            if (!match.Success || (best is not null && match.Index >= best.Index))
                continue;

            var season = match.Groups["s"].Success ? int.Parse(match.Groups["s"].Value) : (int?)null;
            var first = int.Parse(match.Groups["e"].Value);
            var last = match.Groups["e2"].Success ? int.Parse(match.Groups["e2"].Value) : first;
            var episodes = last > first ? Enumerable.Range(first, last - first + 1).ToArray() : [first];

            // "[5x03]": swallow the brackets so they do not end up in the title or the episode title.
            var index = match.Index;
            var length = match.Length;
            if (index > 0 && text[index - 1] == '[' && index + length < text.Length && text[index + length] == ']')
            {
                index--;
                length += 2;
            }
            best = new EpisodeMarker(index, length, season, episodes);
        }
        return best;
    }

    /// <summary>
    /// A bracketed year wins. Otherwise the last bare year followed by nothing but release tags,
    /// falling back (title text only) to the last bare year not at the start.
    /// </summary>
    private static YearMatch? FindYear(string text, bool allowAtStart)
    {
        var bracketed = BracketedYear.Match(text);
        if (bracketed.Success)
            return new YearMatch(bracketed.Index, bracketed.Length, int.Parse(bracketed.Groups["y"].Value));

        var candidates = BareYear.Matches(text).Where(m => m.Index > 0 || allowAtStart).Reverse().ToList();
        var strict = candidates.FirstOrDefault(m =>
            CutAtNoise(text[(m.Index + m.Length)..]).Trim(Separators.ToCharArray()).Length == 0);

        // Release tags the noise list does not know (UHD, REMASTERED, DDP5.1, Atmos) can follow the year.
        // In a title the last bare year still ends it; after an episode marker the strict rule stays,
        // so a number inside an episode title is not read as a year.
        var match = strict ?? (allowAtStart ? null : candidates.FirstOrDefault());
        return match is null ? null : new YearMatch(match.Index, match.Length, int.Parse(match.Groups["y"].Value));
    }

    /// <summary>Returns the text before the first noise token.</summary>
    private static string CutAtNoise(string text)
    {
        text = text.TrimStart(Separators.ToCharArray());
        var noise = Noise.Match(text);
        return noise.Success ? text[..noise.Index] : text;
    }

    /// <summary>Turns dots and underscores into spaces in a segment that has none, then trims the edges.</summary>
    private static string Clean(string text)
    {
        text = text.Trim();
        if (!text.Contains(' '))
            text = text.Replace('.', ' ').Replace('_', ' ');
        return Spaces.Replace(text, " ").Trim(Separators.ToCharArray());
    }

    private sealed record EpisodeMarker(int Index, int Length, int? Season, IReadOnlyList<int> Episodes);

    private sealed record YearMatch(int Index, int Length, int Value);
}
