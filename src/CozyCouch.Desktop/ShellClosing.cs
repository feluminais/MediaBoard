using Avalonia.Controls;
using CozyCouch.Core.Playback;
using CozyCouch.Desktop.Sharing;

namespace CozyCouch.Desktop;

/// <summary>
/// What closing the main window does. It goes to the tray while something needs the app running. Otherwise, while a file
/// plays, it asks about the player first (R45): the close is cancelled, the question runs, then the window closes for
/// real. Windows shutting down and the app ending itself skip all of this, and the host closes the player. A viewer has
/// no question to ask, since a player left running would lose its stream, so no prompt is passed.
/// </summary>
/// <param name="tray">Decides whether closing is hiding.</param>
/// <param name="prompt">Asks about the player, or null when there's no such choice.</param>
/// <param name="playback">Says whether a file plays.</param>
public sealed class ShellClosing(TrayController tray, PlayerExitPrompt? prompt, IPlaybackService playback)
{
    private bool _promptDone;

    public async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_promptDone || sender is not Window window) return;
        if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown) return;
        if (tray.HideInstead(e)) return;
        if (prompt is null || playback.CurrentPlayer is null) return;

        e.Cancel = true;
        _promptDone = true;
        try
        {
            await prompt.RunAsync();
        }
        catch (Exception ex)
        {
            // Closing goes ahead; AppHost.StopAsync still saves and closes whatever is left.
            System.Diagnostics.Trace.TraceWarning($"Exit prompt failed: {ex}");
        }

        window.Close();
    }
}
