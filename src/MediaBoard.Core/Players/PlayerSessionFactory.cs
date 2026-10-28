using MediaBoard.Core.Players.MpcHc;
using MediaBoard.Core.Players.Mpv;
using MediaBoard.Core.Players.Vlc;

namespace MediaBoard.Core.Players;

/// <summary>Picks the session that talks to a player.</summary>
public static class PlayerSessionFactory
{
    /// <summary>
    /// A new, unstarted session for <paramref name="player"/>: MPC-HC and MPC-BE (the same slave protocol; MPC-BE's
    /// support is unconfirmed, and the session fails plainly if it never connects), mpv, VLC, or launch-only for a
    /// custom program.
    /// </summary>
    /// <exception cref="ArgumentException">The player id is not one of mpc-hc, mpc-be, mpv, vlc, custom.</exception>
    public static IPlayerSession Create(PlayerInfo player) => player.Id switch
    {
        "mpc-hc" or "mpc-be" => MpcHcSession.Create(player),
        "mpv" => MpvSession.Create(player),
        "vlc" => VlcSession.Create(player),
        PlayerDetector.CustomId => LaunchOnlySession.Create(player),
        _ => throw new ArgumentException($"Unknown player id: {player.Id}", nameof(player)),
    };
}
