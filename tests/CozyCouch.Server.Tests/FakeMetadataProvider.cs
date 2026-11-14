using CozyCouch.Core.Metadata;

namespace CozyCouch.Server.Tests;

/// <summary>Answers search, title and season from what a test put in. Nothing else is used by the server.</summary>
public sealed class FakeMetadataProvider : IMetadataProvider
{
    public List<SearchCandidate> Hits { get; } = [];

    public Dictionary<(int TmdbId, TitleKind Kind), TitleDetails> Titles { get; } = [];

    public Dictionary<(int TmdbId, int Season), SeasonDetails> Seasons { get; } = [];

    public Task<IReadOnlyList<SearchCandidate>> SearchAnyAsync(string query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SearchCandidate>>(Hits);

    public Task<TitleDetails> GetTitleAsync(int tmdbId, TitleKind kind, CancellationToken ct) =>
        Titles.TryGetValue((tmdbId, kind), out var title) ? Task.FromResult(title) : throw new MetadataUnavailableException(UnavailableReason.Offline);

    public Task<SeasonDetails> GetSeasonAsync(int showTmdbId, int season, CancellationToken ct) =>
        Seasons.TryGetValue((showTmdbId, season), out var details) ? Task.FromResult(details) : throw new MetadataUnavailableException(UnavailableReason.Offline);

    public Task<IReadOnlyList<SearchCandidate>> SearchAsync(string title, int? year, TitleKind kind, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<(int TmdbId, TitleKind Kind)?> FindByImdbIdAsync(string imdbId, CancellationToken ct) =>
        throw new NotSupportedException();
}
