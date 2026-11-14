using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using CozyCouch.Core.Activity;
using CozyCouch.Core.Playback;

namespace CozyCouch.Desktop.Sharing;

/// <summary>Something that keeps the app running with its window closed.</summary>
public interface ITrayReason
{
    /// <summary>Raised on any thread when any of the rest changes.</summary>
    event Action? Changed;

    bool Applies { get; }

    /// <summary>The line this reason adds to the tray tooltip.</summary>
    string Status { get; }

    /// <summary>What to ask before quitting, or null when quitting costs nobody anything.</summary>
    string? QuitQuestion { get; }

    /// <summary>
    /// When this was the last reason and the window is hidden, the app has finished and exits. False brings the window
    /// back. Read as the reason ends.
    /// </summary>
    bool ExitsWhenOver { get; }
}

/// <summary>While sharing is on, closing the window must not stop the server.</summary>
public sealed class SharingTrayReason(SharingController sharing) : ITrayReason
{
    public event Action? Changed
    {
        add => sharing.Changed += value;
        remove => sharing.Changed -= value;
    }

    public bool Applies => sharing.Status == SharingStatus.On;

    public string Status => sharing.Summary ?? "";

    public string? QuitQuestion => sharing.Watching switch
    {
        0 => null,
        1 => "Someone is watching. Quit and stop their stream?",
        var watching => string.Create(CultureInfo.InvariantCulture, $"{watching} streams are playing. Quit and stop them?"),
    };

    public bool ExitsWhenOver => false;
}

/// <summary>
/// A viewer's stream runs through the app, so closing the window while a file plays must not stop it. Once the player
/// closes the app has nothing left to do, unless the viewer still needs telling something, and the window is how.
/// </summary>
public sealed class PlaybackTrayReason : ITrayReason
{
    private readonly ActivityHub _hub;
    private readonly PlaybackService _playback;

    public PlaybackTrayReason(ActivityHub hub, PlaybackService playback)
    {
        _hub = hub;
        _playback = playback;
        hub.StatusChanged += _ => Changed?.Invoke();
    }

    public event Action? Changed;

    public bool Applies => _hub.Current.PlayerName is not null;

    public string Status => $"Playing in {_hub.Current.PlayerName}";

    public string? QuitQuestion => null;

    public bool ExitsWhenOver => !_playback.EndedWithNews;
}

/// <summary>
/// The tray icon and the window's way there. The icon shows while a reason applies, and closing the window then hides
/// it. When the last reason ends with the window hidden, the window comes back, since the app would otherwise run
/// with nothing on screen to reach it by, or the app exits if that reason says so.
/// </summary>
public sealed class TrayController
{
    private static readonly Uri IconAsset = new("avares://CozyCouch.Desktop/Assets/app.ico");

    private readonly Window _window;
    private readonly IReadOnlyList<ITrayReason> _reasons;
    private readonly Func<string, Task<bool>> _confirmQuit;
    private IReadOnlyList<ITrayReason> _heldBy = [];
    private bool _settling;
    private bool _quitting;
    private bool _asking;
    private bool _closed;

    /// <param name="window">The main window.</param>
    /// <param name="reasons">What can keep the app in the tray.</param>
    /// <param name="confirmQuit">Asks a reason's question; true to go ahead.</param>
    public TrayController(Window window, IEnumerable<ITrayReason> reasons, Func<string, Task<bool>> confirmQuit)
    {
        _window = window;
        _reasons = [.. reasons];
        _confirmQuit = confirmQuit;

        var open = new NativeMenuItem("Open");
        open.Click += (_, _) => ShowWindow();
        var quit = new NativeMenuItem("Quit");
        quit.Click += OnQuitClicked;
        Icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(IconAsset)),
            IsVisible = false,
            Menu = new NativeMenu { open, quit },
        };
        Icon.Clicked += (_, _) => ShowWindow();

        _window.Closed += (_, _) => _closed = true;
        foreach (var reason in _reasons) reason.Changed += () => Dispatcher.UIThread.Post(Sync);
    }

    /// <summary>The icon; the application lists it in <see cref="TrayIcon.IconsProperty"/>.</summary>
    public TrayIcon Icon { get; }

    /// <summary>
    /// For a start that keeps the window closed. <paramref name="settled"/> finishes once any reason due at startup has
    /// had its chance, and only then does the absence of one bring the window up.
    /// </summary>
    public async Task StartHiddenAsync(Task settled)
    {
        _settling = true;
        await settled;
        _settling = false;
        Sync();
    }

    /// <summary>Call from the window's Closing, before anything else about closing.</summary>
    /// <returns>True when the close became a hide.</returns>
    public bool HideInstead(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_quitting || e.CloseReason != WindowCloseReason.WindowClosing || !_reasons.Any(reason => reason.Applies)) return false;

        e.Cancel = true;
        _window.Hide();
        return true;
    }

    /// <summary>Does nothing once the window has closed, since a second start can ask during exit.</summary>
    public void ShowWindow()
    {
        if (_closed) return;

        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Asks whatever the reasons need asked, then closes the window for real. Saying no changes nothing.</summary>
    public async Task QuitAsync()
    {
        // The tray menu still works behind an open dialog. A second question would stack on the first, and a second
        // close would pull the window out from under the player prompt.
        if (_asking || _quitting) return;

        _asking = true;
        try
        {
            foreach (var question in _reasons.Where(reason => reason.Applies).Select(reason => reason.QuitQuestion).OfType<string>())
            {
                ShowWindow(); // a dialog can't open over a hidden window
                if (!await _confirmQuit(question)) return;
            }
        }
        finally
        {
            _asking = false;
        }

        _quitting = true;
        _window.Close();
    }

    private async void OnQuitClicked(object? sender, EventArgs e)
    {
        try
        {
            await QuitAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Quit from the tray failed: {ex}");
        }
    }

    private void Sync()
    {
        // Sharing stops as the app exits, and a closed window can't be shown again.
        if (_closed) return;

        var holding = _reasons.Where(reason => reason.Applies).ToList();
        Icon.IsVisible = holding.Count > 0;
        Icon.ToolTipText = string.Join('\n', holding.Select(reason => reason.Status));
        if (holding.Count > 0)
        {
            _heldBy = holding;
            return;
        }

        if (_settling || _window.IsVisible) return;

        if (_heldBy.Count > 0 && _heldBy.All(reason => reason.ExitsWhenOver))
        {
            _quitting = true;
            _window.Close();
        }
        else
        {
            ShowWindow();
        }
    }
}
