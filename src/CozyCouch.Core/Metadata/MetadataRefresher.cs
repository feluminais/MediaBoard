using CozyCouch.Core.Activity;
using CozyCouch.Core.Storage;

namespace CozyCouch.Core.Metadata;

/// <summary>Re-fetches stored titles from TMDB so ratings, posters and episode lists do not go stale.</summary>
public sealed class MetadataRefresher(
    IMetadataProvider provider,
    TitleStore titles,
    Connectivity connectivity,
    ActivityHub hub,
    TimeProvider time)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Re-fetches one title and the seasons already stored for it, then updates the rows and <c>fetched_at</c>.
    /// Nothing is written unless every fetch worked. Provisional and unknown titles are skipped.
    /// </summary>
    /// <exception cref="MetadataUnavailableException">TMDB could not answer.</exception>
    public async Task RefreshAsync(int titleId, CancellationToken ct = default)
    {
        var row = titles.Get(titleId);
        if (row is not { TmdbId: { } tmdbId }) return;

        var details = await provider.GetTitleAsync(tmdbId, row.Kind, ct).ConfigureAwait(false);
        var seasons = new List<SeasonDetails>();
        if (row.Kind == TitleKind.Show)
        {
            foreach (var season in titles.StoredSeasons(titleId))
                seasons.Add(await provider.GetSeasonAsync(tmdbId, season, ct).ConfigureAwait(false));
        }

        // Upsert keeps the row id, and episodes are updated in place, so files linked to them stay linked.
        titles.Upsert(details);
        foreach (var season in seasons) titles.UpsertEpisodes(titleId, season);
    }

    /// <summary>
    /// Refreshes every title fetched more than <see cref="MaxAge"/> ago. Does nothing while offline, and stops
    /// quietly at the first answer that says every other title would fail too (offline, no key, key rejected).
    /// A title TMDB fails on by itself is skipped.
    /// </summary>
    public async Task RefreshStaleAsync(CancellationToken ct)
    {
        if (!connectivity.IsOnline) return;

        var refreshed = 0;
        foreach (var id in titles.FetchedBefore(time.GetUtcNow() - MaxAge))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RefreshAsync(id, ct).ConfigureAwait(false);
                refreshed++;
            }
            catch (MetadataUnavailableException e) when (e.Reason == UnavailableReason.ServerError)
            {
                // Only this title failed, for instance because TMDB removed it.
            }
            catch (MetadataUnavailableException e)
            {
                if (e.Reason == UnavailableReason.Offline) connectivity.ReportOffline();
                break;
            }
        }

        if (refreshed > 0) hub.Log(ActivityLevel.Info, $"Refreshed {refreshed} {(refreshed == 1 ? "title" : "titles")} from TMDB.");
    }
}
