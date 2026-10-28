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

        _library.Review.Add(FakeLibrary.ReviewFile(1, "a.mkv"));
        _library.Review.Add(FakeLibrary.ReviewFile(2, "b.mkv"));
        await Task.Run(_library.RaiseChanged); // a scan or identification run raises it off the UI thread
        Flush();

        await WaitUntil(() => vm.ReviewCount == 2);
        Assert.Equal(2, vm.NavItems.Single(i => i.Key == MainWindowViewModel.ReviewPage).Badge);
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
    public async Task Home_PromptsForFolder_WhenThereAreNone()
    {
        var vm = Create();
        var home = Assert.IsType<HomeViewModel>(vm.CurrentPage);
        Assert.False(home.ShowNoFolders); // nothing until the first load

        _library.RaiseChanged();
        Flush();
        await WaitUntil(() => home.IsLoaded);

        Assert.True(home.ShowNoFolders);
        Assert.False(home.ShowRows);
        home.AddFolderCommand.Execute(null);
        Assert.Equal("Folders", Assert.IsAssignableFrom<PageViewModel>(vm.CurrentPage).Title);
    }

    [AvaloniaFact]
    public async Task Home_ShowsRows_WhenAFolderExists()
    {
        _library.FolderRows.Add(FakeLibrary.Folder(1, @"D:\Movies"));
        var vm = Create();
        var home = Assert.IsType<HomeViewModel>(vm.CurrentPage);

        _library.RaiseChanged();
        Flush();
        await WaitUntil(() => home.IsLoaded);

        Assert.True(home.ShowRows);
        Assert.False(home.ShowNoFolders);
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
    }

    [AvaloniaFact]
    public async Task OpeningFolders_ReloadsTheList()
    {
        var vm = Create();
        _library.FolderRows.Add(FakeLibrary.Folder(1, @"D:\Movies"));

        vm.NavigateCommand.Execute(MainWindowViewModel.FoldersPage);
        var folders = Assert.IsType<FoldersViewModel>(vm.CurrentPage);
        await WaitUntil(() => folders.Folders.Count == 1);

        Assert.Equal(@"D:\Movies", folders.Folders[0].Path);
    }

    [AvaloniaFact]
    public async Task FoldersAndSettings_PagesRenderInTheWindow()
    {
        _library.FolderRows.Add(new MediaBoard.Core.Library.FolderRow(1, @"D:\Movies", true, false, false, 3, ["GTA IV"]));
        var vm = Create();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.NavigateCommand.Execute(MainWindowViewModel.FoldersPage);
        var folders = (FoldersViewModel)vm.CurrentPage;
        await WaitUntil(() => folders.Folders.Count == 1);
        Flush();
        var texts = TextsOf(window);
        Assert.Contains(@"D:\Movies", texts);
        Assert.Contains("3 files", texts);
        Assert.Contains("GTA IV", texts);
        Assert.Contains("Ignored: no video", texts);

        vm.NavigateCommand.Execute(MainWindowViewModel.SettingsPage);
        Flush();
        texts = TextsOf(window);
        Assert.Contains("TMDB key", texts);
        Assert.Contains("This product uses the TMDB API but is not endorsed or certified by TMDB.", texts);
        window.Close();
    }

    private static List<string?> TextsOf(Avalonia.Controls.Window window) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text).ToList();
}
