namespace CozyCouch.Core.Players;

/// <summary>One file played in an external player, from launch until the player closes.</summary>
public interface IPlayerSession : IAsyncDisposable
{
    /// <summary>Whether this session reports positions back.</summary>
    bool TracksProgress { get; }

    /// <summary>
    /// Raised on a background thread (for MPC-HC, its message-window thread), one event at a time and in order.
    /// Handlers must return quickly and must not block on the session. Marshal to the UI thread with
    /// <c>InvokeAsync</c>/<c>Post</c>, never a synchronous <c>Invoke</c>: the UI thread may be busy disposing the
    /// session. An event already in flight may still arrive while, or just after, the session is disposed.
    /// </summary>
    event Action<PlaybackEvent>? Event;

    /// <summary>Launches the player. Launch problems are reported as <see cref="PlaybackEvent.Failed"/>, not thrown.</summary>
    Task StartAsync(string filePath, TimeSpan? startAt, CancellationToken ct);

    /// <summary>The current position, or null when the player cannot tell right now.</summary>
    Task<TimeSpan?> GetPositionAsync(CancellationToken ct);

    /// <summary>
    /// Leaves the player running: disposing the session afterwards stops listening to it and releases the process
    /// handle without closing the player. Without this, disposal closes a player the session started.
    /// </summary>
    void Detach();
}
