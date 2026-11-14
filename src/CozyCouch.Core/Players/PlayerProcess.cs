using System.Diagnostics;

namespace CozyCouch.Core.Players;

/// <summary>The few things a player session needs from the player's process.</summary>
public interface IPlayerProcess : IDisposable
{
    /// <summary>Completes when the process exits.</summary>
    Task Exited { get; }

    bool HasExited { get; }

    /// <summary>The exit code; read only after <see cref="Exited"/> has completed.</summary>
    int ExitCode { get; }

    /// <summary>Asks the player to close (WM_CLOSE to its main window).</summary>
    void CloseMainWindow();

    void Kill();
}

/// <summary>An <see cref="IPlayerProcess"/> over a real <see cref="Process"/>.</summary>
public sealed class PlayerProcess : IPlayerProcess
{
    private readonly Process _process;

    private PlayerProcess(Process process)
    {
        _process = process;
        Exited = process.WaitForExitAsync();
    }

    /// <summary>Starts the process. Throws what <see cref="Process.Start(ProcessStartInfo)"/> throws.</summary>
    public static IPlayerProcess Start(ProcessStartInfo startInfo) =>
        new PlayerProcess(Process.Start(startInfo) ?? throw new InvalidOperationException("No process was started"));

    public Task Exited { get; }

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public void CloseMainWindow() => _process.CloseMainWindow();

    public void Kill()
    {
        try
        {
            _process.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It exited in the meantime, or is already on its way out.
        }
    }

    public void Dispose() => _process.Dispose();
}
