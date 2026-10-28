using MediaBoard.Core.Metadata;
using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Library;

public sealed class SearchService(
    ILibrary library,
    IMetadataProvider provider,
    Connectivity connectivity,
    TitleStore titles) : ISearchService
{
    public const int MaxLibraryResults = 10;
    public const int MaxOnlineResults = 10;
    public const int MinOnlineQueryLength = 2;

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
                .Select(c => new OnlineResult(c, titles.ByTmdb(c.TmdbId, c.Kind)?.Id))
                .ToList();
            return new SearchResults(local, online, OnlineState.Ok);
        }
        catch (MetadataUnavailableException e)
        {
            return new SearchResults(local, [], StateOf(e.Reason));
        }
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

    public IReadOnlyList<PaletteCommand> Commands(string filter)
    {
        var prefix = filter.Trim();
        if (prefix.Length == 0) return AllCommands;

        return AllCommands
            .Where(c => c.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || c.Label.Split(' ').Any(w => w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

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
