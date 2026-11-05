using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Remote;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>What a viewer is told when something stands between them and the library. One place, so the Connect screen and the panel can't drift apart.</summary>
public static class FailureText
{
    public const string SignedOut = "You've been signed out of this library.";
    public const string IdentityChanged = "This server's identity changed. Ask for a new invite.";
    public const string InviteDead = "This invite no longer works. Ask for a new one.";
    public const string Locked = "Too many tries. Wait 15 minutes.";
    public const string FileMissing = "File missing";
    public const string NothingShared = "Nothing is shared with you yet.";

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

/// <summary>
/// Stands in for a page that has nothing to show because of the connection, and keeps the status bar's line. The page
/// stays underneath, so covering it loses nothing. A state that waits for the viewer covers a page even with data, since
/// that data can't be refreshed and only here does it say what to do. Settings is never covered: it's how a viewer leaves
/// a server that won't come back.
/// </summary>
public sealed partial class ConnectionPanelViewModel : ObservableObject
{
    private readonly ConnectionMonitor _monitor;
    private readonly string _server;
    private readonly Func<bool> _signIn;
    private ObservableObject? _page;
    private bool _nothingShared;
    private bool _failed;

    /// <param name="monitor">Says what the link is doing, on any thread.</param>
    /// <param name="server">The server's name.</param>
    /// <param name="signIn">Goes back to the Connect screen. False when it couldn't.</param>
    public ConnectionPanelViewModel(ConnectionMonitor monitor, string server, Func<bool> signIn)
    {
        _monitor = monitor;
        _server = server;
        _signIn = signIn;
        monitor.Changed += () => Dispatcher.UIThread.Post(Sync);
        Sync();
    }

    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    [ObservableProperty]
    public partial string Message { get; private set; } = "";

    /// <summary>Retry, Sign in, or null when there's nothing to press.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAction))]
    public partial string? ActionLabel { get; private set; }

    /// <summary>Set when Sign in couldn't restart the app.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    /// <summary>What the status bar says.</summary>
    [ObservableProperty]
    public partial string StatusLine { get; private set; } = "";

    public bool HasAction => ActionLabel is not null;

    public bool HasNote => Note is not null;

    /// <summary>The page on screen.</summary>
    public void Follow(ObservableObject page)
    {
        if (_page is not null) _page.PropertyChanged -= OnPageChanged;
        _page = page;
        page.PropertyChanged += OnPageChanged;
        Sync();
    }

    /// <summary>The server answered, and the library it shows this viewer is empty.</summary>
    public void SetNothingShared(bool nothing)
    {
        _nothingShared = nothing;
        Sync();
    }

    private static bool WaitsForTheViewer(ConnectionState state) =>
        state is ConnectionState.SignedOut or ConnectionState.IdentityChanged or ConnectionState.VersionMismatch;

    [RelayCommand]
    private void Act()
    {
        if (_monitor.State != ConnectionState.SignedOut) _monitor.Retry();
        else Note = _signIn() ? null : "Restart MediaBoard to sign in.";
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IServerPage.IsLoaded)) Sync();
    }

    private void Sync()
    {
        var state = _monitor.State;
        if (state == ConnectionState.Connected) _failed = false;
        else if (state != ConnectionState.Connecting) _failed = true;

        StatusLine = state switch
        {
            ConnectionState.Connected => $"Connected to {_server}",
            ConnectionState.Unreachable => "Can't reach the server",
            ConnectionState.SignedOut => "Signed out",
            ConnectionState.IdentityChanged => "Server identity changed",
            ConnectionState.VersionMismatch => "Update MediaBoard",
            _ => "Connecting…",
        };

        // The button stays while a retry runs, so the focus still has a place when that try fails.
        ActionLabel = state switch
        {
            ConnectionState.Connected => null,
            ConnectionState.Connecting when !_failed => null,
            ConnectionState.SignedOut => "Sign in",
            _ => "Retry",
        };
        Message = state == ConnectionState.Connected ? FailureText.NothingShared : FailureText.For(state, _server, _monitor.ServerVersion);
        IsShown = Covers(state);
    }

    private bool Covers(ConnectionState state)
    {
        if (_page is not IServerPage page) return false;

        // An online title comes from TMDB, so it has something to show however empty the library is.
        var nothingHere = _nothingShared && page is not TitleViewModel;
        return state switch
        {
            ConnectionState.Connected => nothingHere,
            _ when WaitsForTheViewer(state) => true,
            _ => nothingHere || !page.IsLoaded,
        };
    }
}
