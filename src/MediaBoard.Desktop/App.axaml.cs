using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;
using MediaBoard.Desktop.Remote;
using MediaBoard.Desktop.Sharing;
using MediaBoard.Desktop.Sizing;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop;

public partial class App : Application
{
    // Closing waits at most this long for services to stop; the window is already gone by then.
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(10);

    private ServiceProvider? _services;
    private Func<Task>? _stopHost;
    private TrayController? _tray;
    private FirstRunWindow? _startWindow;
    private bool _exitPromptDone;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // The headless test app has no desktop lifetime, so tests never build the real services.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += OnExit;

            // One listener for the whole run: what it brings up changes when first run hands over to the shell.
            Program.Instance?.Listen(() => Dispatcher.UIThread.Post(BringToFront));
            Start(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var paths = DataPaths.Default;
        var settings = new SettingsStore(paths.Settings);
        var read = settings.TryLoad();
        var saved = (read as SettingsReadResult.Ok)?.Settings.Connection;
        switch (ModeGate.Decide(read, File.Exists(paths.Database)))
        {
            case StartupChoice.RunLocal:
                RunLocal(desktop, paths);
                break;
            case StartupChoice.RunRemote:
                RunRemote(desktop, paths, saved!);
                break;
            case StartupChoice.ShowConnect:
                ShowStartWindow(desktop, paths, settings, StartStep.Connect, saved);
                break;
            case StartupChoice.ShowSettingsError:
                ShowStartWindow(desktop, paths, settings, StartStep.SettingsProblem, null);
                break;
            default:
                ShowStartWindow(desktop, paths, settings, StartStep.Choice, null);
                break;
        }
    }

    private void RunLocal(IClassicDesktopStyleApplicationLifetime desktop, DataPaths paths)
    {
        var services = Composition.Build(paths);
        var (window, tray) = BuildShell(services);
        var host = services.GetRequiredService<AppHost>();
        _stopHost = host.StopAsync;
        host.Start();

        // The lifetime shows whatever is set as MainWindow, so a start in the tray leaves it unset.
        if (_startWindow is null && StartWithWindows.IsTrayStart(desktop.Args)) _ = tray.StartHiddenAsync(host.SharingSettled);
        else Present(desktop, window);
    }

    // Nothing holds a viewer in the tray by itself, so a --tray start left from when this PC shared its library shows the
    // window, as the tray does whenever no reason applies.
    private void RunRemote(IClassicDesktopStyleApplicationLifetime desktop, DataPaths paths, StoredConnection connection)
    {
        var services = RemoteComposition.Build(paths, connection);
        var (window, _) = BuildShell(services);
        var host = services.GetRequiredService<RemoteHost>();
        _stopHost = host.StopAsync;
        host.Start();
        Present(desktop, window);
    }

    private (MainWindow Window, TrayController Tray) BuildShell(ServiceProvider services)
    {
        _services = services;
        services.GetRequiredService<ErrorReporter>().Install();
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
        // Kept alive by the window and settings events it listens to.
        _ = new UiSizeController(window, services.GetRequiredService<SettingsStore>(), Resources);
        var dialogs = services.GetRequiredService<Dialogs>();
        dialogs.Owner = window;
        var tray = new TrayController(window, services.GetServices<ITrayReason>(), question => dialogs.ConfirmAsync(question, "Quit"));
        _tray = tray;
        TrayIcon.SetIcons(this, [tray.Icon]);
        window.Closing += OnMainWindowClosing;
        return (window, tray);
    }

    // Before the lifetime has started it shows MainWindow itself. After first run nobody else will, and the first-run window
    // closes last: with no window left, the app would exit.
    private void Present(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        desktop.MainWindow = window;
        if (_startWindow is not { } start) return;

        window.Show();
        _startWindow = null;
        start.Close();
    }

    private void ShowStartWindow(IClassicDesktopStyleApplicationLifetime desktop, DataPaths paths, SettingsStore settings, StartStep step, StoredConnection? saved)
    {
        var connect = new ConnectViewModel(settings, Environment.MachineName, AppRestart.For(desktop).Run, saved);
        var actions = new StartActions(
            () => RunLocal(desktop, paths),
            () => TryAgain(desktop, settings),
            () => desktop.Shutdown());
        var previous = _startWindow;
        _startWindow = new FirstRunWindow { DataContext = new FirstRunViewModel(settings, connect, step, paths.Settings, actions) };
        desktop.MainWindow = _startWindow;
        if (previous is null) return;

        _startWindow.Show();
        previous.Close();
    }

    private void TryAgain(IClassicDesktopStyleApplicationLifetime desktop, SettingsStore settings)
    {
        if (settings.TryLoad() is not SettingsReadResult.Unreadable) Start(desktop);
    }

    private void BringToFront()
    {
        if (_tray is { } tray)
        {
            tray.ShowWindow();
        }
        else if (_startWindow is { } window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    // Closing goes to the tray while something needs the app running. Otherwise, while a file plays, closing asks about
    // the player first (R45). The close is cancelled, the question runs, then the window closes for real. Windows
    // shutting down skips all of this: AppHost closes the player. A viewer has no question to ask, so RemoteComposition
    // doesn't register the prompt and RemoteHost closes the player.
    private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitPromptDone || _services is not { } services || sender is not Window window) return;
        if (e.CloseReason == WindowCloseReason.OSShutdown) return;
        if (_tray is { } tray && tray.HideInstead(e)) return;
        if (services.GetService<PlayerExitPrompt>() is not { } prompt) return;
        if (services.GetRequiredService<Core.Playback.IPlaybackService>().CurrentPlayer is null) return;

        e.Cancel = true;
        _exitPromptDone = true;
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

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_services is not { } services || _stopHost is not { } stopHost) return;

        // Off the UI thread and bounded: the watcher alone can take 30 s to stop.
        var stop = Task.Run(async () =>
        {
            await stopHost().ConfigureAwait(false);
            await services.DisposeAsync().ConfigureAwait(false);
        });
        try
        {
            stop.Wait(ShutdownWait);
        }
        catch (AggregateException)
        {
            // Nothing left to report to: the window is closed and the process is exiting.
        }
    }
}
