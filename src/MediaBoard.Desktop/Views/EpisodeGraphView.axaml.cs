using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using MediaBoard.Desktop.Controls;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Views;

public sealed record LegendEntry(string Name, IBrush Fill);

public partial class EpisodeGraphView : UserControl
{
    private const double CardWidth = 380;
    private const double CardOffset = 8;

    public static IReadOnlyList<LegendEntry> Legend { get; } =
        RatingBands.Legend.Select(b => new LegendEntry(RatingBands.Name(b), RatingBands.Fill(b))).ToList();

    private ScrollViewer? _scroller;

    public EpisodeGraphView()
    {
        InitializeComponent();
        Graph.HoverChanged += OnHoverChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null) _scroller.ScrollChanged += OnScrolled;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroller is not null) _scroller.ScrollChanged -= OnScrolled;
        _scroller = null;
        Graph.Hover(null);
    }

    // The cells moved under a pointer that did not: the card would point at the wrong one, so it goes until the pointer moves.
    private void OnScrolled(object? sender, ScrollChangedEventArgs e) => Graph.Hover(null);

    private void OnHoverChanged(object? sender, GraphHoverEventArgs e)
    {
        if (DataContext is not EpisodeGraphViewModel vm) return;

        vm.Card.Show(e.Episode);
        if (e.Episode is not null) Place(e.Cell);
    }

    // Below the cell; above it when below would run off the visible part of the page; kept inside the graph's width.
    private void Place(Rect cell)
    {
        CardBox.Measure(new Size(CardWidth, double.PositiveInfinity));
        var height = CardBox.DesiredSize.Height;
        var x = Math.Clamp(cell.X, 0, Math.Max(0, Host.Bounds.Width - CardWidth));

        var below = cell.Bottom + CardOffset;
        var above = cell.Y - CardOffset - height;
        double top = 0, bottom = double.PositiveInfinity;
        if (_scroller is not null && Graph.TranslatePoint(default, _scroller) is { } origin)
        {
            top = -origin.Y;
            bottom = top + _scroller.Viewport.Height;
        }

        var y = below + height <= bottom ? below
            : above >= top ? above
            : Math.Max(top, bottom - height);
        CardBox.Margin = new Thickness(x, Math.Max(0, y), 0, 0);
    }
}
