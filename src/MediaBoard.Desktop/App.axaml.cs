using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop;

public partial class App : Application
{
    // Closing waits at most this long for services to stop; the window is already gone by then.
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(10);

    private ServiceProvider? _services;

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
            var window = new MainWindow { DataContext = _services.GetRequiredService<MainWindowViewModel>() };
            _services.GetRequiredService<Dialogs>().Owner = window;
            desktop.MainWindow = window;
            desktop.Exit += OnExit;
            _services.GetRequiredService<AppHost>().Start();
        }

        base.OnFrameworkInitializationCompleted();
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
