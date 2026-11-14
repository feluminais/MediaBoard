using Avalonia;
using CozyCouch.Core;
using System;

namespace CozyCouch.Desktop;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Before anything opens the database or the certificate: a second copy of the certificate, once disposed,
        // can delete the key the first process is serving with.
        using var instance = SingleInstance.Claim(AppPaths.DataDir, args);
        if (!instance.ShouldRun(args)) return;

        Instance = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>This process's claim on the data directory, set for the whole run of the app and null under a designer or a test.</summary>
    internal static SingleInstance? Instance { get; private set; }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
