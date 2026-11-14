using CozyCouch.Core.Remote;
using CozyCouch.Desktop.ViewModels;

namespace CozyCouch.Desktop;

/// <summary>What this run of the app can do. The shell and its pages ask this instead of asking which mode they're in.</summary>
/// <param name="CanEditLibrary">Rename, hide, fix a match, delete.</param>
/// <param name="ManagesFolders">This PC holds the folders, so it has Folders and Needs review.</param>
/// <param name="Shares">This PC can share its library.</param>
/// <param name="Connection">The link to the server, for a viewer.</param>
public sealed record AppCapabilities(bool CanEditLibrary, bool ManagesFolders, bool Shares, ConnectionMonitor? Connection)
{
    /// <summary>The pages reload when the connection comes back, so restarting a viewer's app helps nothing.</summary>
    public const string RemoteFailureHint = "This loads again when the server answers.";

    public static AppCapabilities Local { get; } = new(CanEditLibrary: true, ManagesFolders: true, Shares: true, Connection: null);

    public string FailureHint => Connection is null ? BackgroundQuery.FailureHint : RemoteFailureHint;

    public static AppCapabilities Remote(ConnectionMonitor connection) =>
        new(CanEditLibrary: false, ManagesFolders: false, Shares: false, connection);
}
