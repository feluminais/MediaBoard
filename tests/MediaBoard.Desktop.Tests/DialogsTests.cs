using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MediaBoard.Desktop.Tests.Support;

namespace MediaBoard.Desktop.Tests;

public class DialogsTests
{
    [AvaloniaFact]
    public async Task WithoutAWindow_EveryPromptSaysNo()
    {
        Assert.False(await new Dialogs().ConfirmAsync("Remove it?", "Remove"));
        Assert.Null(await new Dialogs().PickFolderAsync());
        Assert.Null(await new Dialogs().PickProgramAsync());
        Assert.False(await new Dialogs().AskClosePlayerAsync("MPC-HC")); // leaves the player playing
    }

    [AvaloniaFact]
    public async Task HiddenWindow_ComesBackForTheDialog()
    {
        var owner = new Window();
        owner.Show();
        owner.Hide();
        var dialogs = new Dialogs { Owner = owner };

        var answer = dialogs.AskFinishedAsync("Heat");
        Ui.Flush();

        Assert.True(owner.IsVisible);
        Assert.Single(owner.OwnedWindows).Close();
        Assert.Null(await answer);
        owner.Close();
    }

    [AvaloniaTheory]
    [InlineData("Remove", true)]
    [InlineData("Cancel", false)]
    public async Task Confirm_ShowsTheQuestion_AndReturnsTheButtonPressed(string button, bool expected)
    {
        var owner = new Window();
        owner.Show();
        var dialogs = new Dialogs { Owner = owner };

        var answer = dialogs.ConfirmAsync("Remove \"Movies\" from the library? Your files are never touched.", "Remove");
        Ui.Flush();

        var dialog = Assert.Single(owner.OwnedWindows);
        var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Remove \"Movies\" from the library? Your files are never touched.", texts);
        dialog.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == button)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(expected, await answer);
        owner.Close();
    }

    [AvaloniaTheory]
    [InlineData("Close it", true)]
    [InlineData("Leave it playing", false)]
    public async Task AskClosePlayer_NamesThePlayer_AndReturnsTheButtonPressed(string button, bool expected)
    {
        var owner = new Window();
        owner.Show();
        var dialogs = new Dialogs { Owner = owner };

        var answer = dialogs.AskClosePlayerAsync("MPC-HC");
        Ui.Flush();

        var dialog = Assert.Single(owner.OwnedWindows);
        var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("MPC-HC is still playing. Close it too?", texts);
        Assert.Contains("If you leave it playing, MediaBoard won't save how far you get.", texts);
        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
        var leave = buttons.Single(b => (string?)b.Content == "Leave it playing");
        Assert.True(leave.IsDefault);
        Assert.True(leave.IsCancel);
        buttons.Single(b => (string?)b.Content == button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(expected, await answer);
        owner.Close();
    }

}
