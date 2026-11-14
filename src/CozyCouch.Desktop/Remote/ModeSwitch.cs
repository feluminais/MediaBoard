using CozyCouch.Core.Activity;
using CozyCouch.Core.Settings;
using CozyCouch.Desktop.Sharing;
using CozyCouch.Desktop.ViewModels;

namespace CozyCouch.Desktop.Remote;

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
    private const string NotSaved = "Couldn't save this change. See the activity panel.";
    private const string RestartToConnect = "Restart CozyCouch to connect.";

    /// <returns>What to tell the viewer, or null.</returns>
    public string? UseThisPc()
    {
        if (!Save(AppMode.ThisPc)) return NotSaved;

        return restart.Run() ? null : "Restart CozyCouch to open this PC's library.";
    }

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
            if (!Save(AppMode.Server)) return NotSaved;

            LeaveThisPc();
            return restart.Run() ? null : RestartToConnect;
        }

        if (showConnect is null) return null;

        using var connect = new ConnectViewModel(settings, deviceName ?? Environment.MachineName, restart.Run, saved, LeaveThisPc);
        await showConnect(connect);

        // Closed as the answer came in: Connect saved the mode, and the restart was left to a window that has gone.
        return settings.Load().Mode == AppMode.Server ? RestartToConnect : null;
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
            hub.Log(ActivityLevel.Error, $"Couldn't save settings: {e.Message}", "Make sure you can write to the CozyCouch data folder.");
            return false;
        }
    }
}
