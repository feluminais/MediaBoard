using CozyCouch.Core.Playback;

namespace CozyCouch.Desktop;

/// <summary>
/// Runs when the app closes while a file plays: saves the position, then asks whether to close the player too
/// (R45). Leaving it playing detaches the session, so nothing after that is saved. The question is passed in, so
/// tests answer it without a window.
/// </summary>
public sealed class PlayerExitPrompt(IPlaybackService playback, Func<string, Task<bool>> askClosePlayer)
{
    /// <summary>Does nothing when no file plays. Completes once the session is stopped or detached.</summary>
    public async Task RunAsync()
    {
        if (playback.CurrentPlayer is not { } player) return;

        await playback.SavePositionAsync(CancellationToken.None);
        var close = await askClosePlayer(player);
        await playback.StopAsync(close, CancellationToken.None);
    }
}
