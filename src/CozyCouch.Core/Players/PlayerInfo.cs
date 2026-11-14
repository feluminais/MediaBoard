namespace CozyCouch.Core.Players;

/// <param name="Id">mpc-hc, mpc-be, mpv, vlc or custom.</param>
/// <param name="TracksProgress">Whether the player reports its playback position back to the app.</param>
public sealed record PlayerInfo(string Id, string Name, string ExePath, bool TracksProgress);
