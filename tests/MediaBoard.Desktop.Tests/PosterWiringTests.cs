using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Desktop.Controls;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;

namespace MediaBoard.Desktop.Tests;

/// <summary>The wiring that keeps posters cheap: what a scrolled-away card, a left page and a busy loader do.</summary>
public class PosterWiringTests
{
    // 5 columns x 3 rows are in view at 1000x700; the repeater builds a row or two beyond. Far below 5,000.
    private const int MaxBuilt = 40;

    private readonly ActivityHub _hub = new();
    private readonly FakeLibrary _library = new();

    // A source that never answers until it is cancelled, and records what it was asked.
    private sealed class HungSource
    {
        private readonly List<(string Path, CancellationToken Token)> _asked = [];

        public IReadOnlyList<(string Path, CancellationToken Token)> Asked
        {
            get
            {
                lock (_asked) return _asked.ToList();
            }
        }

        public int Live => Asked.Count(a => !a.Token.IsCancellationRequested);

        public PosterLoader Loader(int maxConcurrent = 6) => new(async (path, _, ct) =>
        {
            lock (_asked) _asked.Add((path, ct));
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return null;
        }, maxConcurrent);
    }

    private static void Settle(Window window)
    {
        Ui.Flush();
        window.UpdateLayout();
        Ui.Flush();
        window.UpdateLayout();
    }

    private async Task<(TitleGridViewModel Grid, GridView View, Window Window, ScrollViewer Scroller)> ShownGrid(PosterLoader loader, int count = 5000)
    {
        for (var i = 0; i < count; i++) _library.TitleCards.Add(FakeLibrary.Card(i, $"Film {i:0000}", poster: $"/p{i}.jpg"));
        var grid = new TitleGridViewModel(TitleKind.Movie, _library, _hub, loader, new PageActions(_ => { }, _ => { }, _ => { }));
        await grid.RefreshAsync();
        var view = new GridView { DataContext = grid };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Settle(window);
        return (grid, view, window, view.GetVisualDescendants().OfType<ItemsRepeater>().Single().FindAncestorOfType<ScrollViewer>()!);
    }

    [AvaloniaFact]
    public async Task Grid_ScrollingAway_CancelsTheRequestsOfCardsThatLeft()
    {
        var source = new HungSource();
        var (grid, view, window, scroller) = await ShownGrid(source.Loader());
        var firstScreen = grid.Items.Take(40).ToList();
        Assert.Contains(firstScreen, c => c.IsLookingForPoster); // the visible ones asked
        Assert.InRange(grid.Items.Count(c => c.IsLookingForPoster), 1, MaxBuilt);

        // Several screens down, then deep into the list.
        foreach (var offset in new[] { 600.0, 1500, 3000, 12000, 40000 })
        {
            scroller.Offset = new Vector(0, offset);
            Settle(window);
        }

        Assert.All(firstScreen, c => Assert.False(c.IsLookingForPoster)); // gone with their screen
        Assert.InRange(grid.Items.Count(c => c.IsLookingForPoster), 0, MaxBuilt);
        // Everything asked for while scrolling is cancelled except what is on screen now.
        Assert.InRange(source.Live, 0, 6);

        // And back to the top: the first cards ask again, nothing else piles up.
        scroller.Offset = new Vector(0, 0);
        Settle(window);
        Assert.Contains(firstScreen, c => c.IsLookingForPoster);
        Assert.InRange(grid.Items.Count(c => c.IsLookingForPoster), 1, MaxBuilt);
    }

    [AvaloniaFact]
    public async Task Grid_LongScroll_KeepsTheRequestCountBounded()
    {
        var source = new HungSource();
        var (grid, view, window, scroller) = await ShownGrid(source.Loader());
        var peak = 0;

        for (var step = 1; step <= 60; step++)
        {
            scroller.Offset = new Vector(0, step * 700.0);
            Settle(window);
            peak = Math.Max(peak, grid.Items.Count(c => c.IsLookingForPoster));
        }

        Assert.InRange(peak, 1, MaxBuilt);
        Assert.InRange(grid.Items.Count(c => c.IsLookingForPoster), 0, MaxBuilt);
    }

    [AvaloniaFact]
    public async Task Grid_ScrolledAwayCards_LetGoOfTheirBitmaps()
    {
        // Real files, so the cards hold real leases while they are on screen.
        var dir = Directory.CreateTempSubdirectory("mediaboard-scroll-").FullName;
        try
        {
            var files = Enumerable.Range(0, 400).Select(i => Posters.WritePng(dir, $"p{i}.png", 40, 60)).ToArray();
            var loader = new PosterLoader((path, _, _) => Task.FromResult<string?>(files[int.Parse(path[2..^4])]));
            var (grid, view, window, scroller) = await ShownGrid(loader, 400);
            await Ui.WaitUntil(() => view.GetVisualDescendants().OfType<PosterImage>().Any(i => i.Image is not null));
            var first = view.GetVisualDescendants().OfType<PosterImage>().Where(i => i.Image is not null).Select(i => i.Image!).ToList();
            Assert.NotEmpty(first);

            scroller.Offset = new Vector(0, 100000);
            Settle(window);
            await Ui.WaitUntil(() => view.GetVisualDescendants().OfType<PosterImage>().Any(i => i.Image is not null && !first.Contains(i.Image)));

            // No control still shows a bitmap of the first screen: the cache owns them, the controls don't.
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<PosterImage>(), i => i.Image is not null && first.Contains(i.Image));
            Assert.InRange(grid.Items.Count(c => c.IsLookingForPoster), 0, MaxBuilt);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void PosterHost_AsksWhenAttached_AndCancelsWhenDetached()
    {
        var source = new HungSource();
        var card = PosterCardViewModel.For(FakeLibrary.Card(1, "Coco", poster: "/c.jpg"), source.Loader(), _ => { });
        var view = new PosterCard { DataContext = card };
        Assert.False(card.IsLookingForPoster); // nothing before it is shown

        var window = new Window { Content = view };
        window.Show();
        Assert.True(card.IsLookingForPoster);

        window.Content = null; // taken out of the tree
        Ui.Flush();
        Assert.False(card.IsLookingForPoster);
    }

    [AvaloniaFact]
    public async Task PosterHost_NewCard_CancelsTheOldOnesRequest()
    {
        var source = new HungSource();
        var first = PosterCardViewModel.For(FakeLibrary.Card(1, "Coco", poster: "/c.jpg"), source.Loader(), _ => { });
        var second = PosterCardViewModel.For(FakeLibrary.Card(2, "Heat", poster: "/h.jpg"), source.Loader(), _ => { });
        var view = new PosterCard { DataContext = first };
        new Window { Content = view }.Show();
        Assert.True(first.IsLookingForPoster);

        view.DataContext = second; // a recycled view is given another card
        await Task.Delay(20);

        Assert.False(first.IsLookingForPoster);
        Assert.True(second.IsLookingForPoster);
    }

    [AvaloniaFact]
    public async Task PosterRow_AsksAndCancelsLikeACard()
    {
        var source = new HungSource();
        var card = PosterCardViewModel.For(FakeLibrary.Card(1, "Coco", poster: "/c.jpg"), source.Loader(), _ => { });
        var window = new Window { Content = new PosterRow { DataContext = card } };
        window.Show();
        Assert.True(card.IsLookingForPoster);

        window.Content = null;
        Ui.Flush();
        await Task.Delay(20);

        Assert.False(card.IsLookingForPoster);
    }

    [AvaloniaFact]
    public async Task Loader_RunsAtMostSixLookupsAtOnce()
    {
        var running = 0;
        var peak = 0;
        var gate = new TaskCompletionSource();
        var loader = new PosterLoader(async (_, _, _) =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref running));
            await gate.Task;
            Interlocked.Decrement(ref running);
            return "x";
        });

        var all = Enumerable.Range(0, 20).Select(i => loader.LoadAsync($"/{i}.jpg", CancellationToken.None)).ToList();
        await Task.Delay(100);
        Assert.Equal(6, Volatile.Read(ref running));

        gate.SetResult();
        await Task.WhenAll(all);
        Assert.Equal(6, peak);
    }

    [AvaloniaFact]
    public async Task Loader_CancelWhileQueued_NeverAsksAndFreesTheSlot()
    {
        var asked = new List<string>();
        var release = new TaskCompletionSource();
        var loader = new PosterLoader(async (path, _, _) =>
        {
            lock (asked) asked.Add(path);
            await release.Task;
            return path;
        }, maxConcurrent: 1);
        var first = loader.LoadAsync("/first.jpg", CancellationToken.None);
        await Task.Delay(50);
        using var cts = new CancellationTokenSource();
        var queued = loader.LoadAsync("/queued.jpg", cts.Token);
        var after = loader.LoadAsync("/after.jpg", CancellationToken.None);
        await Task.Delay(50);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.SetResult();

        Assert.Equal("/first.jpg", await first);
        Assert.Equal("/after.jpg", await after); // the cancelled one gave way instead of holding the slot
        Assert.Equal(["/first.jpg", "/after.jpg"], asked);
    }

    [AvaloniaFact]
    public async Task Loader_CancelledWhileRunning_GivesTheSlotBack()
    {
        var loader = new PosterLoader(async (path, _, ct) =>
        {
            if (path == "/hung.jpg") await Task.Delay(Timeout.Infinite, ct);
            return path;
        }, maxConcurrent: 1);
        using var cts = new CancellationTokenSource();
        var hung = loader.LoadAsync("/hung.jpg", cts.Token);
        await Task.Delay(50);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hung);

        Assert.Equal("/next.jpg", await loader.LoadAsync("/next.jpg", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public async Task List_OfFiveThousandRows_BuildsOnlyTheVisibleOnes()
    {
        for (var i = 0; i < 5000; i++) _library.Recent.Add(FakeLibrary.Card(i, $"Film {i:0000}", poster: $"/p{i}.jpg"));
        var source = new HungSource();
        var list = new ListViewModel(ListKind.RecentlyAdded, _library, _hub, source.Loader(), new PageActions(_ => { }, _ => { }, _ => { }));
        await list.RefreshAsync();
        var view = new ListView { DataContext = list };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Settle(window);

        var rows = view.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.IsType<VirtualizingStackPanel>(rows.ItemsPanelRoot);
        Assert.Equal(5000, list.Items.Count);
        Assert.InRange(view.GetVisualDescendants().OfType<PosterRow>().Count(), 1, 60);
        Assert.InRange(list.Items.Count(c => c.IsLookingForPoster), 1, 60);

        rows.Scroll!.Offset = new Vector(0, 200000);
        Settle(window);

        Assert.InRange(view.GetVisualDescendants().OfType<PosterRow>().Count(), 1, 60);
        Assert.InRange(list.Items.Count(c => c.IsLookingForPoster), 0, 60);
        Assert.All(list.Items.Take(50), c => Assert.False(c.IsLookingForPoster));
    }

    [Fact]
    public void SharedBitmapCache_IsBuiltWithTheDocumentedCapacity() =>
        Assert.Equal(300, PosterImage.SharedCacheCapacity);
}
