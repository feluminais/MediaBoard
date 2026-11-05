using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;

namespace MediaBoard.Desktop.Tests;

/// <summary>The window before the composition: what it shows, where the keyboard lands, and what the choice saves.</summary>
public sealed class FirstRunTests : IDisposable
{
    private static readonly ServerEndpoint Saved = new("Anna's library", ["127.0.0.1:1"], new string('A', 64));

    private readonly string _dir = Directory.CreateTempSubdirectory("mediaboard-first-run-").FullName;
    private readonly List<FirstRunWindow> _windows = [];
    private int _carriedOn;
    private int _triedAgain;
    private int _quit;

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        foreach (var window in _windows) window.Close();
        Directory.Delete(_dir, recursive: true);
    }

    private FirstRunViewModel Model(StartStep step = StartStep.Choice, StoredConnection? saved = null)
    {
        var settings = new SettingsStore(SettingsPath);
        return new FirstRunViewModel(
            settings,
            new ConnectViewModel(settings, "Laptop", () => true, saved),
            step,
            SettingsPath,
            new StartActions(() => _carriedOn++, () => _triedAgain++, () => _quit++));
    }

    private FirstRunWindow Show(FirstRunViewModel model)
    {
        var window = new FirstRunWindow { DataContext = model };
        _windows.Add(window);
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static List<string> ShownButtons(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string ?? b.Name ?? "").ToList();

    private static object? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    [Fact]
    public void AddFolder_SavesThisPc_AndCarriesOn()
    {
        var model = Model();

        model.AddFolderCommand.Execute(null);

        Assert.Equal(AppMode.ThisPc, new SettingsStore(SettingsPath).Load().Mode);
        Assert.Equal(1, _carriedOn);
    }

    // The first library settles the same question next time, so this is no reason to keep someone out.
    [Fact]
    public void AddFolder_SettingsWontSave_StillCarriesOn()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings());
        var model = Model();

        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            model.AddFolderCommand.Execute(null);
        }

        Assert.Equal(1, _carriedOn);
    }

    [Fact]
    public void Connect_FromTheChoice_AndBackAgain()
    {
        var model = Model();

        model.ConnectToServerCommand.Execute(null);

        Assert.True(model.IsConnect);

        model.Connect.BackCommand.Execute(null);

        Assert.True(model.IsChoice);
    }

    [Fact]
    public void SettingsProblem_TriesAgain_OrQuits()
    {
        var model = Model(StartStep.SettingsProblem);

        model.TryAgainCommand.Execute(null);
        model.QuitCommand.Execute(null);

        Assert.Equal(1, _triedAgain);
        Assert.Equal(1, _quit);
    }

    [Fact]
    public void SettingsProblem_FileStillHeld_SaysSo()
    {
        new SettingsStore(SettingsPath).Save(new AppSettings());
        var model = Model(StartStep.SettingsProblem);

        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            model.TryAgainCommand.Execute(null);
        }

        Assert.Equal(0, _triedAgain);
        Assert.NotNull(model.ProblemMessage);

        model.TryAgainCommand.Execute(null);

        Assert.Equal(1, _triedAgain);
    }

    // No mode is saved until someone chooses, so closing the window asks again next time.
    [Fact]
    public void ClosedWithoutChoosing_SavesNothing()
    {
        Model().Dispose();

        Assert.False(File.Exists(SettingsPath));
    }

    [AvaloniaFact]
    public void Choice_ShowsTwoPaths_WithTheKeyboardOnTheFirst()
    {
        var window = Show(Model());

        var first = window.FindControl<Button>("AddFolderChoice");

        Assert.Equal(["AddFolderChoice", "ConnectChoice"], ShownButtons(window).Where(b => b.EndsWith("Choice", StringComparison.Ordinal)));
        Assert.Same(first, Focused(window));
    }

    [AvaloniaFact]
    public void Connect_TheCaretIsInTheInviteBox()
    {
        var model = Model();
        var window = Show(model);

        model.ConnectToServerCommand.Execute(null);
        Settle(window);

        var box = Assert.IsType<TextBox>(Focused(window));
        Assert.Equal("InviteBox", box.Name);
    }

    [AvaloniaFact]
    public void SignedOutDevice_TheCaretWaitsForThePassword_WithInviteOffered()
    {
        var saved = new StoredConnection(Saved, "Sam", "Laptop", null);
        var window = Show(Model(StartStep.Connect, saved));

        var box = Assert.IsType<TextBox>(Focused(window));

        Assert.Equal("PasswordBox", box.Name);
        Assert.Contains("Use an invite", ShownButtons(window));
        var name = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NameBox");
        Assert.Equal("Sam", name.Text);
    }

    [AvaloniaFact]
    public void Password_IsMasked_AndLeavesNoUndoTrail()
    {
        var window = Show(Model(StartStep.Connect, new StoredConnection(Saved, "Sam", "Laptop", null)));

        foreach (var box in window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Name is "PasswordBox" or "RepeatBox"))
        {
            Assert.Equal('•', box.PasswordChar);
            Assert.False(box.IsUndoEnabled);
        }
    }

    [AvaloniaFact]
    public void Enter_InTheInviteBox_Sends_ButShiftEnterBreaksTheLine()
    {
        var model = Model();
        var window = Show(model);
        model.ConnectToServerCommand.Execute(null);
        Settle(window);
        model.Connect.InviteText = "nonsense";

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
        Settle(window);

        Assert.Null(model.Connect.Message);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);

        Assert.NotNull(model.Connect.Message);
    }

    [AvaloniaFact]
    public void HiddenConnectStep_TakesNoEnterOrEscape()
    {
        var model = Model(StartStep.SettingsProblem);
        var window = Show(model);
        var left = 0;
        model.Connect.Left += () => left++;

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(window);

        Assert.Equal(0, left);
        Assert.Null(model.Connect.Message);
        Assert.Equal(1, _triedAgain);
        Assert.Equal(1, _quit);
    }

    [AvaloniaFact]
    public void Connect_EnterSends_EscapeGoesBack()
    {
        var model = Model();
        var window = Show(model);
        model.ConnectToServerCommand.Execute(null);
        Settle(window);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(window);

        Assert.True(model.IsChoice);
    }

    [AvaloniaFact]
    public void PressingEmptySpace_TakesTheCaretOutOfATextBox()
    {
        var model = Model();
        var window = Show(model);
        model.ConnectToServerCommand.Execute(null);
        Settle(window);
        Assert.IsType<TextBox>(Focused(window));

        window.MouseDown(new Avalonia.Point(4, window.Bounds.Height - 4), MouseButton.Left);
        window.MouseUp(new Avalonia.Point(4, window.Bounds.Height - 4), MouseButton.Left);
        Settle(window);

        Assert.IsNotType<TextBox>(Focused(window));
        Assert.NotNull(Focused(window));
    }

    [AvaloniaFact]
    public void SettingsProblem_NamesTheFile()
    {
        var window = Show(Model(StartStep.SettingsProblem));

        var path = window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.IsEffectivelyVisible);

        Assert.Equal(SettingsPath, path.Text);
        Assert.Equal(["Quit", "Try again"], ShownButtons(window).Where(b => b is "Quit" or "Try again"));
    }

    [AvaloniaFact]
    public void Closing_ForgetsWhatWasTyped()
    {
        var model = Model();
        var window = Show(model);
        model.Connect.Password = "correct horse battery";

        window.Close();
        _windows.Clear();

        Assert.Equal("", model.Connect.Password);
    }
}
