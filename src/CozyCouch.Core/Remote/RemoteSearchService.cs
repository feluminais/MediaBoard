using System.Globalization;
using CozyCouch.Core.Library;
using CozyCouch.Core.Metadata;

namespace CozyCouch.Core.Remote;

/// <summary>
/// The palette's search for a viewer. The server asks TMDB with the owner's key, so none reaches this PC; a TMDB
/// answer that can't be had is the same <see cref="OnlineState"/> it is locally, and the library part still shows.
/// </summary>
public sealed class RemoteSearchService(ApiClient client) : ISearchService
{
    private const int MaxKeptSeasons = 300;

    // Only the commands that work for a viewer: a rescan is the owner's, and hidden items never leave their PC.
    private static readonly IReadOnlyList<PaletteCommand> AllCommands = [new("settings", "Open settings")];

    private readonly Dictionary<(int TmdbId, int Season), SeasonDetails> _seasons = [];

    internal static string LocalRoute(string query, int limit) =>
        string.Create(CultureInfo.InvariantCulture, $"search/local?q={Uri.EscapeDataString(query)}&limit={limit}");

    public async Task<SearchResults> SearchAsync(string query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = query.Trim();

        if (text.Length == 0) return new SearchResults([], [], OnlineState.TooShort);
        if (text.Length < SearchService.MinOnlineQueryLength) return new SearchResults(await LocalAsync(text, ct), [], OnlineState.TooShort);

        // The one request answers for both groups. The library's own route is for when it fails.
        try
        {
            return await client.GetAsync<SearchResults>($"search?q={Uri.EscapeDataString(text)}", ct);
        }
        catch (Exception e) when (e is ServerUnreachableException or ApiException)
        {
            return new SearchResults(await LocalAsync(text, ct), [], OnlineState.Offline);
        }
    }

    public async Task<TitleDetails?> GetOnlineDetailsAsync(int tmdbId, TitleKind kind, CancellationToken ct)
    {
        var group = kind == TitleKind.Movie ? "movie" : "tv";
        return await TryAsync(() => client.GetAsync<TitleDetails>(string.Create(CultureInfo.InvariantCulture, $"online/{group}/{tmdbId}"), ct));
    }

    public async Task<SeasonDetails?> GetOnlineSeasonAsync(int tmdbId, int season, CancellationToken ct)
    {
        var key = (tmdbId, season);
        lock (_seasons)
        {
            if (_seasons.TryGetValue(key, out var kept)) return kept;
        }

        var details = await TryAsync(() => client.GetAsync<SeasonDetails>(string.Create(CultureInfo.InvariantCulture, $"online/tv/{tmdbId}/seasons/{season}"), ct));
        if (details is null) return null;

        lock (_seasons)
        {
            if (_seasons.Count >= MaxKeptSeasons) _seasons.Clear();
            _seasons[key] = details;
        }

        return details;
    }

    public IReadOnlyList<PaletteCommand> Commands(string filter) => PaletteCommands.Filter(AllCommands, filter);

    private async Task<IReadOnlyList<TitleCard>> LocalAsync(string text, CancellationToken ct) =>
        await client.GetAsync<List<TitleCard>>(LocalRoute(text, SearchService.MaxLibraryResults), ct);

    // Null is what the local service gives when TMDB can't answer; the caller can't tell a refusal from a dead link.
    private static async Task<T?> TryAsync<T>(Func<Task<T>> ask)
        where T : class
    {
        try
        {
            return await ask();
        }
        catch (Exception e) when (e is ServerUnreachableException or ApiException)
        {
            return null;
        }
    }
}
