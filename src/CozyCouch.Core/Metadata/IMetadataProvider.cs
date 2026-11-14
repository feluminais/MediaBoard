namespace CozyCouch.Core.Metadata;

/// <summary>
/// Source of titles, posters, ratings and episode lists. Every method throws
/// <see cref="MetadataUnavailableException"/> when the source cannot answer.
/// </summary>
public interface IMetadataProvider
{
    Task<IReadOnlyList<SearchCandidate>> SearchAsync(string title, int? year, TitleKind kind, CancellationToken ct);

    /// <summary>Movies and shows matching a free-text query, for the search palette.</summary>
    Task<IReadOnlyList<SearchCandidate>> SearchAnyAsync(string query, CancellationToken ct);

    Task<(int TmdbId, TitleKind Kind)?> FindByImdbIdAsync(string imdbId, CancellationToken ct);

    Task<TitleDetails> GetTitleAsync(int tmdbId, TitleKind kind, CancellationToken ct);

    Task<SeasonDetails> GetSeasonAsync(int showTmdbId, int season, CancellationToken ct);
}
