using System.Xml;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Identification;

/// <summary>
/// Decides which TMDB movie or show, and which episodes, one parsed file is. A hard id (name tag or nfo)
/// is certain and needs no search. Otherwise it searches, retrying shorter titles and then without the
/// year, scores the candidates, and accepts a clear winner or sends the file to review.
/// </summary>
public sealed class Identifier(IMetadataProvider provider)
{
    private const int ReviewCandidateCount = 5;
    private const int NfoParentLevels = 2;
    private const double MarginTolerance = 1e-9;
    private const string TvShowNfo = "tvshow.nfo";
    private const string MovieNfo = "movie.nfo";
    private const string EpisodeNfoRoot = "episodedetails";

    private sealed record Scored(SearchCandidate Candidate, double Score);

    private sealed class SearchState
    {
        public Dictionary<(int, TitleKind), Scored> Pool { get; } = [];
        public Dictionary<(int, TitleKind), TitleDetails> Details { get; } = [];

        public List<Scored> Ranked() => [.. Pool.Values.OrderByDescending(s => s.Score)];
    }

    /// <param name="durationS">Probed length of the file, when known.</param>
    public async Task<IdentificationResult> IdentifyAsync(
        ParsedMedia parsed, string fullPath, double? durationS, CancellationToken ct)
    {
        try
        {
            if (await FindHardIdAsync(parsed, fullPath, ct).ConfigureAwait(false) is { } hard)
            {
                var title = await provider.GetTitleAsync(hard.TmdbId, hard.Kind, ct).ConfigureAwait(false);
                return await ResolveAsync(parsed, title, 1.0, "id_tag", [], ct).ConfigureAwait(false);
            }

            return await SearchAndDecideAsync(parsed, durationS, ct).ConfigureAwait(false);
        }
        catch (MetadataUnavailableException e)
        {
            return Unavailable(e.Reason);
        }
    }

    private static IdentificationResult Unavailable(UnavailableReason reason) => new(
        reason switch
        {
            UnavailableReason.NoKey => IdentificationStatus.NoKey,
            UnavailableReason.KeyRejected => IdentificationStatus.KeyRejected,
            _ => IdentificationStatus.Offline,
        },
        null, [], 0, [], "none");

    // ---- Hard ids ----

    private async Task<(int TmdbId, TitleKind Kind)?> FindHardIdAsync(ParsedMedia parsed, string fullPath, CancellationToken ct)
    {
        var fileKind = parsed.Kind == MediaKind.Episode ? TitleKind.Show : TitleKind.Movie;

        if (await FromIdsAsync(parsed.TmdbId, parsed.ImdbId, fileKind, ct).ConfigureAwait(false) is { } tagged)
        {
            return tagged;
        }

        foreach (var nfo in NfoCandidates(parsed, fullPath))
        {
            var (tmdbId, imdbId) = NfoReader.ReadIds(nfo);
            if (await FromIdsAsync(tmdbId, imdbId, fileKind, ct).ConfigureAwait(false) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private async Task<(int TmdbId, TitleKind Kind)?> FromIdsAsync(int? tmdbId, string? imdbId, TitleKind fileKind, CancellationToken ct)
    {
        if (tmdbId is > 0) return (tmdbId.Value, fileKind);
        if (string.IsNullOrWhiteSpace(imdbId)) return null;

        return await provider.FindByImdbIdAsync(imdbId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The file's own nfo, then tvshow.nfo (episodes) or movie.nfo (anything else) in the file's folder and
    /// up to two parents. An episode's own nfo holds the episode's ids, not the show's, so it is skipped.
    /// </summary>
    private static IEnumerable<string> NfoCandidates(ParsedMedia parsed, string fullPath)
    {
        var own = Path.ChangeExtension(fullPath, ".nfo");
        if (File.Exists(own) && !(parsed.Kind == MediaKind.Episode && IsEpisodeNfo(own)))
        {
            yield return own;
        }

        var name = parsed.Kind == MediaKind.Episode ? TvShowNfo : MovieNfo;
        var dir = Path.GetDirectoryName(fullPath);
        for (var level = 0; level <= NfoParentLevels && !string.IsNullOrEmpty(dir); level++)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) yield return path;

            dir = Path.GetDirectoryName(dir);
        }
    }

    private static bool IsEpisodeNfo(string path)
    {
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            return reader.MoveToContent() == XmlNodeType.Element
                   && string.Equals(reader.LocalName, EpisodeNfoRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- Search ----

    private async Task<IdentificationResult> SearchAndDecideAsync(ParsedMedia parsed, double? durationS, CancellationToken ct)
    {
        var kinds = parsed.Kind switch
        {
            MediaKind.Episode => new[] { TitleKind.Show },
            MediaKind.Movie => [TitleKind.Movie],
            _ => [TitleKind.Movie, TitleKind.Show],
        };

        var state = new SearchState();
        foreach (var (title, year) in Attempts(parsed))
        {
            foreach (var kind in kinds)
            {
                var found = await provider.SearchAsync(title, year, kind, ct).ConfigureAwait(false);
                foreach (var c in found)
                {
                    var score = Score(parsed, title, c, null, null);
                    if (!state.Pool.TryGetValue((c.TmdbId, c.Kind), out var known) || score > known.Score)
                    {
                        state.Pool[(c.TmdbId, c.Kind)] = new Scored(c, score);
                    }
                }
            }

            await VerifyMovieRuntimesAsync(parsed, durationS, state, ct).ConfigureAwait(false);

            var ranked = state.Ranked();
            if (ranked.Count > 0 && ranked[0].Score >= ScoringWeights.AcceptThreshold) break;
        }

        return await DecideAsync(parsed, state, ct).ConfigureAwait(false);
    }

    /// <summary>The query sequence: each shortening of the title with the year, then the same without it.</summary>
    private static IEnumerable<(string Title, int? Year)> Attempts(ParsedMedia parsed)
    {
        var words = parsed.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) yield break;

        var titles = Enumerable.Range(1, words.Length).Reverse().Select(n => string.Join(' ', words.Take(n))).ToList();

        foreach (var title in titles) yield return (title, parsed.Year);

        if (parsed.Year is not null)
        {
            foreach (var title in titles) yield return (title, null);
        }
    }

    /// <summary>
    /// A movie that could be accepted is checked against its TMDB runtime, which can pull its score down.
    /// Only candidates at or above the threshold matter, since the check never raises a score.
    /// </summary>
    private async Task VerifyMovieRuntimesAsync(ParsedMedia parsed, double? durationS, SearchState state, CancellationToken ct)
    {
        if (durationS is not > 0) return;

        foreach (var scored in state.Pool.Values.ToList())
        {
            var c = scored.Candidate;
            if (c.Kind != TitleKind.Movie || scored.Score < ScoringWeights.AcceptThreshold) continue;
            if (state.Details.ContainsKey((c.TmdbId, c.Kind))) continue;

            var details = await provider.GetTitleAsync(c.TmdbId, c.Kind, ct).ConfigureAwait(false);
            state.Details[(c.TmdbId, c.Kind)] = details;
            state.Pool[(c.TmdbId, c.Kind)] = new Scored(c, Score(parsed, parsed.Title, c, durationS, details.RuntimeMin));
        }
    }

    private async Task<IdentificationResult> DecideAsync(ParsedMedia parsed, SearchState state, CancellationToken ct)
    {
        var ranked = state.Ranked();
        var top = ranked.Take(ReviewCandidateCount).Select(s => s.Candidate).ToList();

        if (ranked.Count == 0) return Review(0, top);

        var best = ranked[0];
        var accepted = best.Score >= ScoringWeights.AcceptThreshold
                       && (ranked.Count == 1 || best.Score - ranked[1].Score >= ScoringWeights.AcceptMargin - MarginTolerance);
        if (!accepted) return Review(best.Score, top);

        var key = (best.Candidate.TmdbId, best.Candidate.Kind);
        if (!state.Details.TryGetValue(key, out var title))
        {
            title = await provider.GetTitleAsync(key.TmdbId, key.Kind, ct).ConfigureAwait(false);
        }

        return await ResolveAsync(parsed, title, best.Score, "auto", top, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Scores a candidate against the title that was searched for (a shortened title after a retry).
    /// For an episode year the file name carries the air date, which is usually not the show's start year,
    /// so a show whose year differs is scored as if the file had no year.
    /// </summary>
    private static double Score(ParsedMedia parsed, string queryTitle, SearchCandidate c, double? durationS, int? runtimeMin)
    {
        var forCandidate = parsed with
        {
            Title = queryTitle,
            Year = parsed.Kind == MediaKind.Episode && parsed.Year != c.Year ? null : parsed.Year,
        };
        return CandidateScorer.Score(forCandidate, c, durationS, runtimeMin);
    }

    // ---- Result ----

    private async Task<IdentificationResult> ResolveAsync(
        ParsedMedia parsed, TitleDetails title, double confidence, string source,
        IReadOnlyList<SearchCandidate> candidates, CancellationToken ct)
    {
        if (title.Kind == TitleKind.Movie)
        {
            return new IdentificationResult(IdentificationStatus.Matched, title, [], confidence, candidates, source);
        }

        var episodes = await ResolveEpisodesAsync(parsed, title, ct).ConfigureAwait(false);
        if (episodes is null)
        {
            return Review(confidence, candidates.Count > 0 ? candidates : [ToCandidate(title)]);
        }

        return new IdentificationResult(IdentificationStatus.Matched, title, episodes, confidence, candidates, source);
    }

    private async Task<IReadOnlyList<EpisodeDetails>?> ResolveEpisodesAsync(ParsedMedia parsed, TitleDetails show, CancellationToken ct)
    {
        var number = parsed.Season ?? 1;
        if (show.SeasonNumbers.Count > 0 && !show.SeasonNumbers.Contains(number)) return null;

        var season = await provider.GetSeasonAsync(show.TmdbId, number, ct).ConfigureAwait(false);
        var specials = number != 0 && show.SeasonNumbers.Contains(0)
            ? await provider.GetSeasonAsync(show.TmdbId, 0, ct).ConfigureAwait(false)
            : null;

        return EpisodeResolver.Resolve(parsed, season, specials);
    }

    private static IdentificationResult Review(double confidence, IReadOnlyList<SearchCandidate> candidates) =>
        new(IdentificationStatus.NeedsReview, null, [], confidence, candidates, "auto");

    private static SearchCandidate ToCandidate(TitleDetails t) =>
        new(t.TmdbId, t.Kind, t.Name, null, t.Year, 0, t.Rating, t.PosterPath, t.Overview);
}
