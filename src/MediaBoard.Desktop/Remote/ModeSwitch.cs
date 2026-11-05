using MediaBoard.Core.Activity;
using MediaBoard.Core.Settings;
using MediaBoard.Desktop.Sharing;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Remote;

/// <summary>
/// Moves this PC between its own library and a server's, which takes a restart since the two compositions can't share a
/// process. Neither direction deletes anything: the database, the outbox and the saved server all wait for the way back.
/// </summary>
/// <param name="settings">Where the mode is saved.</param>
/// <param name="restart">Starts the app in the new mode.</param>
/// <param name="hub">Says what's running that a switch would end.</param>
/// <param name="startWithWindows">The sign-in entry, which only a PC on its own library has.</param>
/// <param name="showConnect">Shows the Connect screen; finishes when it has closed.</param>
/// <param name="deviceName">What the owner sees this PC called.</param>
/// <param name="confirm">Asks before ending what's running; true to go ahead.</param>
public sealed class ModeSwitch(
    SettingsStore settings,
    AppRestart restart,
    ActivityHub hub,
    StartWithWindows? startWithWindows = null,
    Func<ConnectViewModel, Task>? showConnect = null,
    string? deviceName = null,
    Func<string, Task<bool>>? confirm = null)
{
    /// <returns>False when the settings file refused or the app couldn't restart, and this run carries on.</returns>
    public bool UseThisPc() => Save(AppMode.ThisPc) && restart.Run();

    /// <summary>
    /// With a session still held, goes straight back to it. Otherwise opens Connect, and the mode changes only when that
    /// succeeds, so closing it changes nothing.
    /// </summary>
    /// <returns>What to tell the viewer, or null.</returns>
    public async Task<string?> ConnectToServerAsync()
    {
        if (StoppingQuestion() is { } question && confirm is { } ask && !await ask(question)) return null;

        var saved = settings.Load().Connection;
        if (saved is { ProtectedToken: not null } && ModeGate.HasUsableEndpoint(saved))
        {
            if (!Save(AppMode.Server)) return "Couldn't save this change. See the activity panel.";

            LeaveThisPc();
            return restart.Run() ? null : "Restart MediaBoard to connect.";
        }

        if (showConnect is null) return null;

        using var connect = new ConnectViewModel(settings, deviceName ?? Environment.MachineName, restart.Run, saved, LeaveThisPc);
        await showConnect(connect);
        return null;
    }

    private string? StoppingQuestion() => (hub.Current.Sharing, hub.Current.PlayerName) switch
    {
        (not null, not null) => "Connecting to a server stops sharing and closes the player.",
        (not null, null) => "Connecting to a server stops sharing.",
        (null, not null) => "Connecting to a server closes the player.",
        _ => null,
    };

    // Windows would otherwise go on opening the window at every sign-in, for a viewer with no reason to want that.
    private void LeaveThisPc()
    {
        if (startWithWindows is { } entry && settings.Load().Sharing.StartWithWindows) entry.Set(false);
    }

    private bool Save(AppMode mode)
    {
        try
        {
            settings.Update(s => s with { Mode = mode });
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            hub.Log(ActivityLevel.Error, $"Couldn't save settings: {e.Message}", "Make sure you can write to the MediaBoard data folder.");
            return false;
        }
    }
}
