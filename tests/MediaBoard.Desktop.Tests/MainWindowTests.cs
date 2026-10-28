using Avalonia.Headless.XUnit;
using MediaBoard.Desktop.Views;

namespace MediaBoard.Desktop.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void MainWindow_IsTitledMediaBoard()
    {
        var window = new MainWindow();

        Assert.Equal("MediaBoard", window.Title);
    }
}
