using System.Collections.Concurrent;
using System.Globalization;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Remote;

/// <summary>This PC can't serve a stream: the app is closing, or the loopback address couldn't be bound.</summary>
public sealed class StreamUnavailableException(Exception? innerException = null) : Exception("A stream can't be served from this PC.", innerException);

/// <summary>Turns a shared file into a URL a player can open, and says when what's behind that URL breaks.</summary>
public interface IStreamUrls
{
    /// <summary>
    /// Called before a session starts, and may start what serves the URL. Each call gives a new URL, which
    /// <see cref="Release"/> gives back.
    /// </summary>
    /// <exception cref="StreamUnavailableException">It can't be served.</exception>
    string UrlFor(int fileId, string fileName);

    /// <summary>The file behind a URL from <see cref="UrlFor"/>, or null. Answers from memory: it runs under the session gate.</summary>
    int? FileOf(string locator);

    /// <summary>Nothing will use this URL again: its session ended or never began. Must not block.</summary>
    void Release(string url);

    /// <summary>
    /// A file's stream can't go on, which a player won't show: it plays out its buffer and reports the file as finished.
    /// Gives the file, the URL, which says which session it was, and the code. Raised on a request's thread, and again by
    /// every request that meets the same failure.
    /// </summary>
    event Action<int, string, ApiErrorCode>? StreamFailed;
}

/// <summary>
/// The server's files, as <see cref="PlaybackService"/> sees them. The network is used only in
/// <see cref="ResolveAsync"/>, before a session starts. What it finds there is kept for <see cref="FindByLocator"/>.
/// </summary>
public sealed class RemotePlaybackSource : IPlaybackSource
{
    private readonly ApiClient _api;
    private readonly IStreamUrls _urls;
    private readonly ProgressOutbox _outbox;
    private readonly ConcurrentDictionary<int, PlayTarget> _targets = new();
    private volatile string? _ownerName;

    /// <param name="api">Where files are looked up.</param>
    /// <param name="urls">Makes the URLs players open.</param>
    /// <param name="outbox">Holds progress the server may not have yet.</param>
    public RemotePlaybackSource(ApiClient api, IStreamUrls urls, ProgressOutbox outbox)
    {
        _api = api;
        _urls = urls;
        _outbox = outbox;
        urls.StreamFailed += OnStreamFailed;
    }

    /// <summary>
    /// A stream failed: the file, its URL and what to tell the viewer. This goes to <see cref="PlaybackService.AbandonAsync"/>.
    /// Raised on a request's thread.
    /// </summary>
    public event Action<int, string, string>? StreamLost;

    /// <exception cref="ServerUnreachableException">No answer.</exception>
    /// <exception cref="IdentityChangedException">The server's certificate changed.</exception>
    /// <exception cref="ApiException">The server refused.</exception>
    /// <exception cref="StreamUnavailableException">This PC can't serve the stream.</exception>
    public async Task<PlayTarget?> ResolveAsync(int fileId, CancellationToken ct)
    {
        // Read before asking: a send that finishes during the request could leave neither answer with the entry in it.
        var waiting = _outbox.Waiting(fileId);

        FileReply file;
        try
        {
            file = await _api.GetAsync<FileReply>(string.Create(CultureInfo.InvariantCulture, $"files/{fileId}"), ct).ConfigureAwait(false);
        }
        catch (ApiException e) when (e.Code == ApiErrorCode.NotFound)
        {
            return null;
        }

        var saved = Overlay(file.Saved, waiting);

        // A missing file can't be opened; the name is all the log needs.
        if (file.Missing) return new PlayTarget(fileId, file.FileName, file.Title, file.DurationS, saved, Missing: true);

        // Asked now, not when a stream fails: by then the server may be why.
        _ownerName ??= (await _api.GetAsync<MeReply>("me", ct).ConfigureAwait(false)).OwnerName;

        var target = new PlayTarget(fileId, _urls.UrlFor(fileId, file.FileName), file.Title, file.DurationS, saved, Missing: false);
        _targets[fileId] = target;
        return target;
    }

    public bool PlayerCanLoseItsStream => true;

    public PlayTarget? FindByLocator(string locator) =>
        _urls.FileOf(locator) is { } fileId ? _targets.GetValueOrDefault(fileId) : null;

    public void Released(string locator) => _urls.Release(locator);

    /// <summary>The server settles two devices by the newest entry, so an entry older than its answer has lost already.</summary>
    private static Progress? Overlay(Progress? saved, PendingProgress? waiting)
    {
        if (waiting is null) return saved;

        var at = waiting.At.UtcDateTime;
        if (saved is not null && saved.LastPlayed > at) return saved;

        var position = waiting.PositionS ?? (waiting.Watched == false ? 0 : saved?.PositionS ?? 0);
        var duration = waiting.DurationS > 0 ? waiting.DurationS : saved?.DurationS ?? 0;
        return new Progress(waiting.FileId, position, duration, waiting.Watched ?? saved?.Watched ?? false, at);
    }

    private void OnStreamFailed(int fileId, string url, ApiErrorCode code) => StreamLost?.Invoke(fileId, url, ReasonFor(fileId, code));

    private string ReasonFor(int fileId, ApiErrorCode code) => code switch
    {
        ApiErrorCode.StreamStopped => $"{_ownerName} stopped this stream",
        ApiErrorCode.FileMissing => _targets.TryGetValue(fileId, out var target) ? $"File missing: {target.Title}" : "File missing",
        ApiErrorCode.DriveUnavailable => "The drive with this file isn't available",
        ApiErrorCode.Unreachable => "Can't reach the server",
        ApiErrorCode.TooManyStreams => "Too many streams on this account",
        _ => "The stream ended",
    };
}
