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

    private void Scan(int folderId, string name, int done, int total)
    {
        _hub.BeginScan(folderId, name);
        _hub.ReportScan(folderId, done, total);
    }

    [AvaloniaFact]
    public void Problem_ShowsTheLatestWarningOrError()
    {
        var vm = Create();
        Assert.Null(vm.Problem);

        _hub.Log(ActivityLevel.Warning, "Couldn't start MPC-HC", "Choose another player in Settings");
        Flush();

        Assert.Equal("Couldn't start MPC-HC", vm.Problem);
    }

    [AvaloniaFact]
    public void Problem_CountsSeveral()
    {
        var vm = Create();

        _hub.Log(ActivityLevel.Warning, "File missing: Heat.mkv");
        _hub.Log(ActivityLevel.Error, "Couldn't delete the files: access denied");
        Flush();

        Assert.Equal("2 problems", vm.Problem);
    }

    [AvaloniaFact]
    public void Problem_IgnoresInfo()
    {
        var vm = Create();

        _hub.Log(ActivityLevel.Info, "Added 3 files");
        Flush();

        Assert.Null(vm.Problem);
    }

    [AvaloniaFact]
    public void Problem_ClearsOnceSeen_AndSkipsWhatArrivesWhileTheActivityPanelIsOpen()
    {
        var vm = Create();
        _hub.Log(ActivityLevel.Warning, "File missing: Heat.mkv");
        Flush();

        vm.IsActivityOpen = true;
        Assert.Null(vm.Problem);
        _hub.Log(ActivityLevel.Error, "Couldn't fix the match: locked");
        Flush();
        Assert.Null(vm.Problem);

        vm.IsActivityOpen = false;
        _hub.Log(ActivityLevel.Error, "Couldn't save progress");
        Flush();
        Assert.Equal("Couldn't save progress", vm.Problem);
    }

    [AvaloniaFact]
    public void StatusBar_ShowsScanProgress()
    {
        var vm = Create();

        Scan(1, "Supernatural", 12, 44);
        Flush();

        Assert.Equal("Scanning Supernatural, 12 of 44", vm.Progress);
    }

    [AvaloniaFact]
    public void StatusBar_ShowsUpToDate_WhenNothingRuns()
    {
        var vm = Create();
        Scan(1, "Supernatural", 12, 44);
        Flush();

        _hub.EndScan(1);
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
    public void StatusBar_ShowsLibraryError_InsteadOfUpToDate()
    {
        var vm = Create();

        _hub.SetLibraryError("Make sure no other program is using library.db.");
        Flush();

        Assert.Equal("Can't open the library", vm.LibraryProblem);
        Assert.Equal("Make sure no other program is using library.db.", vm.LibraryErrorHint);
        Assert.Null(vm.Progress);
    }

    [AvaloniaFact]
    public void StatusBar_ShowsFailedScan_UntilItWorksAgain()
    {
        var vm = Create();

        _hub.SetFailedScans(["Movies"]);
        Flush();
        Assert.Equal("Scan failed: Movies", vm.FailedScans);
        Assert.Null(vm.Progress);

        _hub.SetFailedScans([]);
        Flush();
        Assert.Null(vm.FailedScans);
        Assert.Equal("Up to date", vm.Progress);
    }

    [AvaloniaFact]
    public async Task StatusBar_UpdatesFromBackgroundThread()
    {
        var vm = Create();

        await Task.Run(() => Scan(1, "Movies", 3, 9));
        Flush();

        Assert.Equal("Scanning Movies, 3 of 9", vm.Progress);
    }
}
