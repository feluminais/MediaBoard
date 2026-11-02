using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Controls;

/// <summary>The episode under the pointer, and where its cell is in the graph's own coordinates; null when the pointer is on none.</summary>
public sealed class GraphHoverEventArgs(GraphEpisode? episode, Rect cell) : EventArgs
{
    public GraphEpisode? Episode { get; } = episode;

    public Rect Cell { get; } = cell;
}

/// <summary>
/// The episode graph: a column of rating-coloured cells for each season (see <see cref="GraphLayout"/>). One control
/// draws every cell, so a show with a thousand episodes costs a thousand rectangles and not a thousand elements. The
/// text of a cell is cached by its string, so a repaint formats nothing new. It is not focusable and acts on the
/// pointer only: <see cref="HoverChanged"/> tells the page which episode to describe.
/// </summary>
public sealed class EpisodeGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<GraphSeason>?> SeasonsProperty =
        AvaloniaProperty.Register<EpisodeGraph, IReadOnlyList<GraphSeason>?>(nameof(Seasons));

    private static readonly Typeface CellFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface LabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);

    private readonly Dictionary<(string Text, bool Dark), FormattedText> _cellText = [];
    private readonly Dictionary<string, FormattedText> _labels = [];
    private GraphLayout _layout = GraphLayout.Empty;
    private double _layoutWidth = double.NaN;
    private (int Season, int Episode)? _hover;
    private IBrush _labelBrush = Brushes.Gray;
    private IBrush _outlineBrush = Brushes.White;

    static EpisodeGraph()
    {
        AffectsMeasure<EpisodeGraph>(SeasonsProperty);
        AffectsRender<EpisodeGraph>(SeasonsProperty);
        SeasonsProperty.Changed.AddClassHandler<EpisodeGraph>((graph, _) => graph.OnSeasonsChanged());
    }

    public EpisodeGraph()
    {
        ActualThemeVariantChanged += (_, _) =>
        {
            ReadBrushes();
            _cellText.Clear();
            _labels.Clear();
            InvalidateVisual();
        };
    }

    public IReadOnlyList<GraphSeason>? Seasons
    {
        get => GetValue(SeasonsProperty);
        set => SetValue(SeasonsProperty, value);
    }

    /// <summary>The pointer moved onto another episode, or off the cells.</summary>
    public event EventHandler<GraphHoverEventArgs>? HoverChanged;

    /// <summary>The cell the pointer is on, if any (for tests and the page).</summary>
    public GraphEpisode? Hovered => _hover is { } h ? Seasons?[h.Season].Episodes[h.Episode] : null;

    public GraphLayout Layout => _layout;

    protected override Size MeasureOverride(Size availableSize)
    {
        Relayout(availableSize.Width);
        return _layout.Size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The page may give a different width than it measured with; wrap to the width the control really has.
        if (Relayout(finalSize.Width)) InvalidateMeasure();
        return finalSize;
    }

    private bool Relayout(double width)
    {
        var seasons = Seasons ?? [];
        if (width.Equals(_layoutWidth)) return false;

        _layoutWidth = width;
        _layout = GraphLayout.Compute(seasons.Select(s => s.Episodes.Count).ToList(), width);
        return true;
    }

    private void OnSeasonsChanged()
    {
        _layoutWidth = double.NaN; // the counts changed, so lay out again
        _hover = null;
        HoverChanged?.Invoke(this, new GraphHoverEventArgs(null, default));
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size)); // so the gaps between cells are part of the control for the pointer
        var seasons = Seasons;
        if (seasons is null || _layout.Columns.Count == 0) return;

        foreach (var column in _layout.Columns)
        {
            var season = seasons[column.Season];
            DrawText(context, Label($"S{season.Number}"), column.CellsX, column.Top - GraphLayout.HeaderHeight, GraphLayout.CellWidth, GraphLayout.HeaderHeight);

            for (var row = 0; row < column.Count; row++)
            {
                var episode = season.Episodes[column.Start + row];
                var rect = _layout.CellRect(column, row);
                var band = RatingBands.Of(episode.RoundedRating);
                context.DrawRectangle(RatingBands.Fill(band), null, rect, 5, 5);
                DrawText(context, CellText(episode.RatingText, RatingBands.DarkText(band)), rect.X, rect.Y, rect.Width, rect.Height);

                if (column.Rail)
                {
                    DrawText(context, Label($"E{column.Start + row + 1}"), column.CellsX - GraphLayout.RailWidth - GraphLayout.Gap, rect.Y,
                        GraphLayout.RailWidth, rect.Height);
                }
            }
        }

        if (_hover is { } h && Seasons is not null)
        {
            var column = _layout.Columns.First(c => c.Season == h.Season && h.Episode >= c.Start && h.Episode < c.Start + c.Count);
            var rect = _layout.CellRect(column, h.Episode - column.Start).Inflate(1.5);
            context.DrawRectangle(null, new Pen(_outlineBrush, 2), rect, 6, 6);
        }
    }

    private static void DrawText(DrawingContext context, FormattedText text, double x, double y, double width, double height) =>
        context.DrawText(text, new Point(x + (width - text.Width) / 2, y + (height - text.Height) / 2));

    private FormattedText CellText(string text, bool dark)
    {
        if (_cellText.TryGetValue((text, dark), out var cached)) return cached;

        var brush = dark ? Brushes.Black : Brushes.White;
        return _cellText[(text, dark)] = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, CellFace, 15, brush);
    }

    private FormattedText Label(string text)
    {
        if (_labels.TryGetValue(text, out var cached)) return cached;

        return _labels[text] = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 12, _labelBrush);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ReadBrushes();
    }

    private void ReadBrushes()
    {
        _labelBrush = FindBrush("MutedTextBrush") ?? Brushes.Gray;
        _outlineBrush = FindBrush("SystemControlForegroundBaseHighBrush") ?? Brushes.White;
    }

    private IBrush? FindBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Hover(_layout.HitTest(e.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hover(null);
    }

    /// <summary>Puts the hover on a cell (or none) and tells the page, when it moved.</summary>
    public void Hover((int Season, int Episode)? cell)
    {
        if (cell == _hover) return;

        _hover = cell;
        InvalidateVisual();
        if (cell is not { } c || Seasons is null)
        {
            HoverChanged?.Invoke(this, new GraphHoverEventArgs(null, default));
            return;
        }

        var column = _layout.Columns.First(x => x.Season == c.Season && c.Episode >= x.Start && c.Episode < x.Start + x.Count);
        HoverChanged?.Invoke(this, new GraphHoverEventArgs(Seasons[c.Season].Episodes[c.Episode], _layout.CellRect(column, c.Episode - column.Start)));
    }
}
