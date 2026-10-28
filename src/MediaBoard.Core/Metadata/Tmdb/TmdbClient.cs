using System.Globalization;
using System.Net;
using System.Text.Json;
using MediaBoard.Core.Activity;

namespace MediaBoard.Core.Metadata.Tmdb;

/// <summary>
/// The only place the app talks to TMDB. Failures become <see cref="MetadataUnavailableException"/> and a
/// matching <see cref="ActivityHub"/> state (online, key). Nothing here retries beyond the 429 back-off in
/// <see cref="RequestQueue"/>, and the API key never reaches a log line or an exception message.
/// </summary>
public sealed class TmdbClient : IMetadataProvider
{
    public const string BaseUrl = "https://api.themoviedb.org";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly Func<string?> _keyAccessor;
    private readonly ActivityHub _hub;
    private readonly RequestQueue _queue;

    public TmdbClient(HttpClient http, Func<string?> keyAccessor, ActivityHub hub, TimeProvider? timeProvider = null)
    {
        _http = http;
        _keyAccessor = keyAccessor;
        _hub = hub;
        _queue = new RequestQueue(timeProvider);
    }

    public async Task<IReadOnlyList<SearchCandidate>> SearchAsync(string title, int? year, TitleKind kind,
        CancellationToken ct)
    {
        var query = new List<KeyValuePair<string, string>> { new("query", title) };
        if (year is { } y)
            query.Add(new(kind == TitleKind.Movie ? "year" : "first_air_date_year", y.ToString(CultureInfo.InvariantCulture)));

        var response = await GetAsync<TmdbSearchResponse>(
            kind == TitleKind.Movie ? "/3/search/movie" : "/3/search/tv", query, ct).ConfigureAwait(false);
        return (response.Results ?? []).Select(item => ToCandidate(item, kind)).ToList();
    }

    public async Task<IReadOnlyList<SearchCandidate>> SearchAnyAsync(string query, CancellationToken ct)
    {
        var response = await GetAsync<TmdbSearchResponse>("/3/search/multi", [new("query", query)], ct)
            .ConfigureAwait(false);

        var candidates = new List<SearchCandidate>();
        foreach (var item in response.Results ?? [])
        {
            var kind = item.MediaType switch
            {
                "movie" => TitleKind.Movie,
                "tv" => TitleKind.Show,
                _ => (TitleKind?)null, // person, or a kind we do not know
            };
            if (kind is { } k) candidates.Add(ToCandidate(item, k));
        }

        return candidates;
    }

    public async Task<(int TmdbId, TitleKind Kind)?> FindByImdbIdAsync(string imdbId, CancellationToken ct)
    {
        var response = await GetAsync<TmdbFindResponse>(
            $"/3/find/{Uri.EscapeDataString(imdbId)}", [new("external_source", "imdb_id")], ct).ConfigureAwait(false);

        if (response.MovieResults is { Count: > 0 } movies) return (movies[0].Id, TitleKind.Movie);
        if (response.TvResults is { Count: > 0 } shows) return (shows[0].Id, TitleKind.Show);
        return null;
    }

    public async Task<TitleDetails> GetTitleAsync(int tmdbId, TitleKind kind, CancellationToken ct)
    {
        var id = tmdbId.ToString(CultureInfo.InvariantCulture);
        if (kind == TitleKind.Movie)
        {
            var movie = await GetAsync<TmdbMovieDetails>($"/3/movie/{id}", [], ct).ConfigureAwait(false);
            return new TitleDetails(movie.Id, TitleKind.Movie, movie.Title ?? "", ParseYear(movie.ReleaseDate), null,
                NullIfEmpty(movie.Overview), RatingOf(movie.VoteAverage, movie.VoteCount),
                NullIfEmpty(movie.PosterPath), movie.Runtime is > 0 ? movie.Runtime : null, []);
        }

        var show = await GetAsync<TmdbTvDetails>($"/3/tv/{id}", [], ct).ConfigureAwait(false);
        var ended = show.Status is "Ended" or "Canceled";
        return new TitleDetails(show.Id, TitleKind.Show, show.Name ?? "", ParseYear(show.FirstAirDate),
            ended ? ParseYear(show.LastAirDate) : null, NullIfEmpty(show.Overview),
            RatingOf(show.VoteAverage, show.VoteCount), NullIfEmpty(show.PosterPath),
            show.EpisodeRunTime is { Count: > 0 } runtimes && runtimes[0] > 0 ? runtimes[0] : null,
            (show.Seasons ?? []).Select(s => s.SeasonNumber).ToList());
    }

    public async Task<SeasonDetails> GetSeasonAsync(int showTmdbId, int season, CancellationToken ct)
    {
        var path = $"/3/tv/{showTmdbId.ToString(CultureInfo.InvariantCulture)}/season/{season.ToString(CultureInfo.InvariantCulture)}";
        var dto = await GetAsync<TmdbSeason>(path, [], ct).ConfigureAwait(false);

        var episodes = (dto.Episodes ?? [])
            .Select(e => new EpisodeDetails(e.SeasonNumber, e.EpisodeNumber, e.Name ?? "",
                e.Runtime is > 0 ? e.Runtime : null, ParseDate(e.AirDate)))
            .ToList();
        return new SeasonDetails(dto.SeasonNumber, episodes);
    }

    private async Task<T> GetAsync<T>(string path, List<KeyValuePair<string, string>> query, CancellationToken ct)
        where T : class
    {
        var key = _keyAccessor();
        if (string.IsNullOrWhiteSpace(key))
        {
            _hub.SetKeyState(KeyState.Missing);
            throw new MetadataUnavailableException(UnavailableReason.NoKey, "No TMDB key is set.");
        }

        key = key.Trim();
        query.Add(new("language", "en-US"));

        HttpResponseMessage response;
        try
        {
            response = await _queue.SendAsync(_http, () => TmdbAuth.CreateRequest(BaseUrl, path, key, query), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // No HTTP status means we never reached TMDB. The inner message is dropped on purpose.
            _hub.SetOnline(false);
            _hub.Log(ActivityLevel.Warning, $"TMDB is unreachable ({ex.GetType().Name}).");
            throw new MetadataUnavailableException(UnavailableReason.Offline, "TMDB is unreachable.");
        }
        catch (MetadataUnavailableException ex) when (ex.Reason == UnavailableReason.ServerError)
        {
            _hub.SetOnline(true); // rate limited: the server answered
            _hub.Log(ActivityLevel.Warning, ex.Message);
            throw;
        }

        using (response)
        {
            _hub.SetOnline(true);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _hub.SetKeyState(KeyState.Rejected);
                _hub.Log(ActivityLevel.Error, "TMDB rejected the API key.");
                throw new MetadataUnavailableException(UnavailableReason.KeyRejected, "TMDB rejected the API key.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = $"TMDB answered {(int)response.StatusCode}.";
                _hub.Log(ActivityLevel.Warning, message);
                throw new MetadataUnavailableException(UnavailableReason.ServerError, message);
            }

            _hub.SetKeyState(KeyState.Ok);
            try
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return JsonSerializer.Deserialize<T>(body, Json)
                       ?? throw new JsonException("Empty response.");
            }
            catch (JsonException)
            {
                _hub.Log(ActivityLevel.Warning, "TMDB sent a response we could not read.");
                throw new MetadataUnavailableException(UnavailableReason.ServerError, "TMDB sent an unreadable response.");
            }
        }
    }

    private static SearchCandidate ToCandidate(TmdbSearchItem item, TitleKind kind)
    {
        var movie = kind == TitleKind.Movie;
        return new SearchCandidate(item.Id, kind,
            (movie ? item.Title : item.Name) ?? "",
            NullIfEmpty(movie ? item.OriginalTitle : item.OriginalName),
            ParseYear(movie ? item.ReleaseDate : item.FirstAirDate),
            item.Popularity,
            RatingOf(item.VoteAverage, item.VoteCount),
            NullIfEmpty(item.PosterPath),
            NullIfEmpty(item.Overview));
    }

    /// <summary>TMDB reports 0.0 for titles nobody has rated; that is "no rating", not a zero.</summary>
    private static double? RatingOf(double average, int votes) => votes > 0 ? average : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? ParseYear(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year > 0
            ? year
            : null;

    private static DateOnly? ParseDate(string? date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
