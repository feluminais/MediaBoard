using System.ComponentModel;
using System.Diagnostics;

namespace MediaBoard.Core.Players;

/// <summary>Starting, classifying the exit of, and closing a player process, the same way for every adapter.</summary>
internal static class PlayerLaunch
{
    /// <summary>A player that exits sooner than this without ever answering did not start properly.</summary>
    public static readonly TimeSpan EarlyExitWindow = TimeSpan.FromSeconds(3);

    /// <summary>How long disposal waits for a player to close before killing it.</summary>
    public static readonly TimeSpan DefaultCloseTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Starts the process. A missing program or any other launch problem comes back as <paramref name="failure"/>,
    /// never as an exception.
    /// </summary>
    public static IPlayerProcess? TryStart(
        Func<ProcessStartInfo, IPlayerProcess> start,
        ProcessStartInfo startInfo,
        string name,
        string exePath,
        out string failure)
    {
        failure = "";
        try
        {
            return start(startInfo);
        }
        catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3)
        {
            failure = $"{name} not found: {exePath}";
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            failure = $"{name} could not start: {e.Message}";
        }

        return null;
    }

    /// <summary>The exit code, or 0 (a normal exit) when it is not available.</summary>
    public static int ExitCodeOrZero(IPlayerProcess? process)
    {
        try
        {
            return process?.ExitCode ?? 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The final event for a player that exited: Failed when it never answered and left right after starting, or
    /// left with a non-zero code; Closed otherwise.
    /// </summary>
    public static PlaybackEvent ExitEvent(string name, int exitCode, bool answered, TimeSpan sinceStart) =>
        !answered && sinceStart < EarlyExitWindow ? new PlaybackEvent.Failed($"{name} closed right after starting")
        : exitCode != 0 ? new PlaybackEvent.Failed($"{name} stopped unexpectedly")
        : new PlaybackEvent.Closed();

    /// <summary>
    /// Ends a session's hold on the player: closes it like <see cref="CloseAsync"/>, or, for a detached session,
    /// only disposes the process handle and leaves the player running.
    /// </summary>
    public static Task ReleaseAsync(IPlayerProcess process, TimeSpan timeout, bool detached)
    {
        if (!detached) return CloseAsync(process, timeout);

        process.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Asks the player to close, kills it after <paramref name="timeout"/>, then disposes the process.</summary>
    public static async Task CloseAsync(IPlayerProcess process, TimeSpan timeout)
    {
        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                await Task.WhenAny(process.Exited, Task.Delay(timeout));
                if (!process.HasExited)
                {
                    process.Kill();
                    await Task.WhenAny(process.Exited, Task.Delay(timeout));
                }
            }
        }
        finally
        {
            process.Dispose();
        }
    }
}
