using System.Globalization;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;

namespace MediaBoard.Core.Remote;

/// <summary>
/// The library of the server a viewer is signed in to, behind the interface the pages already use. Every read waits for
/// the server on the caller's thread, which is why the pages run theirs on the thread pool. Failures are thrown
/// (<see cref="ServerUnreachableException"/>, <see cref="ApiException"/>) for the page's query to show. Whatever edits the
/// library on the owner's PC is not offered to a viewer and throws <see cref="NotSupportedException"/>.
/// </summary>
public sealed class RemoteLibrary : ILibrary, IDisposable
{
    private const string LibraryChangedEvent = "library-changed";

    private readonly ApiClient _client;
    private readonly ConnectionMonitor _monitor;

    /// <param name="client">The client to read through.</param>
    /// <param name="monitor">Holds the one events connection, so this listens to it and opens none.</param>
    public RemoteLibrary(ApiClient client, ConnectionMonitor monitor)
    {
        _client = client;
        _monitor = monitor;
        monitor.EventReceived += OnEvent;
        monitor.Changed += OnConnectionChanged;
    }

    /// <summary>Raised on the monitor's thread, or on the thread of the caller that made the edit.</summary>
    public event Action? Changed;

    public IReadOnlyList<ContinueItem> ContinueWatching(int limit) => _client.Get<List<ContinueItem>>(Limited("continue", limit));

    public void DismissContinue(int titleId)
    {
        try
        {
            _client.Send(HttpMethod.Post, Route("continue/{0}/dismiss", titleId));
        }
        catch (ApiException e) when (e.Code == ApiErrorCode.NotFound)
        {
            // A title that isn't on the list changes nothing, as it doesn't locally.
            return;
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<TitleCard> RecentlyAdded(int limit) => _client.Get<List<TitleCard>>(Limited("recent", limit));

    // includeHidden is not sent: hidden titles never leave the owner's PC.
    public IReadOnlyList<TitleCard> Titles(TitleKind kind, TitleSort sort, bool includeHidden) =>
        _client.Get<List<TitleCard>>($"titles?kind={kind}&sort={sort}");

    public TitleDetailsView? Title(int titleId)
    {
        try
        {
            return _client.Get<TitleDetailsView>(Route("titles/{0}", titleId));
        }
        catch (ApiException e) when (e.Code == ApiErrorCode.NotFound)
        {
            return null;
        }
    }

    public IReadOnlyList<ReviewItem> NeedsReview() => [];

    public int NeedsReviewCount() => 0;

    // Without a row the pages would say the library has no folders. It tells nothing about the owner's disks.
    public IReadOnlyList<FolderRow> Folders() =>
        [new FolderRow(0, string.Empty, Enabled: true, Hidden: false, Unavailable: false, FileCount: 0, IgnoredFolders: [])];

    public IReadOnlyList<TitleCard> SearchLocal(string query, int limit) =>
        _client.Get<List<TitleCard>>(RemoteSearchService.LocalRoute(query, limit));

    public int? NextEpisodeFileId(int titleId)
    {
        try
        {
            return _client.Get<NextReply>(Route("titles/{0}/next", titleId)).FileId;
        }
        catch (ApiException e) when (e.Code == ApiErrorCode.NotFound)
        {
            return null;
        }
    }

    public void Rename(TargetKind kind, int id, string? displayName) => throw new NotSupportedException();

    public void SetHidden(TargetKind kind, int id, bool hidden) => throw new NotSupportedException();

    public void SetWatched(int fileId, bool watched)
    {
        // No Changed: a show sends one of these per file, and the page that edits refreshes itself afterwards.
        _client.Send(HttpMethod.Put, Route("files/{0}/watched", fileId), new WatchedRequest(watched));
    }

    public Task FixMatchAsync(int fileId, int tmdbId, TitleKind kind, CancellationToken ct) => throw new NotSupportedException();

    public void MarkNotMedia(int fileId) => throw new NotSupportedException();

    public DeletePreview PreviewDelete(int titleId) => throw new NotSupportedException();

    public Task<DeleteResult> DeleteFromDiskAsync(DeletePreview preview) => throw new NotSupportedException();

    public void Dispose()
    {
        _monitor.EventReceived -= OnEvent;
        _monitor.Changed -= OnConnectionChanged;
    }

    private static string Route(string format, int id) => string.Format(CultureInfo.InvariantCulture, format, id);

    private static string Limited(string route, int limit) => string.Create(CultureInfo.InvariantCulture, $"{route}?limit={limit}");

    private void OnEvent(string name)
    {
        if (name == LibraryChangedEvent) Changed?.Invoke();
    }

    // Whatever changed while the link was down went unheard, so coming back is a change too.
    private void OnConnectionChanged()
    {
        if (_monitor.State == ConnectionState.Connected) Changed?.Invoke();
    }
}
