using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;

namespace MediaBoard.Desktop;

/// <summary>What the app does first.</summary>
public enum StartupChoice
{
    RunLocal,
    RunRemote,
    AskFirstRun,
    ShowSettingsError,

    /// <summary>This PC is a viewer but has nothing to connect with: it was signed out, or its saved server can't be used.</summary>
    ShowConnect,
}

/// <summary>Picks the mode before any composition exists, from nothing but the settings file and whether a library does.</summary>
public static class ModeGate
{
    private const int FingerprintLength = 64;

    /// <summary>An existing library with no mode saved is someone who was here before modes, so they never see first run.</summary>
    public static StartupChoice Decide(SettingsReadResult read, bool databaseExists) => read switch
    {
        SettingsReadResult.Unreadable => StartupChoice.ShowSettingsError,
        SettingsReadResult.Ok { Settings: { Mode: AppMode.Server, Connection: { } connection } } => CanConnect(connection) ? StartupChoice.RunRemote : StartupChoice.ShowConnect,
        SettingsReadResult.Ok { Settings.Mode: AppMode.Server } => StartupChoice.AskFirstRun,
        SettingsReadResult.Ok { Settings.Mode: AppMode.ThisPc } => StartupChoice.RunLocal,
        _ => databaseExists ? StartupChoice.RunLocal : StartupChoice.AskFirstRun,
    };

    /// <summary>
    /// False for what a hand-edited settings file can leave behind, which <see cref="ApiClient"/>'s constructor would throw
    /// on. There's nothing to repair it from, so it means a new invite.
    /// </summary>
    public static bool HasUsableEndpoint(StoredConnection connection) =>
        connection.Endpoint is { Fingerprint: { Length: FingerprintLength } fingerprint, Addresses.Count: > 0 } && fingerprint.All(char.IsAsciiHexDigit);

    private static bool CanConnect(StoredConnection connection) => connection.ProtectedToken is not null && HasUsableEndpoint(connection);
}
