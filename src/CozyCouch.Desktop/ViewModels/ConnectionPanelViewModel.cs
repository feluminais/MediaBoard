using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CozyCouch.Core.Remote;

namespace CozyCouch.Desktop.ViewModels;

/// <summary>
/// Stands in for a page that has nothing to show because of the connection, and keeps the status bar's line. The page
/// stays underneath, so covering it loses nothing. A state that waits for the viewer covers a page even with data, since
/// that data can't be refreshed and only here does it say what to do, and it goes on covering through a retry until the
/// next state arrives. A first load that failed with the link up is covered too, and Retry reloads that page. Settings is
/// never covered: it's how a viewer leaves a server that won't come back.
/// </summary>
public sealed partial class ConnectionPanelViewModel : ObservableObject
{
    private readonly ConnectionMonitor _monitor;
    private readonly string _server;
    private readonly Func<bool> _signIn;
    private ObservableObject? _page;
    private bool _nothingShared;
    private bool _failed;
    private bool _waited;

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

    [RelayCommand]
    private void Act()
    {
        switch (_monitor.State)
        {
            case ConnectionState.SignedOut:
                Note = _signIn() ? null : "Restart CozyCouch to sign in.";
                break;
            case ConnectionState.Connected:
                if (_page is PageViewModel page) _ = page.RefreshAsync();
                break;
            default:
                _monitor.Retry();
                break;
        }
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IServerPage.IsLoaded) or nameof(IServerPage.LoadFailed)) Sync();
    }

    private void Sync()
    {
        var state = _monitor.State;
        if (state == ConnectionState.Connected) _failed = false;
        else if (state != ConnectionState.Connecting) _failed = true;

        if (ConnectionMonitor.IsFinal(state)) _waited = true;
        else if (state != ConnectionState.Connecting) _waited = false;

        var loadFailed = _page is IServerPage { IsLoaded: false, LoadFailed: true };
        StatusLine = state switch
        {
            ConnectionState.Connected => $"Connected to {_server}",
            ConnectionState.Unreachable => "Can't reach the server",
            ConnectionState.SignedOut => "Signed out",
            ConnectionState.IdentityChanged => "Server identity changed",
            ConnectionState.VersionMismatch => "Update CozyCouch",
            _ => "Connecting…",
        };

        // The button stays while a retry runs, so the focus still has a place when that try fails.
        ActionLabel = state switch
        {
            ConnectionState.Connected => loadFailed ? "Retry" : null,
            ConnectionState.Connecting when !_failed => null,
            ConnectionState.SignedOut => "Sign in",
            _ => "Retry",
        };
        Message = state != ConnectionState.Connected ? FailureText.For(state, _server, _monitor.ServerVersion)
            : loadFailed ? FailureText.LoadFailed
            : FailureText.NothingShared;
        IsShown = Covers(state, loadFailed);
    }

    private bool Covers(ConnectionState state, bool loadFailed)
    {
        if (_page is not IServerPage page) return false;

        // An online title comes from TMDB, so it has something to show however empty the library is.
        var nothingHere = _nothingShared && page is not TitleViewModel;
        return state switch
        {
            ConnectionState.Connected => nothingHere || loadFailed,
            _ when _waited => true,
            _ => nothingHere || !page.IsLoaded,
        };
    }
}
