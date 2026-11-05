using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;

namespace MediaBoard.Desktop;

/// <summary>
/// Starts the app over after a change of mode, since the two compositions can't share a process. The new process goes
/// first, so one that won't start leaves this one running with a message instead of nothing on screen. It waits on
/// <see cref="SingleInstance.RestartArgument"/> until this one has stopped and let go of the data directory.
/// </summary>
/// <param name="processPath">This program, or null when that can't be told.</param>
/// <param name="start">Starts a program with arguments.</param>
/// <param name="shutdown">Ends this process.</param>
public sealed class AppRestart(string? processPath, Action<string, string> start, Action shutdown)
{
    public static AppRestart For(IClassicDesktopStyleApplicationLifetime desktop) =>
        new(Environment.ProcessPath, (path, arguments) => Process.Start(path, arguments).Dispose(), () => desktop.Shutdown());

    /// <returns>False when the new process couldn't start, and this one carries on.</returns>
    public bool Run()
    {
        if (string.IsNullOrEmpty(processPath)) return false;

        try
        {
            start(processPath, SingleInstance.RestartArgument);
        }
        catch (Win32Exception e)
        {
            Trace.TraceWarning($"Restarting failed: {e.GetType().Name} {e.NativeErrorCode}");
            return false;
        }

        shutdown();
        return true;
    }
}
