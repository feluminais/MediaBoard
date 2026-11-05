using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
    private TrayController? _tray;
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
            _services = Composition.Build();
            _services.GetRequiredService<ErrorReporter>().Install();
            var window = new MainWindow { DataContext = _services.GetRequiredService<MainWindowViewModel>() };
            // Kept alive by the window and settings events it listens to.
            _ = new UiSizeController(window, _services.GetRequiredService<Core.Settings.SettingsStore>(), Resources);
            var dialogs = _services.GetRequiredService<Dialogs>();
            dialogs.Owner = window;
            _tray = new TrayController(window, _services.GetServices<ITrayReason>(), question => dialogs.ConfirmAsync(question, "Quit"));
            TrayIcon.SetIcons(this, [_tray.Icon]);
            window.Closing += OnMainWindowClosing;
            desktop.Exit += OnExit;

            var host = _services.GetRequiredService<AppHost>();
            host.Start();
            // The lifetime shows whatever is set as MainWindow, so a start in the tray leaves it unset.
            if (StartWithWindows.IsTrayStart(desktop.Args)) _ = _tray.StartHiddenAsync(host.SharingSettled);
            else desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Closing goes to the tray while something needs the app running. Otherwise, while a file plays, closing asks about
    // the player first (R45). The close is cancelled, the question runs, then the window closes for real. Windows
    // shutting down skips all of this: AppHost closes the player.
    private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitPromptDone || _services is not { } services || sender is not Window window) return;
        if (e.CloseReason == WindowCloseReason.OSShutdown) return;
        if (_tray is { } tray && tray.HideInstead(e)) return;
        var prompt = services.GetRequiredService<PlayerExitPrompt>();
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
        if (_services is not { } services) return;

        // Off the UI thread and bounded: the watcher alone can take 30 s to stop.
        var stop = Task.Run(async () =>
        {
            await services.GetRequiredService<AppHost>().StopAsync().ConfigureAwait(false);
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
