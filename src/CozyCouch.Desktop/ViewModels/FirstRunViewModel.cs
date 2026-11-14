using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CozyCouch.Core.Settings;

namespace CozyCouch.Desktop.ViewModels;

public enum StartStep { Choice, Connect, SettingsProblem }

/// <param name="CarryOn">Opens the app on this PC's library.</param>
/// <param name="TryAgain">Reads the settings file again.</param>
/// <param name="Quit">Leaves the app.</param>
public sealed record StartActions(Action CarryOn, Action TryAgain, Action Quit);

/// <summary>
/// The window before any composition exists: the choice of first run, the Connect screen for a device with no way in, and
/// the one thing to say when the settings can't be read. Choosing is what sets the mode.
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly StartActions _actions;

    /// <param name="settings">Where the choice goes.</param>
    /// <param name="connect">The Connect screen.</param>
    /// <param name="step">What to show first.</param>
    /// <param name="settingsPath">The settings file, to name it when it can't be read.</param>
    /// <param name="actions">What the app does next.</param>
    public FirstRunViewModel(SettingsStore settings, ConnectViewModel connect, StartStep step, string settingsPath, StartActions actions)
    {
        _settings = settings;
        _actions = actions;
        Connect = connect;
        SettingsPath = settingsPath;
        Step = step;
        connect.Left += ShowChoice;
    }

    public ConnectViewModel Connect { get; }

    public string SettingsPath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoice), nameof(IsConnect), nameof(IsSettingsProblem))]
    public partial StartStep Step { get; private set; }

    /// <summary>Set when Try again found the file still held, so the press shows an answer.</summary>
    [ObservableProperty]
    public partial string? ProblemMessage { get; private set; }

    public bool IsChoice => Step == StartStep.Choice;

    public bool IsConnect => Step == StartStep.Connect;

    public bool IsSettingsProblem => Step == StartStep.SettingsProblem;

    public void Dispose()
    {
        Connect.Left -= ShowChoice;
        Connect.Dispose();
    }

    // A settings file that won't take the choice costs nothing: the library this creates answers the same question next time.
    [RelayCommand]
    private void AddFolder()
    {
        try
        {
            _settings.Update(s => s with { Mode = AppMode.ThisPc });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"The choice of this PC wasn't saved: {e.GetType().Name}");
        }

        _actions.CarryOn();
    }

    [RelayCommand]
    private void ConnectToServer() => Step = StartStep.Connect;

    [RelayCommand]
    private void TryAgain()
    {
        if (_settings.TryLoad() is SettingsReadResult.Unreadable) ProblemMessage = "Still can't read it.";
        else _actions.TryAgain();
    }

    [RelayCommand]
    private void Quit() => _actions.Quit();

    private void ShowChoice() => Step = StartStep.Choice;
}
