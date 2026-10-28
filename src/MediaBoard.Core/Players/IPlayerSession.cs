namespace MediaBoard.Core.Players;

/// <summary>One file played in an external player, from launch until the player closes.</summary>
public interface IPlayerSession : IAsyncDisposable
{
    /// <summary>Whether this session reports positions back.</summary>
    bool TracksProgress { get; }

    /// <summary>
    /// Raised on a background thread (for MPC-HC, its message-window thread). Handlers must return quickly,
    /// must not block on the session, and must marshal to the UI thread themselves.
    /// </summary>
    event Action<PlaybackEvent>? Event;

    /// <summary>Launches the player. Launch problems are reported as <see cref="PlaybackEvent.Failed"/>, not thrown.</summary>
    Task StartAsync(string filePath, TimeSpan? startAt, CancellationToken ct);

    /// <summary>The current position, or null when the player cannot tell right now.</summary>
    Task<TimeSpan?> GetPositionAsync(CancellationToken ct);
}
