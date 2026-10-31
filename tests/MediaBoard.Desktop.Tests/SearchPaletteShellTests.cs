using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Metadata;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;

namespace MediaBoard.Desktop.Tests;

/// <summary>The palette inside the shell: what its choices do to the pages, and how the window shows it.</summary>
public partial class MainWindowViewModelTests
{
    private static readonly SearchCandidate MatrixHit = new(603, TitleKind.Movie, "The Matrix", null, 1999, 50, 8.2, null, null);

    [AvaloniaFact]
    public async Task Palette_OpensEmpty_EveryTime()
    {
        _library.LocalSearch = _ => [FakeLibrary.Card(1, "Heat")];
        var vm = Create();
        vm.OpenSearchCommand.Execute(null);
        vm.Search.Query = "heat";
        await WaitUntil(() => vm.Search.Rows.Count > 0);

        vm.CloseSearchCommand.Execute(null);
        vm.OpenSearchCommand.Execute(null);

        Assert.True(vm.IsSearchOpen);
        Assert.Equal("", vm.Search.Query);
        Assert.Empty(vm.Search.Rows);
    }

    [AvaloniaFact]
    public async Task Palette_LibraryHit_OpensTheTitlePage_AndClosesThePalette()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Heat");
        _library.LocalSearch = _ => [FakeLibrary.Card(1, "Heat")];
        var vm = Create();
        var home = vm.CurrentPage;
        vm.OpenSearchCommand.Execute(null);
        vm.Search.Query = "heat";
        await WaitUntil(() => vm.Search.Rows.Count > 0);

        vm.Search.OpenSelectedCommand.Execute(null);

        var page = Assert.IsType<TitleViewModel>(vm.CurrentPage);
        Assert.Equal(1, vm.OpenTitleId);
        Assert.False(vm.IsSearchOpen);
        page.BackCommand.Execute(null);
        Assert.Same(home, vm.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Palette_OnlineHit_OpensTheOnlinePage_NotInLibrary()
    {
        var titles = new Titles(_library, _hub);
        titles.Search.Candidates.Add(MatrixHit);
        titles.Search.Details = new TitleDetails(603, TitleKind.Movie, "The Matrix", 1999, null, "A hacker learns.", 8.2, null, 136, []);
        var clock = new ManualClock();
        var vm = Create(titles, clock: clock);
        vm.OpenSearchCommand.Execute(null);
        vm.Search.Query = "matrix";
        await WaitUntil(() => vm.Search.LibraryTask.IsCompleted);
        clock.Advance(SearchPaletteViewModel.OnlineDelay);
        await WaitUntil(() => vm.Search.Rows.Any(r => r.Text == "The Matrix"));

        vm.Search.OpenSelectedCommand.Execute(null);

        var page = Assert.IsType<TitleViewModel>(vm.CurrentPage);
        Assert.True(page.IsOnline);
        await WaitUntil(() => page.IsLoaded);
        Assert.Equal("Not in library", page.Tag);
        Assert.False(vm.IsSearchOpen);
    }

    [AvaloniaFact]
    public void Palette_SettingsCommand_OpensSettings()
    {
        var titles = new Titles(_library, _hub);
        titles.Search.CommandList.Add(new("settings", "Open settings"));
        var vm = Create(titles);
        vm.OpenSearchCommand.Execute(null);
        vm.Search.Query = ">";

        vm.Search.Rows.Single(r => r.Text == "Open settings").OpenCommand.Execute(null);

        Assert.IsType<SettingsViewModel>(vm.CurrentPage);
        Assert.False(vm.IsSearchOpen);
    }

    [AvaloniaFact]
    public async Task Palette_HiddenCommand_OpensMoviesWithShowHidden()
    {
        var titles = new Titles(_library, _hub);
        titles.Search.CommandList.Add(new("hidden", "Show hidden items"));
        var vm = Create(titles);
        vm.OpenSearchCommand.Execute(null);
        vm.Search.Query = ">";

        vm.Search.Rows.Single(r => r.Text == "Show hidden items").OpenCommand.Execute(null);

        var movies = Assert.IsType<TitleGridViewModel>(vm.CurrentPage);
        Assert.Equal("Movies", movies.Title);
        Assert.True(movies.ShowHidden);
        Assert.True(vm.NavItems.Single(i => i.Key == MainWindowViewModel.MoviesPage).IsSelected);
        await WaitUntil(() => movies.IsLoaded);
    }

    [AvaloniaFact]
    public async Task Palette_HiddenCommand_OnAGridPage_StaysOnIt()
    {
        var titles = new Titles(_library, _hub);
        titles.Search.CommandList.Add(new("hidden", "Show hidden items"));
        var vm = Create(titles);
        vm.NavigateCommand.Execute(MainWindowViewModel.ShowsPage);
        var shows = Assert.IsType<TitleGridViewModel>(vm.CurrentPage);
        vm.OpenSearchCommand.Execute(null);
        vm.Search.Query = ">";

        vm.Search.Rows.Single(r => r.Text == "Show hidden items").OpenCommand.Execute(null);

        Assert.Same(shows, vm.CurrentPage);
        Assert.True(shows.ShowHidden);
        await WaitUntil(() => shows.IsLoaded);
    }

    [AvaloniaFact]
    public async Task Palette_RescanCommand_RunsOnce_OffTheUiThread_AndClosesThePalette()
    {
        var titles = new Titles(_library, _hub);
        titles.Search.CommandList.Add(new("rescan", "Rescan library"));
        var threads = new List<int>();
        var vm = Create(titles, rescan: () =>
        {
            lock (threads) threads.Add(Environment.CurrentManagedThreadId);
            return Task.CompletedTask;
        });
        vm.OpenSearchCommand.Execute(null);
        vm.Search.Query = ">";

        vm.Search.Rows.Single(r => r.Text == "Rescan library").OpenCommand.Execute(null);
        await WaitUntil(() => threads.Count == 1);

        Assert.NotEqual(Environment.CurrentManagedThreadId, threads[0]);
        Assert.False(vm.IsSearchOpen);
    }

    [AvaloniaFact]
    public async Task Palette_RescanThatFails_IsLogged()
    {
        var titles = new Titles(_library, _hub);
        titles.Search.CommandList.Add(new("rescan", "Rescan library"));
        var vm = Create(titles, rescan: () => throw new InvalidOperationException("disk gone"));
        vm.OpenSearchCommand.Execute(null);
        vm.Search.Query = ">";

        vm.Search.Rows.Single(r => r.Text == "Rescan library").OpenCommand.Execute(null);

        await WaitUntil(() => _hub.Recent.Any(e => e.Level == ActivityLevel.Error));
        Assert.Contains(_hub.Recent, e => e.Message.Contains("disk gone"));
    }

    [AvaloniaFact]
    public void Palette_CtrlF_PutsTheCaretInTheBox_EscapeReturnsFocus()
    {
        var vm = Create();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var elsewhere = window.FindControl<Button>("StatusStrip")!;
        elsewhere.Focus();
        Flush();
        Assert.Same(elsewhere, window.FocusManager!.GetFocusedElement());

        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Flush();

        Assert.True(vm.IsSearchOpen);
        var input = window.FindControl<SearchPalette>("Palette")!.FindControl<TextBox>("Input");
        Assert.Same(input, window.FocusManager!.GetFocusedElement());

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Flush();

        Assert.False(vm.IsSearchOpen);
        Assert.Same(elsewhere, window.FocusManager!.GetFocusedElement());
        window.Close();
    }

    [AvaloniaFact]
    public void Palette_Escape_ClosesIt_WhenTheFocusIsNotInTheBox()
    {
        var vm = Create();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Flush();
        window.FindControl<Button>("StatusStrip")!.Focus();
        Flush();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Flush();

        Assert.False(vm.IsSearchOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void PressingEmptySpace_TakesTheFocusOutOfATextBox_ToTheRoot()
    {
        var vm = Create();
        vm.NavigateCommand.Execute(MainWindowViewModel.SettingsPage);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Flush();
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "KeyBox");
        box.Focus();
        Flush();
        Assert.Same(box, window.FocusManager!.GetFocusedElement());

        window.MouseDown(new Point(1100, 400), MouseButton.Left);
        Flush();

        Assert.Same(window.FindControl<Panel>("Root"), window.FocusManager!.GetFocusedElement());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Palette_TypingArrowsAndEnter_WorkFromTheKeyboard()
    {
        _library.Details[2] = FakeLibrary.Movie(2, "Dune");
        _library.LocalSearch = _ => [FakeLibrary.Card(1, "Heat"), FakeLibrary.Card(2, "Dune")];
        var vm = Create();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Flush();

        window.KeyTextInput("du");
        await WaitUntil(() => vm.Search.Rows.Count == 3);
        Assert.Equal("du", vm.Search.Query);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal("Dune", vm.Search.Selected?.Text);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Flush();

        Assert.False(vm.IsSearchOpen);
        Assert.Equal(2, vm.OpenTitleId);
        Assert.IsType<TitleViewModel>(vm.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void Palette_ClickingTheBackdrop_Closes_ClickingThePaletteDoesNot()
    {
        var vm = Create();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.OpenSearchCommand.Execute(null);
        Flush();
        var palette = window.FindControl<SearchPalette>("Palette")!;
        var inside = palette.TranslatePoint(new Point(20, 20), window)!.Value;

        window.MouseDown(inside, MouseButton.Left);
        window.MouseUp(inside, MouseButton.Left);
        Flush();
        Assert.True(vm.IsSearchOpen);

        var outside = new Point(window.Width - 10, window.Height - 60);
        window.MouseDown(outside, MouseButton.Left);
        window.MouseUp(outside, MouseButton.Left);
        Flush();

        Assert.False(vm.IsSearchOpen);
        window.Close();
    }
}
