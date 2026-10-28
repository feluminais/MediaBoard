using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Players;
using MediaBoard.Core.Settings;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;

namespace MediaBoard.Desktop.Tests;

public class HomeViewModelTests : IDisposable
{
    private readonly ActivityHub _hub = new();
    private readonly FakeLibrary _library = new();
    private readonly FakePlayback _playback = new();
    private readonly List<string> _asked = [];
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"mediaboard-home-{Guid.NewGuid():N}.json");

    public HomeViewModelTests() => _library.FolderRows.Add(FakeLibrary.Folder(1, @"D:\Movies"));

    public void Dispose() => File.Delete(_settingsPath);

    private MainWindowViewModel Create() =>
        new(_library, new StatusBarViewModel(_hub), new ActivityPanelViewModel(_hub), _hub,
            new FoldersViewModel(_library, new IdleFolderActions(), _hub, () => Task.FromResult<string?>(null), _ => Task.FromResult(true)),
            new SettingsViewModel(
                new SettingsStore(_settingsPath),
                new PlayerDetector(_ => false, () => [], _hub),
                _hub, () => Task.FromResult<string?>(null), _ => { }),
            Posters.Local(_asked), new Titles(_library, _hub, _playback) { Posters = Posters.Local(_asked) }.Services());

    private async Task<(MainWindowViewModel Shell, HomeViewModel Home)> LoadedHome()
    {
        var shell = Create();
        var home = Assert.IsType<HomeViewModel>(shell.CurrentPage);
        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => home.IsLoaded);
        return (shell, home);
    }

    [AvaloniaFact]
    public async Task Home_ReloadsWhenOpened()
    {
        var (shell, home) = await LoadedHome();
        Assert.Empty(home.ContinueWatching);
        shell.NavigateCommand.Execute(MainWindowViewModel.MoviesPage);
        _library.Continue.Add(FakeLibrary.Resume(1, "Klaus")); // progress saved while Home was away, without a library change

        shell.NavigateCommand.Execute(MainWindowViewModel.HomePage);

        await Ui.WaitUntil(() => home.ContinueWatching.Count == 1);
        Assert.Equal("Klaus", home.ContinueWatching[0].Name);
    }

    [AvaloniaFact]
    public async Task Home_TwoRows_ContinueAndRecent()
    {
        _library.Continue.Add(FakeLibrary.Resume(1, "Klaus", poster: "/k.jpg"));
        _library.Continue.Add(FakeLibrary.Resume(2, "Arcane", episode: "S01E04"));
        _library.Recent.Add(FakeLibrary.Card(3, "Coco", poster: "/c.jpg"));
        _library.Recent.Add(FakeLibrary.Card(4, "Heat"));
        _library.Recent.Add(FakeLibrary.Card(5, "Up", offline: true));

        var (_, home) = await LoadedHome();

        // Most recently played first, newest first.
        Assert.Equal(["Arcane", "Klaus"], home.ContinueWatching.Select(c => c.Name));
        Assert.Equal(["Up", "Heat", "Coco"], home.RecentlyAdded.Select(c => c.Name));
        Assert.Equal("S01E04", home.ContinueWatching[0].Subtitle);
        Assert.Equal("Not identified (offline)", home.RecentlyAdded[0].Subtitle);
        Assert.Equal(HomeViewModel.RowLimit, _library.Limits.First(l => l.Query == "ContinueWatching").Limit);
        Assert.False(home.NothingInProgress);
    }

    [AvaloniaFact]
    public async Task Row_OpenAll_NavigatesToFullList()
    {
        for (var i = 1; i <= 30; i++)
        {
            _library.Continue.Add(FakeLibrary.Resume(i, $"Film {i}"));
            _library.Recent.Add(FakeLibrary.Card(i, $"Film {i}"));
        }

        var (shell, home) = await LoadedHome();
        Assert.Equal(HomeViewModel.RowLimit, home.ContinueWatching.Count); // the row is short

        home.OpenContinueAllCommand.Execute(null);
        var continueList = Assert.IsType<ListViewModel>(shell.CurrentPage);
        await Ui.WaitUntil(() => continueList.IsLoaded);
        Assert.Equal("Continue watching", continueList.Title);
        Assert.Equal(30, continueList.Items.Count); // all in-progress items
        Assert.Contains(("ContinueWatching", int.MaxValue), _library.Limits);

        shell.NavigateCommand.Execute(MainWindowViewModel.HomePage);
        home.OpenRecentAllCommand.Execute(null);
        var recentList = Assert.IsType<ListViewModel>(shell.CurrentPage);
        await Ui.WaitUntil(() => recentList.IsLoaded);
        Assert.Equal("Recently added", recentList.Title);
        Assert.Equal(30, recentList.Items.Count); // the whole library
        Assert.Equal("Film 30", recentList.Items[0].Name); // newest first
        Assert.Contains(("RecentlyAdded", int.MaxValue), _library.Limits);
    }

    [AvaloniaFact]
    public async Task Home_Empty_ExplainsWhy()
    {
        _library.Recent.Add(FakeLibrary.Card(1, "Coco"));
        var (_, home) = await LoadedHome();

        Assert.True(home.NothingInProgress);

        var view = new HomeView { DataContext = home };
        new Window { Width = 1000, Height = 700, Content = view }.Show();
        var text = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Nothing in progress");
        Assert.True(text.IsVisible);
    }

    [AvaloniaFact]
    public async Task Home_Rows_ShowTitlesAndOpenAllArrows()
    {
        _library.Continue.Add(FakeLibrary.Resume(1, "Klaus"));
        _library.Recent.Add(FakeLibrary.Card(2, "Coco"));
        var (_, home) = await LoadedHome();

        var view = new HomeView { DataContext = home };
        new Window { Width = 1000, Height = 700, Content = view }.Show();

        Assert.Equal(2, view.GetVisualDescendants().OfType<Button>().Count(b => b.Content as string == "\u203A"));
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Continue watching", texts);
        Assert.Contains("Recently added", texts);
        Assert.Contains("Klaus", texts);
        Assert.Contains("Coco", texts);
    }

    [AvaloniaFact]
    public async Task ClickingARecentCard_OpensTheTitle()
    {
        _library.Recent.Add(FakeLibrary.Card(7, "Coco"));
        var (shell, home) = await LoadedHome();

        home.RecentlyAdded[0].OpenCommand.Execute(null);

        Assert.Equal(7, shell.OpenTitleId);
        Assert.IsType<TitleViewModel>(shell.CurrentPage);
    }

    [AvaloniaFact]
    public async Task TitlePage_BackReturnsToTheWholePageItCameFrom()
    {
        _library.Recent.Add(FakeLibrary.Card(7, "Coco"));
        _library.Details[7] = FakeLibrary.Movie(7, "Coco");
        var (shell, home) = await LoadedHome();
        home.RecentlyAdded[0].OpenCommand.Execute(null);
        var page = Assert.IsType<TitleViewModel>(shell.CurrentPage);
        await Ui.WaitUntil(() => page.IsLoaded);
        Assert.Equal("Coco", page.Header.Name);

        page.BackCommand.Execute(null);

        Assert.Same(home, shell.CurrentPage);
    }

    [AvaloniaFact]
    public async Task TitlePage_RefreshesWhenTheLibraryChanges()
    {
        _library.Recent.Add(FakeLibrary.Card(7, "Coco"));
        _library.Details[7] = FakeLibrary.Movie(7, "Coco");
        var (shell, home) = await LoadedHome();
        home.RecentlyAdded[0].OpenCommand.Execute(null);
        var page = Assert.IsType<TitleViewModel>(shell.CurrentPage);
        await Ui.WaitUntil(() => page.IsLoaded);

        _library.Details[7] = FakeLibrary.Movie(7, "Coco, renamed");
        await Task.Run(_library.RaiseChanged);
        Ui.Flush();

        await Ui.WaitUntil(() => page.Header.Name == "Coco, renamed");
    }

    [AvaloniaFact]
    public async Task OnlineTitle_OpensFromTheShell_AndBackWorks()
    {
        var (shell, home) = await LoadedHome();

        shell.OpenOnlineTitle(603, MediaBoard.Core.Metadata.TitleKind.Movie);

        var page = Assert.IsType<TitleViewModel>(shell.CurrentPage);
        Assert.True(page.IsOnline);
        page.BackCommand.Execute(null);
        Assert.Same(home, shell.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Sidebar_DropsTheTitlePage_AndBackFromNothingGoesHome()
    {
        _library.Recent.Add(FakeLibrary.Card(7, "Coco"));
        var (shell, home) = await LoadedHome();
        home.RecentlyAdded[0].OpenCommand.Execute(null);

        shell.NavigateCommand.Execute(MainWindowViewModel.MoviesPage);
        Assert.IsType<TitleGridViewModel>(shell.CurrentPage);

        shell.OpenOnlineTitle(1, MediaBoard.Core.Metadata.TitleKind.Show);
        Assert.IsType<TitleViewModel>(shell.CurrentPage);
        ((TitleViewModel)shell.CurrentPage).BackCommand.Execute(null);
        Assert.IsType<TitleGridViewModel>(shell.CurrentPage); // not the Home page opened before the sidebar click
    }

    [AvaloniaFact]
    public async Task ClickingAContinueCard_ResumesThePlayback_OffTheUiThread()
    {
        _library.Continue.Add(FakeLibrary.Resume(7, "Klaus"));
        var (_, home) = await LoadedHome();

        home.ContinueWatching[0].OpenCommand.Execute(null);
        await Ui.WaitUntil(() => _playback.Played.Count == 1);

        var (fileId, fromStart, threadId) = _playback.Played.Single();
        Assert.Equal(70, fileId);
        Assert.False(fromStart);
        Assert.NotEqual(Environment.CurrentManagedThreadId, threadId);
    }

    [AvaloniaFact]
    public async Task Posters_ResolveOnlyForCardsThatAskForThem()
    {
        _library.Recent.Add(FakeLibrary.Card(1, "Coco", poster: "/c.jpg"));
        _library.Recent.Add(FakeLibrary.Card(2, "Heat", poster: "/h.jpg"));
        _library.Recent.Add(FakeLibrary.Card(3, "Up")); // TMDB has no poster for it
        var (_, home) = await LoadedHome();
        Assert.Empty(_asked); // nothing scrolled into view yet

        var heat = home.RecentlyAdded.Single(c => c.Name == "Heat");
        heat.RequestPoster();
        home.RecentlyAdded.Single(c => c.Name == "Up").RequestPoster();
        await Ui.WaitUntil(() => heat.PosterLocalPath is not null);

        Assert.Equal("local:/h.jpg", heat.PosterLocalPath);
        Assert.Equal(["/h.jpg"], _asked);
        Assert.Null(home.RecentlyAdded.Single(c => c.Name == "Coco").PosterLocalPath);
    }

    [AvaloniaFact]
    public async Task Posters_AreNotAskedForAgain_WhenAlreadyResolved()
    {
        _library.Recent.Add(FakeLibrary.Card(1, "Coco", poster: "/c.jpg"));
        var (_, home) = await LoadedHome();
        var coco = home.RecentlyAdded[0];
        coco.RequestPoster();
        await Ui.WaitUntil(() => coco.PosterLocalPath is not null);

        coco.RequestPoster();
        coco.RequestPoster();
        await Task.Delay(50);

        Assert.Single(_asked);
    }

    [AvaloniaFact]
    public async Task Refresh_KeepsTheCardsAndTheirPosters_WhenNothingChanged()
    {
        _library.Recent.Add(FakeLibrary.Card(1, "Coco", poster: "/c.jpg"));
        var (_, home) = await LoadedHome();
        var coco = home.RecentlyAdded[0];
        coco.RequestPoster();
        await Ui.WaitUntil(() => coco.PosterLocalPath is not null);

        await home.RefreshAsync();

        Assert.Same(coco, home.RecentlyAdded[0]); // same card, so no redraw and no flicker
        Assert.Equal("local:/c.jpg", home.RecentlyAdded[0].PosterLocalPath);
    }

    [AvaloniaFact]
    public async Task Refresh_ChangedProgress_KeepsTheResolvedPoster()
    {
        _library.Continue.Add(FakeLibrary.Resume(1, "Klaus", poster: "/k.jpg", position: 100));
        var (_, home) = await LoadedHome();
        var card = home.ContinueWatching[0];
        card.RequestPoster();
        await Ui.WaitUntil(() => card.PosterLocalPath is not null);

        _library.Continue[0] = FakeLibrary.Resume(1, "Klaus", poster: "/k.jpg", position: 3000);
        await home.RefreshAsync();

        Assert.NotSame(card, home.ContinueWatching[0]);
        Assert.Equal("local:/k.jpg", home.ContinueWatching[0].PosterLocalPath); // no placeholder frame
        Assert.Equal(0.5, home.ContinueWatching[0].Progress);
    }

    [AvaloniaFact]
    public async Task LeavingThePage_CancelsPosterWork()
    {
        var gate = new TaskCompletionSource<string?>();
        var cancelled = false;
        var loader = new PosterLoader(async (_, _, ct) =>
        {
            ct.Register(() => cancelled = true);
            return await gate.Task;
        });
        var card = PosterCardViewModel.For(FakeLibrary.Card(1, "Coco", poster: "/c.jpg"), loader, _ => { });

        card.RequestPoster();
        await Task.Delay(50);
        card.CancelPoster();
        await Ui.WaitUntil(() => cancelled);
        gate.SetResult("late");
        await Task.Delay(50);
        Ui.Flush();

        Assert.Null(card.PosterLocalPath); // a late answer for a card nobody shows is dropped
    }

    [AvaloniaFact]
    public async Task LibraryChange_ReloadsTheGridOnlyWhileItIsOpen()
    {
        _library.TitleCards.Add(FakeLibrary.Card(1, "Coco"));
        var (shell, _) = await LoadedHome();
        Assert.Empty(_library.TitleQueries); // Home is open: the big grids stay untouched

        shell.NavigateCommand.Execute(MainWindowViewModel.MoviesPage);
        var grid = Assert.IsType<TitleGridViewModel>(shell.CurrentPage);
        await Ui.WaitUntil(() => grid.IsLoaded);
        var queries = _library.TitleQueries.Count;

        _library.TitleCards.Add(FakeLibrary.Card(2, "Heat"));
        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => grid.Items.Count == 2);

        Assert.True(_library.TitleQueries.Count > queries);
    }

    private sealed class IdleFolderActions : IFolderActions
    {
        public Task<FolderOutcome> AddAsync(string path) => Task.FromResult(FolderOutcome.Done);

        public Task<FolderOutcome> RemoveAsync(int folderId) => Task.FromResult(FolderOutcome.Done);

        public Task<FolderOutcome> SetEnabledAsync(int folderId, bool enabled) => Task.FromResult(FolderOutcome.Done);

        public Task<FolderOutcome> RepointAsync(int folderId, string newPath) => Task.FromResult(FolderOutcome.Done);

        public Task WhenIdleAsync() => Task.CompletedTask;
    }
}
