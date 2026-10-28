using MediaBoard.Core.Metadata;

namespace MediaBoard.Core.Tests.Support;

/// <summary>
/// In-memory <see cref="IMetadataProvider"/>. Searches answer only for the exact (title, year, kind) that was
/// registered, everything else is empty. Every call is recorded.
/// </summary>
public sealed class FakeMetadataProvider : IMetadataProvider
{
    private readonly Dictionary<(string Title, int? Year, TitleKind Kind), SearchCandidate[]> _searches = [];
    private readonly Dictionary<string, (int TmdbId, TitleKind Kind)> _imdb = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int TmdbId, TitleKind Kind), TitleDetails> _titles = [];
    private readonly Dictionary<(int Show, int Season), SeasonDetails> _seasons = [];

    public List<(string Title, int? Year, TitleKind Kind)> Searches { get; } = [];
    public List<string> ImdbLookups { get; } = [];
    public List<(int TmdbId, TitleKind Kind)> TitleFetches { get; } = [];
    public List<(int Show, int Season)> SeasonFetches { get; } = [];

    /// <summary>When set, every call throws <see cref="MetadataUnavailableException"/> with this reason.</summary>
    public UnavailableReason? Unavailable { get; set; }

    public FakeMetadataProvider OnSearch(string title, int? year, TitleKind kind, params SearchCandidate[] results)
    {
        _searches[(title.ToLowerInvariant(), year, kind)] = results;
        return this;
    }

    public FakeMetadataProvider OnImdb(string imdbId, int tmdbId, TitleKind kind)
    {
        _imdb[imdbId] = (tmdbId, kind);
        return this;
    }

    public FakeMetadataProvider OnTitle(TitleDetails title)
    {
        _titles[(title.TmdbId, title.Kind)] = title;
        return this;
    }

    public FakeMetadataProvider OnSeason(int showTmdbId, SeasonDetails season)
    {
        _seasons[(showTmdbId, season.Season)] = season;
        return this;
    }

    public Task<IReadOnlyList<SearchCandidate>> SearchAsync(string title, int? year, TitleKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        Searches.Add((title, year, kind));
        IReadOnlyList<SearchCandidate> found = _searches.TryGetValue((title.ToLowerInvariant(), year, kind), out var hit) ? hit : [];
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<SearchCandidate>> SearchAnyAsync(string query, CancellationToken ct)
    {
        ThrowIfUnavailable();
        return Task.FromResult<IReadOnlyList<SearchCandidate>>([]);
    }

    public Task<(int TmdbId, TitleKind Kind)?> FindByImdbIdAsync(string imdbId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        ImdbLookups.Add(imdbId);
        return Task.FromResult<(int TmdbId, TitleKind Kind)?>(_imdb.TryGetValue(imdbId, out var hit) ? hit : null);
    }

    public Task<TitleDetails> GetTitleAsync(int tmdbId, TitleKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        TitleFetches.Add((tmdbId, kind));
        return _titles.TryGetValue((tmdbId, kind), out var hit)
            ? Task.FromResult(hit)
            : throw new MetadataUnavailableException(UnavailableReason.ServerError, "TMDB answered 404.");
    }

    public Task<SeasonDetails> GetSeasonAsync(int showTmdbId, int season, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        SeasonFetches.Add((showTmdbId, season));
        return _seasons.TryGetValue((showTmdbId, season), out var hit)
            ? Task.FromResult(hit)
            : throw new MetadataUnavailableException(UnavailableReason.ServerError, "TMDB answered 404.");
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable is { } reason) throw new MetadataUnavailableException(reason);
    }
}
