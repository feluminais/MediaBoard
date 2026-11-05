using System.Security;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Settings;
using Microsoft.Win32;

namespace MediaBoard.Desktop.Sharing;

/// <summary>The registry value that starts the app at sign-in.</summary>
public interface IRunKey
{
    /// <param name="command">What Windows should run, or null to remove the value.</param>
    void Set(string? command);
}

/// <summary>The current user's Run key, untouched until <see cref="Set"/> is called.</summary>
public sealed class RegistryRunKey : IRunKey
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MediaBoard";

    public void Set(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (command is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, command);
    }
}

/// <summary>The "Start with Windows" option. Windows starts the app with <see cref="TrayArgument"/>, so no window opens.</summary>
public sealed class StartWithWindows(SettingsStore settings, IRunKey key, string program, ActivityHub hub)
{
    public const string TrayArgument = "--tray";

    public static bool IsTrayStart(IEnumerable<string>? args) => args?.Contains(TrayArgument, StringComparer.Ordinal) ?? false;

    /// <returns>False when the key or the settings file refused, which is logged. The two then stay as they were.</returns>
    public bool Set(bool on)
    {
        try
        {
            key.Set(CommandFor(on));
            try
            {
                settings.Update(s => s with { Sharing = s.Sharing with { StartWithWindows = on } });
            }
            catch (Exception e) when (IsRefusal(e))
            {
                key.Set(CommandFor(!on));
                throw;
            }

            return true;
        }
        catch (Exception e) when (IsRefusal(e))
        {
            hub.Log(ActivityLevel.Error, "Couldn't change Start with Windows.");
            return false;
        }
    }

    private static bool IsRefusal(Exception e) => e is IOException or UnauthorizedAccessException or SecurityException;

    private string? CommandFor(bool on) => on ? $"\"{program}\" {TrayArgument}" : null;
}
