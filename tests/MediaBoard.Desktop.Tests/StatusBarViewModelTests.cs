using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MediaBoard.Core.Activity;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Tests;

public class StatusBarViewModelTests
{
    private readonly ActivityHub _hub = new();

    private StatusBarViewModel Create() => new(_hub);

    // Hub events are posted to the UI thread; run them before asserting.
    private static void Flush() => Dispatcher.UIThread.RunJobs();

    [AvaloniaFact]
    public void StatusBar_ShowsScanProgress()
    {
        var vm = Create();

        _hub.SetScan("Supernatural", 12, 44);
        Flush();

        Assert.Equal("Scanning Supernatural, 12 of 44", vm.Progress);
    }

    [AvaloniaFact]
    public void StatusBar_ShowsUpToDate_WhenNothingRuns()
    {
        var vm = Create();
        _hub.SetScan("Supernatural", 12, 44);
        Flush();

        _hub.SetScan(null, 0, 0);
        Flush();

        Assert.Equal("Up to date", vm.Progress);
    }

    [AvaloniaFact]
    public void StatusBar_ShowsOnlineOrOffline()
    {
        var vm = Create();

        _hub.SetOnline(true);
        Flush();
        Assert.Equal("Online", vm.Connection);

        _hub.SetOnline(false);
        Flush();
        Assert.Equal("Offline", vm.Connection);
    }

    [AvaloniaFact]
    public void StatusBar_ShowsKeyMissing_AndClickOpensSettings()
    {
        var vm = Create();
        var opened = 0;
        vm.SettingsRequested += () => opened++;

        Assert.Equal("TMDB key missing", vm.KeyProblem);

        vm.OpenSettingsCommand.Execute(null);
        Assert.Equal(1, opened);
    }

    [AvaloniaFact]
    public void StatusBar_ShowsKeyRejected_AndNothingWhenKeyWorks()
    {
        var vm = Create();

        _hub.SetKeyState(KeyState.Rejected);
        Flush();
        Assert.Equal("TMDB key rejected", vm.KeyProblem);

        _hub.SetKeyState(KeyState.Ok);
        Flush();
        Assert.Null(vm.KeyProblem);
    }

    [AvaloniaFact]
    public void StatusBar_ShowsUnavailableFolder()
    {
        var vm = Create();

        _hub.SetUnavailableFolders(["Torrent Downloads"]);
        Flush();

        Assert.Equal("Folder unavailable: Torrent Downloads", vm.UnavailableFolders);
    }

    [AvaloniaFact]
    public void StatusBar_ShowsIdentifyingCount_InsteadOfUpToDate()
    {
        var vm = Create();

        _hub.SetPendingIdentification(37);
        Flush();

        Assert.Equal("Identifying 37", vm.Identifying);
        Assert.Null(vm.Progress);
    }

    [AvaloniaFact]
    public void StatusBar_ShowsPlayerWhilePlaying()
    {
        var vm = Create();

        _hub.SetPlayer("MPC-HC");
        Flush();
        Assert.Equal("MPC-HC", vm.Player);

        _hub.SetPlayer(null);
        Flush();
        Assert.Null(vm.Player);
    }

    [AvaloniaFact]
    public async Task StatusBar_UpdatesFromBackgroundThread()
    {
        var vm = Create();

        await Task.Run(() => _hub.SetScan("Movies", 3, 9));
        Flush();

        Assert.Equal("Scanning Movies, 3 of 9", vm.Progress);
    }
}
