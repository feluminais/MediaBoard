namespace CozyCouch.Core.Players;

/// <summary>
/// A session that can follow a player the viewer started without CozyCouch, because the player was set up to listen
/// at a fixed address (mpv's pipe, VLC's web interface). It reports through <see cref="IPlayerSession.Event"/> like a
/// launched session, starting with <see cref="PlaybackEvent.FileChanged"/> for the file the player has open, and
/// raises <see cref="PlaybackEvent.Closed"/> when the player goes away. Disposing it never closes the player.
/// </summary>
public interface IAttachableSession : IPlayerSession
{
    /// <summary>The player's name for messages and the status bar.</summary>
    string PlayerName { get; }

    /// <summary>
    /// One attempt to connect to a running player. False when none answers; the session is then unused and is
    /// disposed. Valid once per session, in place of <see cref="IPlayerSession.StartAsync"/>.
    /// </summary>
    Task<bool> AttachAsync(CancellationToken ct);
}
