using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CozyCouch.Core.Activity;
using CozyCouch.Core.Library;
using CozyCouch.Core.Metadata;
using CozyCouch.Desktop.Controls;
using CozyCouch.Desktop.Tests.Support;
using CozyCouch.Desktop.ViewModels;
using CozyCouch.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CozyCouch.Desktop.Tests;

/// <summary>
/// A long show must not build a thousand rows, and a refresh, a closed edit or a page swap must not leave the keyboard
/// focus on nothing (the next Tab would start at the top of the window).
/// </summary>
public class FocusAndScaleTests
{
    private readonly ActivityHub _hub = new();
    private readonly FakeLibrary _library = new();
    private readonly Titles _t;

    public FocusAndScaleTests()
    {
        _hub.SetOnline(true);
        _hub.SetKeyState(KeyState.Ok);
        _t = new Titles(_library, _hub);
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Settle(Window window)
    {
        Settle();
        window.UpdateLayout();
        Settle();
        window.UpdateLayout();
    }

    // The list sits under the header and the graph, out of the first screen: bring it into view so its rows exist.
    private static void ScrollToList(Window window, TitleView view)
    {
        view.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
        Settle(window);
    }

    private static IInputElement? Focused(Window window) => window.FocusManager!.GetFocusedElement();

    // Season 1..seasons, perSeason episodes each; the first episode is watched when asked.
    private void LongShow(int seasons, int perSeason, bool firstWatched = false)
    {
        var list = new List<SeasonView>();
        var id = 5000;
        for (var s = 1; s <= seasons; s++)
        {
            var episodes = new List<EpisodeRow>();
            for (var e = 1; e <= perSeason; e++, id++)
                episodes.Add(FakeLibrary.Episode(id, s, e, $"Episode {s}-{e}", id + 100_000, watched: firstWatched && s == 1 && e == 1));
            list.Add(FakeLibrary.Season(2, s, [.. episodes]));
        }

        _library.Details[2] = FakeLibrary.Show(2, "Long runner", [.. list]);
        _library.Next[2] = 100_000 + 5000;
    }

    private async Task<(TitleViewModel Page, TitleView View, Window Window)> ShownTitle(int titleId)
    {
        var page = _t.Page(titleId);
        await page.RefreshAsync();
        var view = new TitleView { DataContext = page };
        var window = new Window { Width = 1100, Height = 700, Content = view };
        window.Show();
        Settle(window);
        return (page, view, window);
    }

    [AvaloniaFact]
    public async Task TitlePage_ThousandEpisodes_BuildsOnlyTheRowsInView()
    {
        LongShow(10, 100);

        var (page, view, window) = await ShownTitle(2);

        // One season's episodes are listed at a time (the graph picks the season), every row the same height.
        Assert.Equal(100, page.Rows.Count);
        Assert.Equal(100 * TitleViewModel.RowHeight, page.ListHeight);
        Assert.InRange(view.GetVisualDescendants().OfType<Button>().Count(), 1, 400);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TitlePage_Refresh_KeepsTheFocusedRowButton()
    {
        LongShow(2, 10);
        var (page, view, window) = await ShownTitle(2);
        ScrollToList(window, view);
        var button = view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Mark watched" && b.Classes.Contains("rowAction"));
        button.Focus();
        Assert.Same(button, Focused(window));

        LongShow(2, 10, firstWatched: true); // what pressing the button does to the library
        await page.RefreshAsync();
        Settle(window);

        Assert.Same(button, Focused(window));
        Assert.Equal("Mark unwatched", button.Content);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TitlePage_RenameClosed_GivesTheFocusBackToTheRenameButton()
    {
        LongShow(1, 5);
        var (page, view, window) = await ShownTitle(2);
        var episode = page.Seasons[0].Episodes[1];
        ScrollToList(window, view);
        var rename = view.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.Command, episode.BeginRenameCommand));
        rename.Focus();

        episode.BeginRenameCommand.Execute(null);
        Settle(window);
        Assert.IsType<RenameBox>(Focused(window));

        episode.CancelRenameCommand.Execute(null);
        Settle(window);

        Assert.Same(rename, Focused(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task TitlePage_FixMatch_FocusesTheSearchBoxAndComesBack()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Next[1] = 10;
        var (page, view, window) = await ShownTitle(1);
        var fixMatch = view.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.Command, page.FixMatchCommand));
        fixMatch.Focus();

        page.FixMatchCommand.Execute(null);
        Settle(window);
        var box = Assert.IsType<TextBox>(Focused(window));
        Assert.Equal("Input", box.Name);

        page.Picker!.CancelCommand.Execute(null);
        Settle(window);

        Assert.Same(fixMatch, Focused(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Grid_ANewTitleLands_KeepsTheFocusedCard()
    {
        for (var i = 0; i < 300; i++) _library.TitleCards.Add(FakeLibrary.Card(i, $"Film {i:0000}"));
        var grid = new TitleGridViewModel(TitleKind.Movie, _library, _hub, Posters.None(), new PageActions(_ => { }, _ => { }, _ => { }));
        await grid.RefreshAsync();
        var view = new GridView { DataContext = grid };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Settle(window);
        var card = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("card"));
        card.Focus();

        _library.TitleCards.Add(FakeLibrary.Card(9999, "Brand new"));
        await grid.RefreshAsync();
        Settle(window);

        Assert.Equal(301, grid.Items.Count);
        Assert.Same(card, Focused(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Review_SettlingARow_MovesTheFocusToTheRowThatTookItsPlace()
    {
        for (var i = 1; i <= 6; i++) _library.Review.Add(new ReviewItem(i, $"F{i}.mkv", $"F{i}.mkv", $"F{i}", null, Core.Parsing.MediaKind.Movie, null, [], false, []));
        var page = new ReviewViewModel(_t.Services());
        await page.RefreshAsync();
        var view = new ReviewView { DataContext = page };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Settle(window);
        var row = page.Items.OfType<ReviewRowViewModel>().ElementAt(2);
        var button = view.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.Command, row.NotMediaCommand));
        button.Focus();

        row.NotMediaCommand.Execute(null);
        await Ui.WaitUntil(() => page.Items.Count == 5);
        Settle(window);

        var now = Focused(window);
        Assert.NotNull(now);
        Assert.True(((InputElement)now!).IsEffectivelyVisible);
        window.Close();
    }

    private static void PressEscape(Window window)
    {
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(window);
    }

    [AvaloniaFact]
    public async Task Grid_Escape_ClearsTheFilterText()
    {
        for (var i = 0; i < 50; i++) _library.TitleCards.Add(FakeLibrary.Card(i, $"Film {i:0000}"));
        var grid = new TitleGridViewModel(TitleKind.Movie, _library, _hub, Posters.None(), new PageActions(_ => { }, _ => { }, _ => { }));
        await grid.RefreshAsync();
        var view = new GridView { DataContext = grid };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Settle(window);
        var box = view.FindControl<TextBox>("FilterBox")!;
        box.Focus();
        box.Text = "Film 0003";
        Settle(window);
        Assert.Equal("Film 0003", grid.FilterText);

        PressEscape(window);

        Assert.Equal("", grid.FilterText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FixMatch_Escape_ClearsTheTextThenClosesTheSearch()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Next[1] = 10;
        var (page, _, window) = await ShownTitle(1);
        page.FixMatchCommand.Execute(null);
        Settle(window);
        var picker = page.Picker!;
        Assert.NotEqual("", picker.Query);

        PressEscape(window);
        Assert.Equal("", picker.Query);
        Assert.NotNull(page.Picker);

        PressEscape(window);
        Assert.Null(page.Picker);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Grid_ComingBack_LandsWhereTheViewerLeft()
    {
        for (var i = 0; i < 600; i++) _library.TitleCards.Add(FakeLibrary.Card(i, $"Film {i:0000}"));
        var grid = new TitleGridViewModel(TitleKind.Movie, _library, _hub, Posters.None(), new PageActions(_ => { }, _ => { }, _ => { }));
        await grid.RefreshAsync();
        var first = new Window { Width = 1000, Height = 700, Content = new GridView { DataContext = grid } };
        first.Show();
        Settle(first);
        var scroller = first.GetVisualDescendants().OfType<ItemsRepeater>().Single().FindAncestorOfType<ScrollViewer>()!;
        scroller.Offset = new Vector(0, 3000);
        Settle(first);
        Assert.True(Math.Abs(grid.ScrollOffset - 3000) < 1, $"saved {grid.ScrollOffset}, scroller at {scroller.Offset.Y}, extent {scroller.Extent.Height}");
        first.Close(); // the shell builds a new view when the page comes back

        var second = new Window { Width = 1000, Height = 700, Content = new GridView { DataContext = grid } };
        second.Show();
        Settle(second);

        var restored = second.GetVisualDescendants().OfType<ItemsRepeater>().Single().FindAncestorOfType<ScrollViewer>()!;
        Assert.True(Math.Abs(restored.Offset.Y - 3000) < 1, $"restored to {restored.Offset.Y}, saved {grid.ScrollOffset}");
        second.Close();
    }

    [AvaloniaFact]
    public void DeleteDialog_ThousandPaths_BuildsOnlyTheRowsInView()
    {
        var files = Enumerable.Range(0, 1000).Select(i => new DeleteFile(i, $@"D:\Shows\Long runner\Long.Runner.S01E{i:000}.mkv", 1_000_000)).ToList();
        var dialog = new ConfirmDeleteDialog(new DeletePreview(1, "Long runner", files));
        dialog.Show();
        Settle(dialog);

        Assert.InRange(dialog.GetVisualDescendants().OfType<SelectableTextBlock>().Count(), 1, 60);
        dialog.Close();
    }

    [AvaloniaFact]
    public void MainWindow_PageSwap_PutsTheFocusOnTheNewPage()
    {
        using var stack = new ShellStack();
        var vm = stack.Services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Settle(window);
        vm.NavigateCommand.Execute(MainWindowViewModel.SettingsPage);
        Settle(window);
        var save = window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Save");
        save.Focus(); // a control of the page about to be replaced
        Assert.Same(save, Focused(window));

        vm.OpenOnlineTitle(603, TitleKind.Movie);
        Settle(window);

        var now = Assert.IsAssignableFrom<InputElement>(Focused(window));
        Assert.True(now.IsEffectivelyVisible);
        Assert.NotNull(now.FindAncestorOfType<TitleView>());
        window.Close();
    }
}
