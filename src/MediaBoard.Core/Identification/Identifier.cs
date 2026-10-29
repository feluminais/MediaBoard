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

    /// <summary>
    /// One pool candidate. <see cref="BaseScore"/> is the best score over all attempts (each already reduced
    /// for dropped words); the duration penalty is kept apart so a later attempt cannot undo it.
    /// </summary>
    private sealed record Entry(SearchCandidate Candidate, double BaseScore, bool DurationMismatch)
    {
        public double Score => Math.Clamp(BaseScore - (DurationMismatch ? ScoringWeights.DurationPenalty : 0.0), 0.0, 1.0);
    }

    private sealed class SearchState
    {
        public Dictionary<(int, TitleKind), Entry> Pool { get; } = [];
        public Dictionary<(int, TitleKind), TitleDetails> Details { get; } = [];

        public List<Entry> Ranked() => [.. Pool.Values.OrderByDescending(e => e.Score)];
    }

    /// <param name="durationS">Probed length of the file, when known.</param>
    public async Task<IdentificationResult> IdentifyAsync(
        ParsedMedia parsed, string fullPath, double? durationS, CancellationToken ct)
    {
        try
        {
            if (await FindHardIdTitleAsync(parsed, fullPath, ct).ConfigureAwait(false) is { } title)
            {
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

    /// <summary>
    /// The title a name tag or nfo id points to. An id TMDB answers with a server error for (a stale or wrong
    /// id gives a 404) is skipped, so the next source or the normal search decides instead.
    /// </summary>
    private async Task<TitleDetails?> FindHardIdTitleAsync(ParsedMedia parsed, string fullPath, CancellationToken ct)
    {
        var fileKind = parsed.Kind == MediaKind.Episode ? TitleKind.Show : TitleKind.Movie;

        if (await TitleFromIdsAsync(parsed.TmdbId, parsed.ImdbId, fileKind, ct).ConfigureAwait(false) is { } tagged)
        {
            return tagged;
        }

        foreach (var nfo in NfoCandidates(parsed, fullPath))
        {
            var (tmdbId, imdbId) = NfoReader.ReadIds(nfo);
            if (await TitleFromIdsAsync(tmdbId, imdbId, fileKind, ct).ConfigureAwait(false) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private async Task<TitleDetails?> TitleFromIdsAsync(int? tmdbId, string? imdbId, TitleKind fileKind, CancellationToken ct)
    {
        if (tmdbId is > 0 && await TryGetTitleAsync(tmdbId.Value, fileKind, ct).ConfigureAwait(false) is { } byTmdb)
        {
            return byTmdb;
        }

        if (string.IsNullOrWhiteSpace(imdbId)) return null;

        try
        {
            return await provider.FindByImdbIdAsync(imdbId, ct).ConfigureAwait(false) is { } found
                ? await TryGetTitleAsync(found.TmdbId, found.Kind, ct).ConfigureAwait(false)
                : null;
        }
        catch (MetadataUnavailableException e) when (e.Reason == UnavailableReason.ServerError)
        {
            return null;
        }
    }

    private async Task<TitleDetails?> TryGetTitleAsync(int tmdbId, TitleKind kind, CancellationToken ct)
    {
        try
        {
            return await provider.GetTitleAsync(tmdbId, kind, ct).ConfigureAwait(false);
        }
        catch (MetadataUnavailableException e) when (e.Reason == UnavailableReason.ServerError)
        {
            return null;
        }
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
        foreach (var (title, year, dropped) in Attempts(parsed))
        {
            foreach (var kind in kinds)
            {
                var found = await provider.SearchAsync(title, year, kind, ct).ConfigureAwait(false);
                foreach (var c in found)
                {
                    var score = Math.Max(0.0, Score(parsed, title, c) - ScoringWeights.ShortenedWordPenalty * dropped);
                    var key = (c.TmdbId, c.Kind);
                    state.Pool[key] = state.Pool.TryGetValue(key, out var known)
                        ? known with { BaseScore = Math.Max(known.BaseScore, score) }
                        : new Entry(c, score, false);
                }
            }

            await VerifyMovieRuntimesAsync(parsed, durationS, state, ct).ConfigureAwait(false);

            var ranked = state.Ranked();
            if (ranked.Count > 0 && ranked[0].Score >= ScoringWeights.AcceptThreshold) break;
        }

        return await DecideAsync(parsed, state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The query sequence: each shortening of the title with the year, then the same without it. Dropped is
    /// the number of words left out of the parsed title.
    /// </summary>
    private static IEnumerable<(string Title, int? Year, int Dropped)> Attempts(ParsedMedia parsed)
    {
        var words = parsed.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) yield break;

        var titles = Enumerable.Range(1, words.Length).Reverse()
            .Select(n => (Title: string.Join(' ', words.Take(n)), Dropped: words.Length - n))
            .ToList();

        foreach (var (title, dropped) in titles) yield return (title, parsed.Year, dropped);

        if (parsed.Year is not null)
        {
            foreach (var (title, dropped) in titles) yield return (title, null, dropped);
        }
    }

    /// <summary>
    /// A movie that could be accepted is checked once against its TMDB runtime. A mismatch is stored on the
    /// pool entry, so later attempts that return the same film cannot undo it. Only candidates whose base score
    /// reaches the threshold matter, since the penalty never raises a score.
    /// </summary>
    private async Task VerifyMovieRuntimesAsync(ParsedMedia parsed, double? durationS, SearchState state, CancellationToken ct)
    {
        if (durationS is not > 0) return;

        foreach (var entry in state.Pool.Values.ToList())
        {
            var c = entry.Candidate;
            var key = (c.TmdbId, c.Kind);
            if (c.Kind != TitleKind.Movie || entry.BaseScore < ScoringWeights.AcceptThreshold) continue;
            if (state.Details.ContainsKey(key)) continue;

            var details = await provider.GetTitleAsync(c.TmdbId, c.Kind, ct).ConfigureAwait(false);
            state.Details[key] = details;

            var mismatch = CandidateScorer.Score(parsed, c, durationS, details.RuntimeMin)
                           < CandidateScorer.Score(parsed, c, null, null);
            state.Pool[key] = entry with { DurationMismatch = mismatch };
        }
    }

    private async Task<IdentificationResult> DecideAsync(ParsedMedia parsed, SearchState state, CancellationToken ct)
    {
        var ranked = state.Ranked();
        var top = ranked.Take(ReviewCandidateCount).Select(e => e.Candidate).ToList();

        if (ranked.Count == 0) return Review(0, top);

        var best = ranked[0];
        var accepted = best.Score >= ScoringWeights.AcceptThreshold
                       && (ranked.Count == 1
                           || best.Score - ranked[1].Score >= ScoringWeights.AcceptMargin - MarginTolerance
                           || LeadsOnVotes(parsed, ranked));
        if (!accepted) return Review(best.Score, top);

        var key = (best.Candidate.TmdbId, best.Candidate.Kind);
        if (!state.Details.TryGetValue(key, out var title))
        {
            title = await provider.GetTitleAsync(key.TmdbId, key.Kind, ct).ConfigureAwait(false);
        }

        return await ResolveAsync(parsed, title, best.Score, "auto", top, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Two titles with the same name and no year to tell them apart ("Supernatural" 2005 and 1977): the leader
    /// is still accepted when every candidate inside the margin ties with it on title and it has far more
    /// votes than each of them. A known year decides on its own, so this never applies with one.
    /// </summary>
    private static bool LeadsOnVotes(ParsedMedia parsed, List<Entry> ranked)
    {
        if (parsed.Year is not null) return false;

        var best = ranked[0];
        if (best.Candidate.VoteCount < ScoringWeights.VoteLeadMinimum) return false;

        var bestTitle = CandidateScorer.TitleScore(parsed.Title, best.Candidate);
        return ranked.Skip(1)
            .TakeWhile(e => best.Score - e.Score < ScoringWeights.AcceptMargin - MarginTolerance)
            .All(e => Math.Abs(CandidateScorer.TitleScore(parsed.Title, e.Candidate) - bestTitle) <= MarginTolerance
                      && best.Candidate.VoteCount >= ScoringWeights.VoteLeadFactor * e.Candidate.VoteCount);
    }

    /// <summary>Scores a candidate against the title that was searched for (a shortened title after a retry).</summary>
    private static double Score(ParsedMedia parsed, string queryTitle, SearchCandidate c) =>
        CandidateScorer.Score(parsed with { Title = queryTitle }, c, null, null);

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
        if (show.SeasonNumbers.Count > 0 && !show.SeasonNumbers.Contains(number))
        {
            return await ResolveInOtherSeasonsAsync(parsed, show, number, null, ct).ConfigureAwait(false);
        }

        var season = await provider.GetSeasonAsync(show.TmdbId, number, ct).ConfigureAwait(false);

        // The resolver reads season 0 only to match an episode title, so a file without one never needs it.
        var specials = number != 0 && parsed.EpisodeTitles.Count > 0 && show.SeasonNumbers.Contains(0)
            ? await provider.GetSeasonAsync(show.TmdbId, 0, ct).ConfigureAwait(false)
            : null;

        if (EpisodeResolver.Resolve(parsed, season, specials) is { } episodes) return episodes;

        // Numbers TMDB has, with a title it does not know: nothing suggests a different numbering.
        if (parsed.Episodes.All(n => season.Episodes.Any(e => e.Number == n))) return null;

        return await ResolveInOtherSeasonsAsync(parsed, show, number, specials, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A file numbered differently from TMDB (Amazon's Futurama "S04E13 Bend Her" is TMDB's S05E13) is found by
    /// its episode title in the show's other seasons. Season 0 counts only when it was already read. The run's
    /// cache fetches each season once, however many files ask.
    /// </summary>
    private async Task<IReadOnlyList<EpisodeDetails>?> ResolveInOtherSeasonsAsync(
        ParsedMedia parsed, TitleDetails show, int number, SeasonDetails? specials, CancellationToken ct)
    {
        if (parsed.EpisodeTitles.Count == 0) return null;

        var seasons = new List<SeasonDetails>();
        if (specials is not null) seasons.Add(specials);
        foreach (var other in show.SeasonNumbers.Where(n => n != 0 && n != number))
        {
            try
            {
                seasons.Add(await provider.GetSeasonAsync(show.TmdbId, other, ct).ConfigureAwait(false));
            }
            catch (MetadataUnavailableException e) when (e.Reason == UnavailableReason.ServerError)
            {
                // A season TMDB lists but cannot serve is left out.
            }
        }

        return EpisodeResolver.ResolveAcrossSeasons(parsed, seasons);
    }

    private static IdentificationResult Review(double confidence, IReadOnlyList<SearchCandidate> candidates) =>
        new(IdentificationStatus.NeedsReview, null, [], confidence, candidates, "auto");

    private static SearchCandidate ToCandidate(TitleDetails t) =>
        new(t.TmdbId, t.Kind, t.Name, null, t.Year, 0, t.Rating, t.PosterPath, t.Overview);
}
