using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Playback;

/// <param name="Locator">What the player opens: a path, or a loopback URL.</param>
public sealed record PlayTarget(int FileId, string Locator, string Title, double? DurationS, Progress? Saved, bool Missing);

/// <summary>Where <see cref="PlaybackService"/> gets the files it plays: this PC's library, or a shared one.</summary>
public interface IPlaybackSource
{
    /// <summary>Null when the file isn't known. May use the network; called before a session starts.</summary>
    Task<PlayTarget?> ResolveAsync(int fileId, CancellationToken ct);

    /// <summary>Called under the session gate: must not block or use the network.</summary>
    PlayTarget? FindByLocator(string locator);
}

/// <summary>Where <see cref="PlaybackService"/> saves progress. Called under the session gate: must not block or use the network.</summary>
public interface IProgressSink
{
    void Save(int fileId, double positionS, double durationS);

    void SetWatched(int fileId, bool watched);
}
