using CozyCouch.Core.Storage;

namespace CozyCouch.Core.Playback;

/// <param name="Locator">What the player opens: a path, or a loopback URL.</param>
public sealed record PlayTarget(int FileId, string Locator, string Title, double? DurationS, Progress? Saved, bool Missing);

/// <summary>Where <see cref="PlaybackService"/> gets the files it plays: this PC's library, or a shared one.</summary>
public interface IPlaybackSource
{
    /// <summary>Null when the file isn't known. May use the network, and throw what that throws; called before a session starts.</summary>
    Task<PlayTarget?> ResolveAsync(int fileId, CancellationToken ct);

    /// <summary>Called under the session gate: must not block or use the network.</summary>
    PlayTarget? FindByLocator(string locator);

    /// <summary>
    /// Whether the player reads over a link that can break without it saying so. A player that lost its stream reports
    /// Ended early and then claims the whole file. So <see cref="PlaybackService"/> doesn't count an Ended unless both the
    /// position and the duration are known, and after any Ended that doesn't count it stops believing the player's positions
    /// until it plays again. A file on this PC can't go that way.
    /// </summary>
    bool PlayerCanLoseItsStream => false;

    /// <summary>
    /// A locator from <see cref="ResolveAsync"/> is done with: its session ended, or it never got one. Any way of ending a
    /// session can race a stop, so a locator can arrive more than once, and a repeat must change nothing. Must not block. A
    /// local file holds nothing.
    /// </summary>
    void Released(string locator)
    {
    }
}

/// <summary>Where <see cref="PlaybackService"/> saves progress. Called under the session gate: must not block or use the network.</summary>
public interface IProgressSink
{
    void Save(int fileId, double positionS, double durationS);

    void SetWatched(int fileId, bool watched);

    /// <summary>The player paused on this file, with its session still open. A local sink has nothing to do.</summary>
    void Paused(int fileId)
    {
    }

    /// <summary>The session is done with this file: it ended, closed, failed or moved on. A local sink has nothing to do.</summary>
    void Finished(int fileId)
    {
    }
}
