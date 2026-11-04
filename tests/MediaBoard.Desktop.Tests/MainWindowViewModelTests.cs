using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MediaBoard.Core.Activity;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;

namespace MediaBoard.Desktop.Tests;

public partial class MainWindowViewModelTests
{
    private readonly ActivityHub _hub = new();
    private readonly FakeLibrary _library = new();

    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"mediaboard-shell-{Guid.NewGuid():N}.json");

    private MainWindowViewModel Create(Titles? titles = null, Func<Task>? rescan = null, TimeProvider? clock = null) =>
        new(_library, new StatusBarViewModel(_hub), new ActivityPanelViewModel(_hub), _hub,
            new FoldersViewModel(_library, new IdleFolderActions(), _hub, () => Task.FromResult<string?>(null), _ => Task.FromResult(true)),
            new SettingsViewModel(
                new MediaBoard.Core.Settings.SettingsStore(_settingsPath),
                new MediaBoard.Core.Players.PlayerDetector(_ => false, () => [], _hub),
                _hub, () => Task.FromResult<string?>(null), _ => { }),
            Posters.None(), (titles ?? new Titles(_library, _hub)).Services(), rescan, clock);

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    // The pages only need something to call; nothing in these tests adds or removes a folder.
    private sealed class IdleFolderActions : IFolderActions
    {
        public Task<FolderOutcome> AddAsync(string path) => Task.FromResult(FolderOutcome.Done);

        public Task<FolderOutcome> RemoveAsync(int folderId) => Task.FromResult(FolderOutcome.Done);

        public Task<FolderOutcome> SetEnabledAsync(int folderId, bool enabled) => Task.FromResult(FolderOutcome.Done);

        public Task<FolderOutcome> RepointAsync(int folderId, string newPath) => Task.FromResult(FolderOutcome.Done);

        public Task WhenIdleAsync() => Task.CompletedTask;
    }

    // Queries run on the thread pool; pump the UI thread until their results are applied.
    private static async Task WaitUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The view model never caught up.");
            await Task.Delay(10);
            Flush();
        }
    }

    [AvaloniaFact]
    public async Task Sidebar_ReviewCountUpdates_OnLibraryChanged()
    {
        var vm = Create();
        _library.RaiseChanged();
        Flush();
        await WaitUntil(() => vm.CurrentPage is HomeViewModel { IsLoaded: true });
        Assert.Equal(0, vm.ReviewCount);
        Assert.False(vm.HasReview);

        _library.Review.Add(FakeLibrary.ReviewFile(1, "a.mkv"));
        _library.RaiseChanged();
        Flush();
        await WaitUntil(() => vm.HasReview);
        Assert.Equal("1 needs review", vm.ReviewItem.Label);

        _library.Review.Add(FakeLibrary.ReviewFile(2, "b.mkv"));
        await Task.Run(_library.RaiseChanged); // a scan or identification run raises it off the UI thread
        Flush();

        await WaitUntil(() => vm.ReviewCount == 2);
        Assert.True(vm.HasReview);
        Assert.Equal("2 need review", vm.ReviewItem.Label);

        _library.Review.Clear();
        _library.RaiseChanged();
        Flush();
        await WaitUntil(() => !vm.HasReview);
    }

    [AvaloniaFact]
    public void ReviewItem_IsSelected_WhileTheReviewPageIsOpen()
    {
        var vm = Create();

        vm.NavigateCommand.Execute(MainWindowViewModel.ReviewPage);

        Assert.IsType<ReviewViewModel>(vm.CurrentPage);
        Assert.True(vm.ReviewItem.IsSelected);
        Assert.DoesNotContain(vm.NavItems, i => i.IsSelected);

        vm.NavigateCommand.Execute(MainWindowViewModel.MoviesPage);

        Assert.False(vm.ReviewItem.IsSelected);
    }

    [AvaloniaFact]
    public async Task ResolvingTheLastItem_HidesTheSidebarItem_AndStaysOnTheReviewPage()
    {
        _library.Review.Add(FakeLibrary.ReviewFile(1, "a.mkv"));
        var vm = Create();
        _library.RaiseChanged();
        Flush();
        await WaitUntil(() => vm.HasReview);
        vm.NavigateCommand.Execute(MainWindowViewModel.ReviewPage);
        var page = Assert.IsType<ReviewViewModel>(vm.CurrentPage);
        await WaitUntil(() => page.IsLoaded && page.Groups.Count > 0);

        _library.MarkNotMedia(1);
        _library.RaiseChanged();
        Flush();

        await WaitUntil(() => !vm.HasReview && page.Groups.Count == 0);
        Assert.Same(page, vm.CurrentPage);
        Assert.True(vm.ReviewItem.IsSelected);
        Assert.Equal("Nothing to review", page.EmptyText);
    }

    [AvaloniaFact]
    public async Task ReviewItem_RendersAtTheBottomOfTheSidebar_AndOpensTheReviewPageOnClick()
    {
        var vm = Create();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var button = () => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Button>().Single(b => b.Name == "ReviewNavButton");
        Flush();
        Assert.False(button().IsVisible);

        _library.Review.Add(FakeLibrary.ReviewFile(1, "a.mkv"));
        _library.Review.Add(FakeLibrary.ReviewFile(2, "b.mkv"));
        _library.RaiseChanged();
        Flush();
        await WaitUntil(() => vm.HasReview);
        Flush();
        Assert.True(button().IsVisible);
        Assert.Contains("2 need review", TextsOf(window));
        var sidebarBottom = Avalonia.VisualExtensions.TranslatePoint(button(), new Avalonia.Point(0, button().Bounds.Height), window)!.Value.Y;
        var statusTop = Avalonia.VisualExtensions.TranslatePoint(window.FindControl<Button>("StatusStrip")!, default, window)!.Value.Y;
        Assert.InRange(statusTop - sidebarBottom, 0, 24); // pinned to the bottom edge, above the status bar

        button().Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Flush();

        Assert.IsType<ReviewViewModel>(vm.CurrentPage);
        Assert.True(vm.ReviewItem.IsSelected);
        Assert.Contains("selected", button().Classes);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OpeningNeedsReview_LoadsThePage_AndAChangeReloadsIt()
    {
        _library.Review.Add(FakeLibrary.ReviewFile(1, "a.mkv"));
        var vm = Create();

        vm.NavigateCommand.Execute(MainWindowViewModel.ReviewPage);
        var page = Assert.IsType<ReviewViewModel>(vm.CurrentPage);
        await WaitUntil(() => page.IsLoaded);
        Assert.Equal([1], page.Groups.SelectMany(g => g.Rows).Select(r => r.FileId));

        _library.Review.Add(FakeLibrary.ReviewFile(2, "b.mkv"));
        _library.RaiseChanged(); // a scan or identification run
        Flush();

        await WaitUntil(() => page.Groups.SelectMany(g => g.Rows).Count() == 2);
    }

    [AvaloniaFact]
    public async Task LeavingNeedsReview_StopsItsTmdbLookups()
    {
        _hub.SetOnline(true);
        _hub.SetKeyState(MediaBoard.Core.Activity.KeyState.Ok);
        using var gate = new ManualResetEventSlim();
        var titles = new Titles(_library, _hub);
        titles.Search.OnDetails = (_, _) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return null;
        };
        foreach (var id in new[] { 1, 2 })
            _library.Review.Add(new MediaBoard.Core.Library.ReviewItem(
                id, $"{id}.mkv", $"{id}.mkv", "T", null, MediaBoard.Core.Parsing.MediaKind.Movie, null, [], false,
                [new MediaBoard.Core.Library.ReviewCandidate(id, MediaBoard.Core.Metadata.TitleKind.Movie, "T", 2000)]));
        var vm = Create(titles);

        vm.NavigateCommand.Execute(MainWindowViewModel.ReviewPage);
        await WaitUntil(() => titles.Search.DetailRequests.Count == 1); // the first lookup is in flight
        vm.NavigateCommand.Execute(MainWindowViewModel.HomePage);
        gate.Set();
        await Task.Delay(200);
        Flush();

        Assert.Single(titles.Search.DetailRequests);
    }

    [AvaloniaFact]
    public async Task Refresh_ReturnsAtOnce_WhileAQueryBlocks()
    {
        using var gate = new ManualResetEventSlim();
        var uiThread = Environment.CurrentManagedThreadId;
        var queryThreads = new List<int>();
        _library.Review.Add(FakeLibrary.ReviewFile(1, "a.mkv"));
        _library.OnQuery = _ =>
        {
            lock (queryThreads) queryThreads.Add(Environment.CurrentManagedThreadId);
            gate.Wait(TimeSpan.FromSeconds(10));
        };
        var vm = Create();

        _library.RaiseChanged();
        Flush(); // runs the posted refresh; it must not wait for the blocked queries

        Assert.Equal(0, vm.ReviewCount);
        gate.Set();
        await WaitUntil(() => vm.ReviewCount == 1 && vm.CurrentPage is HomeViewModel { IsLoaded: true });
        lock (queryThreads) Assert.DoesNotContain(uiThread, queryThreads);
    }

    [AvaloniaFact]
    public async Task Refresh_DropsAStaleResult()
    {
        using var firstGate = new ManualResetEventSlim();
        using var firstDone = new ManualResetEventSlim();
        var calls = 0;
        _library.Review.Add(FakeLibrary.ReviewFile(1, "a.mkv"));
        _library.OnQuery = name =>
        {
            if (name != nameof(FakeLibrary.NeedsReviewCount) || Interlocked.Increment(ref calls) != 1) return;
            firstGate.Wait(TimeSpan.FromSeconds(10));
            firstDone.Set();
        };
        var vm = Create();

        _library.RaiseChanged(); // first count reads 1 and blocks
        Flush();
        _library.Review.Add(FakeLibrary.ReviewFile(2, "b.mkv"));
        _library.RaiseChanged(); // second count reads 2 and returns at once
        Flush();
        await WaitUntil(() => vm.ReviewCount == 2);

        firstGate.Set();
        Assert.True(firstDone.Wait(TimeSpan.FromSeconds(10)));
        await Task.Delay(50);
        Flush();

        Assert.Equal(2, vm.ReviewCount);
    }

    [AvaloniaFact]
    public async Task Refresh_LogsAFailedQuery_WithoutCrashing()
    {
        _library.OnQuery = _ => throw new InvalidOperationException("database is locked");
        var vm = Create();

        _library.RaiseChanged();
        Flush();
        // Three queries read the library and fail: the review count, Home and Folders.
        await WaitUntil(() => _hub.Recent.Count(e => e.Level == ActivityLevel.Error) == 3);

        var error = _hub.Recent.First(e => e.Level == ActivityLevel.Error);
        Assert.Contains("database is locked", error.Message);
        Assert.Equal(BackgroundQuery.FailureHint, error.Hint);
        Assert.Equal(0, vm.ReviewCount);

        // The same failure again is not logged twice.
        var logged = _hub.Recent.Count;
        _library.RaiseChanged();
        Flush();
        await Task.Delay(100);
        Flush();
        Assert.Equal(logged, _hub.Recent.Count);
    }

    [AvaloniaFact]
    public void LibraryError_ShowsOnHome_AndOpensActivity()
    {
        var vm = Create();

        _hub.SetLibraryError("Make sure no other program is using library.db.");
        Flush();

        var home = Assert.IsType<HomeViewModel>(vm.CurrentPage);
        Assert.True(home.ShowError);
        Assert.False(home.ShowNoFolders);
        Assert.Equal("Make sure no other program is using library.db.", home.LibraryError);
        Assert.True(vm.IsActivityOpen);
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
    public void OpeningTheActivityPanel_MarksProblemsSeen()
    {
        var vm = Create();
        _hub.Log(ActivityLevel.Warning, "Couldn't start MPC-HC");
        Flush();
        Assert.Equal("Couldn't start MPC-HC", vm.StatusBar.Problem);

        vm.ToggleActivityCommand.Execute(null);

        Assert.Null(vm.StatusBar.Problem);
    }

    [AvaloniaFact]
    public async Task FoldersPage_ShowsTheNewCounts_OnceTheScanIsRecorded()
    {
        _library.FolderRows.Add(FakeLibrary.Folder(1, @"G:\Torrent Downloads"));
        var vm = Create();
        vm.NavigateCommand.Execute(MainWindowViewModel.FoldersPage);
        var folders = Assert.IsType<FoldersViewModel>(vm.CurrentPage);
        await Ui.WaitUntil(() => folders.Folders.Count == 1);
        _hub.BeginScan(1, "Torrent Downloads");
        _hub.ReportScan(1, 810, 810);
        Flush();
        _hub.EndScan(1);
        Flush();
        Assert.Equal("Scanning… 810 of 810 files", folders.Folders[0].Summary);

        // What AppHost does next: record the result, which tells the library it changed.
        _library.FolderRows[0] = _library.FolderRows[0] with { FileCount = 810 };
        _library.RaiseChanged();

        await Ui.WaitUntil(() => folders.Folders[0].Summary == "810 files");
    }

    // The hub outlives every page; a page made for one visit must not stay on its event.
    private int StatusSubscribers()
    {
        var field = typeof(ActivityHub).GetField(nameof(ActivityHub.StatusChanged),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (field.GetValue(_hub) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    [AvaloniaFact]
    public async Task RecentlyAddedList_LeavesTheHub_WhenItIsLeft()
    {
        var vm = Create();
        var before = StatusSubscribers();

        for (var visit = 0; visit < 3; visit++)
        {
            vm.NavigateCommand.Execute(MainWindowViewModel.HomePage);
            var home = Assert.IsType<HomeViewModel>(vm.CurrentPage);
            home.OpenRecentAllCommand.Execute(null);
            var list = Assert.IsType<ListViewModel>(vm.CurrentPage);
            await Ui.WaitUntil(() => list.IsLoaded);
            Assert.Equal(before + 1, StatusSubscribers()); // it follows the scans while open

            vm.NavigateCommand.Execute(MainWindowViewModel.MoviesPage);
            Assert.Equal(before, StatusSubscribers());
        }
    }

    [AvaloniaFact]
    public async Task RecentlyAddedList_FollowsTheHubAgain_WhenComingBackToIt()
    {
        var vm = Create();
        var home = Assert.IsType<HomeViewModel>(vm.CurrentPage);
        home.OpenRecentAllCommand.Execute(null);
        var list = Assert.IsType<ListViewModel>(vm.CurrentPage);
        await Ui.WaitUntil(() => list.IsLoaded);
        vm.OpenOnlineTitle(603, MediaBoard.Core.Metadata.TitleKind.Movie); // the list goes on the back stack
        Assert.IsType<TitleViewModel>(vm.CurrentPage).BackCommand.Execute(null);
        Assert.Same(list, vm.CurrentPage);

        _hub.BeginScan(1, "Torrent Downloads");
        Flush();

        Assert.Equal("Scanning folders…", list.EmptyText);
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

    [AvaloniaTheory]
    [InlineData(false)] // no folders: the prompt
    [InlineData(true)] // a folder: the rows
    public async Task Home_ShowsRowsOrThePrompt_ByWhetherAFolderExists(bool hasFolder)
    {
        if (hasFolder) _library.FolderRows.Add(FakeLibrary.Folder(1, @"D:\Movies"));
        var vm = Create();
        var home = Assert.IsType<HomeViewModel>(vm.CurrentPage);
        Assert.False(home.ShowNoFolders); // nothing until the first load

        _library.RaiseChanged();
        Flush();
        await WaitUntil(() => home.IsLoaded);

        Assert.Equal(!hasFolder, home.ShowNoFolders);
        Assert.Equal(hasFolder, home.ShowRows);
    }

    [AvaloniaFact]
    public async Task FirstRun_Home_PromptsForFolderAndKey()
    {
        var vm = Create();
        var home = Assert.IsType<HomeViewModel>(vm.CurrentPage);
        _library.RaiseChanged();
        Flush();
        await WaitUntil(() => home.IsLoaded);

        Assert.True(home.ShowNoFolders);
        Assert.True(home.ShowKeyPrompt); // no key yet

        home.AddKeyCommand.Execute(null);
        Assert.Equal("Settings", Assert.IsAssignableFrom<PageViewModel>(vm.CurrentPage).Title);
    }

    [AvaloniaFact]
    public async Task FirstRun_Home_HidesKeyPrompt_OnceThereIsAKey()
    {
        var vm = Create();
        var home = Assert.IsType<HomeViewModel>(vm.CurrentPage);
        _library.RaiseChanged();
        Flush();
        await WaitUntil(() => home.IsLoaded);
        Assert.True(home.ShowKeyPrompt);

        _hub.SetKeyState(KeyState.Ok);
        await WaitUntil(() => !home.ShowKeyPrompt);

        Assert.True(home.ShowNoFolders); // the folder prompt stays
    }

    [AvaloniaFact]
    public async Task Home_AddFolderPrompt_OpensTheFoldersPage_AndAsksForAFolder()
    {
        var asked = 0;
        var folders = new FoldersViewModel(_library, new IdleFolderActions(), _hub, () => { asked++; return Task.FromResult<string?>(null); }, _ => Task.FromResult(true));
        var vm = new MainWindowViewModel(_library, new StatusBarViewModel(_hub), new ActivityPanelViewModel(_hub), _hub, folders,
            new SettingsViewModel(
                new MediaBoard.Core.Settings.SettingsStore(_settingsPath),
                new MediaBoard.Core.Players.PlayerDetector(_ => false, () => [], _hub),
                _hub, () => Task.FromResult<string?>(null), _ => { }),
            Posters.None(), new Titles(_library, _hub).Services());
        var home = Assert.IsType<HomeViewModel>(vm.CurrentPage);

        home.AddFolderCommand.Execute(null);
        await WaitUntil(() => asked == 1);

        Assert.Same(folders, vm.CurrentPage);
        Assert.Equal("Folders", Assert.IsAssignableFrom<PageViewModel>(vm.CurrentPage).Title);
    }

    private static List<string?> TextsOf(Avalonia.Controls.Window window) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text).ToList();
}
