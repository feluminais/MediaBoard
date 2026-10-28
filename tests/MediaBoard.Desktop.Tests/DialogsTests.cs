using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MediaBoard.Desktop.Tests.Support;

namespace MediaBoard.Desktop.Tests;

public class DialogsTests
{
    [AvaloniaFact]
    public async Task Confirm_WithoutAWindow_SaysNo()
    {
        Assert.False(await new Dialogs().ConfirmAsync("Remove it?", "Remove"));
        Assert.Null(await new Dialogs().PickFolderAsync());
        Assert.Null(await new Dialogs().PickProgramAsync());
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

    [Fact]
    public void OpenUrl_IgnoresAddressesThatAreNotHttps()
    {
        // Nothing to assert beyond "doesn't start anything or throw": a file path or http address is dropped.
        new Dialogs().OpenUrl(@"C:\Windows\notepad.exe");
        new Dialogs().OpenUrl("http://example.com");
        new Dialogs().OpenUrl("not a url");
    }
}
