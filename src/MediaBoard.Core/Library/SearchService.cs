using MediaBoard.Core.Metadata;
using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Library;

public sealed class SearchService(
    ILibrary library,
    IMetadataProvider provider,
    Connectivity connectivity,
    TitleStore titles,
    ViewerAccess? access = null,
    Viewer? viewer = null) : ISearchService
{
    public const int MaxLibraryResults = 10;
    public const int MaxOnlineResults = 10;
    public const int MinOnlineQueryLength = 2;
    private const int MaxKeptSeasons = 300;

    private readonly Dictionary<(int TmdbId, int Season), SeasonDetails> _seasons = [];

    private static readonly IReadOnlyList<PaletteCommand> AllCommands =
    [
        new("rescan", "Rescan library"),
        new("settings", "Open settings"),
        new("hidden", "Show hidden items"),
    ];

    public async Task<SearchResults> SearchAsync(string query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = query.Trim();

        // Local first: the palette shows these while TMDB is still thinking.
        IReadOnlyList<TitleCard> local = text.Length == 0 ? [] : library.SearchLocal(text, MaxLibraryResults);

        if (text.Length < MinOnlineQueryLength) return new SearchResults(local, [], OnlineState.TooShort);
        if (!connectivity.IsOnline) return new SearchResults(local, [], OnlineState.Offline);

        try
        {
            var found = await provider.SearchAnyAsync(text, ct).ConfigureAwait(false);
            var online = found.Take(MaxOnlineResults)
                .Select(c => new OnlineResult(c, ReachableTitleId(c)))
                .ToList();
            return new SearchResults(local, online, OnlineState.Ok);
        }
        catch (MetadataUnavailableException e)
        {
            return new SearchResults(local, [], StateOf(e.Reason));
        }
    }

    /// <summary>The local title of an online hit, left out when the viewer can't reach it so a search doesn't reveal it.</summary>
    private int? ReachableTitleId(SearchCandidate candidate)
    {
        var id = titles.ByTmdb(candidate.TmdbId, candidate.Kind)?.Id;
        if (id is null || viewer is not { SeesHidden: false }) return id;

        ArgumentNullException.ThrowIfNull(access);
        return access.CanReachTitle(viewer, id.Value) ? id : null;
    }

    public async Task<TitleDetails?> GetOnlineDetailsAsync(int tmdbId, TitleKind kind, CancellationToken ct)
    {
        if (!connectivity.IsOnline) return null;

        try
        {
            return await provider.GetTitleAsync(tmdbId, kind, ct).ConfigureAwait(false);
        }
        catch (MetadataUnavailableException e)
        {
            if (e.Reason == UnavailableReason.Offline) connectivity.ReportOffline();
            return null;
        }
    }

    public async Task<SeasonDetails?> GetOnlineSeasonAsync(int tmdbId, int season, CancellationToken ct)
    {
        var key = (tmdbId, season);
        lock (_seasons)
        {
            if (_seasons.TryGetValue(key, out var kept)) return kept;
        }

        if (!connectivity.IsOnline) return null;

        try
        {
            var details = await provider.GetSeasonAsync(tmdbId, season, ct).ConfigureAwait(false);
            lock (_seasons)
            {
                if (_seasons.Count >= MaxKeptSeasons) _seasons.Clear(); // a few hundred seasons is a long session
                _seasons[key] = details;
            }

            return details;
        }
        catch (MetadataUnavailableException e)
        {
            if (e.Reason == UnavailableReason.Offline) connectivity.ReportOffline();
            return null;
        }
    }

    public IReadOnlyList<PaletteCommand> Commands(string filter) => PaletteCommands.Filter(AllCommands, filter);

    private OnlineState StateOf(UnavailableReason reason)
    {
        switch (reason)
        {
            case UnavailableReason.NoKey:
                return OnlineState.NoKey;
            case UnavailableReason.KeyRejected:
                return OnlineState.KeyRejected;
            case UnavailableReason.Offline:
                connectivity.ReportOffline();
                return OnlineState.Offline;
            default:
                return OnlineState.Offline;
        }
    }
}
