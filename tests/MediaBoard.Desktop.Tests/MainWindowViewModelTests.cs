using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MediaBoard.Core.Activity;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;

namespace MediaBoard.Desktop.Tests;

public class MainWindowViewModelTests
{
    private readonly ActivityHub _hub = new();
    private readonly FakeLibrary _library = new();

    private MainWindowViewModel Create() =>
        new(_library, new StatusBarViewModel(_hub), new ActivityPanelViewModel(_hub));

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    [AvaloniaFact]
    public async Task Sidebar_ReviewCountUpdates_OnLibraryChanged()
    {
        var vm = Create();
        _library.RaiseChanged();
        Flush();
        Assert.Equal(0, vm.ReviewCount);

        _library.Review.Add(FakeLibrary.ReviewFile(1, "a.mkv"));
        _library.Review.Add(FakeLibrary.ReviewFile(2, "b.mkv"));
        await Task.Run(_library.RaiseChanged); // a scan or identification run raises it off the UI thread
        Flush();

        Assert.Equal(2, vm.ReviewCount);
        Assert.Equal(2, vm.NavItems.Single(i => i.Key == MainWindowViewModel.ReviewPage).Badge);
    }

    [AvaloniaFact]
    public void CtrlF_OpensSearch()
    {
        var vm = Create();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Flush();

        Assert.True(vm.IsSearchOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void ActivityPanel_ShowsRecentWithHints()
    {
        _hub.Log(ActivityLevel.Info, "Scanned Movies");
        _hub.Log(ActivityLevel.Error, "TMDB rejected the API key.", "Check the key in Settings");
        var vm = new ActivityPanelViewModel(_hub);

        Assert.Equal(["TMDB rejected the API key.", "Scanned Movies"], vm.Entries.Select(e => e.Message));
        Assert.Equal("Check the key in Settings", vm.Entries[0].Hint);
        Assert.True(vm.Entries[0].IsError);
        Assert.Null(vm.Entries[1].Hint);

        _hub.Log(ActivityLevel.Warning, "Folder unavailable: Shows", "Connect the drive");
        Flush();

        Assert.Equal("Folder unavailable: Shows", vm.Entries[0].Message);
        Assert.Equal("Connect the drive", vm.Entries[0].Hint);
    }

    [AvaloniaFact]
    public void StatusBarClick_TogglesActivityPanel()
    {
        var vm = Create();
        Assert.False(vm.IsActivityOpen);

        vm.ToggleActivityCommand.Execute(null);
        Assert.True(vm.IsActivityOpen);

        vm.ToggleActivityCommand.Execute(null);
        Assert.False(vm.IsActivityOpen);
    }

    [AvaloniaFact]
    public void KeyMissingClick_OpensSettings()
    {
        var vm = Create();

        vm.StatusBar.OpenSettingsCommand.Execute(null);

        Assert.Equal("Settings", Assert.IsAssignableFrom<PageViewModel>(vm.CurrentPage).Title);
    }

    [AvaloniaFact]
    public void Navigate_ShowsPageAndMarksSidebar()
    {
        var vm = Create();
        Assert.IsType<HomeViewModel>(vm.CurrentPage);

        vm.NavigateCommand.Execute(MainWindowViewModel.MoviesPage);

        Assert.Equal("Movies", Assert.IsAssignableFrom<PageViewModel>(vm.CurrentPage).Title);
        Assert.Equal([MainWindowViewModel.MoviesPage], vm.NavItems.Where(i => i.IsSelected).Select(i => i.Key));
    }

    [AvaloniaFact]
    public void Home_PromptsForFolder_WhenThereAreNone()
    {
        var vm = Create();
        _library.RaiseChanged();
        Flush();

        var home = Assert.IsType<HomeViewModel>(vm.CurrentPage);
        Assert.True(home.HasNoFolders);

        home.AddFolderCommand.Execute(null);
        Assert.Equal("Folders", Assert.IsAssignableFrom<PageViewModel>(vm.CurrentPage).Title);
    }

    [AvaloniaFact]
    public void Home_ShowsRows_WhenAFolderExists()
    {
        _library.FolderRows.Add(FakeLibrary.Folder(1, @"D:\Movies"));
        var vm = Create();
        _library.RaiseChanged();
        Flush();

        var home = Assert.IsType<HomeViewModel>(vm.CurrentPage);
        Assert.False(home.HasNoFolders);
        Assert.True(home.IsLoaded);
    }
}
