using MediaBoard.Core.Remote;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>What a viewer is told when something stands between them and the library. One place, so the Connect screen and the panel can't drift apart.</summary>
public static class FailureText
{
    public const string SignedOut = "You've been signed out of this library.";
    public const string IdentityChanged = "This server's identity changed. Ask for a new invite.";
    public const string InviteDead = "This invite no longer works. Ask for a new one.";
    public const string Locked = "Too many tries. Wait 15 minutes.";
    public const string Busy = "The server is busy. Try again in a moment.";
    public const string FileMissing = "File missing";
    public const string NothingShared = "Nothing is shared with you yet.";
    public const string LoadFailed = "Couldn't load this page.";

    public static string Unreachable(string server) => $"Can't reach {server}. Their PC may be off or asleep, or sharing is off.";

    /// <param name="state">The state of the link.</param>
    /// <param name="server">The server's name.</param>
    /// <param name="serverVersion">The API version it last gave, which says which side is behind on a mismatch.</param>
    public static string For(ConnectionState state, string server, int? serverVersion) => state switch
    {
        ConnectionState.Unreachable => Unreachable(server),
        ConnectionState.SignedOut => SignedOut,
        ConnectionState.IdentityChanged => IdentityChanged,
        ConnectionState.VersionMismatch when serverVersion < ApiVersion.Current => "Update MediaBoard on the owner's PC.",
        ConnectionState.VersionMismatch => "Update MediaBoard on this PC.",
        _ => $"Connecting to {server}…",
    };
}
