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

/// <summary>A press on a cell (season and episode number) or on a season's heading (episode null).</summary>
public sealed class GraphChosenEventArgs(int season, int? episode) : EventArgs
{
    public int Season { get; } = season;

    public int? Episode { get; } = episode;
}

/// <summary>
/// The episode graph: a column of cells for each season (see <see cref="GraphLayout"/>), coloured by rating or by how far
/// the viewer got (<see cref="Mode"/>). One control draws every cell, so a show with a thousand episodes costs a thousand
/// rectangles and not a thousand elements. The text of a cell is cached by its string, so a repaint formats nothing new.
/// It is not focusable and acts on the pointer only: <see cref="HoverChanged"/> tells the page which episode to describe,
/// and <see cref="Chosen"/> which season or episode was pressed.
/// </summary>
public sealed class EpisodeGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<GraphSeason>?> SeasonsProperty =
        AvaloniaProperty.Register<EpisodeGraph, IReadOnlyList<GraphSeason>?>(nameof(Seasons));

    public static readonly StyledProperty<GraphMode> ModeProperty =
        AvaloniaProperty.Register<EpisodeGraph, GraphMode>(nameof(Mode), GraphMode.Ratings);

    /// <summary>The season the page lists below the graph; its heading is marked.</summary>
    public static readonly StyledProperty<int?> SelectedSeasonProperty =
        AvaloniaProperty.Register<EpisodeGraph, int?>(nameof(SelectedSeason));

    /// <summary>Cells and headings can be pressed: the pointer becomes a hand over them.</summary>
    public static readonly StyledProperty<bool> IsChoosableProperty =
        AvaloniaProperty.Register<EpisodeGraph, bool>(nameof(IsChoosable));

    /// <summary>Smaller cells, so the graph of a library show leaves room for the list under it.</summary>
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<EpisodeGraph, bool>(nameof(IsCompact));

    private enum Ink { White, Black, Normal, OnAccent, Muted, Warning }

    private static readonly Typeface CellFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface LabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
    private static readonly Cursor Hand = new(StandardCursorType.Hand);

    private readonly Dictionary<(string Text, Ink Ink), FormattedText> _cellText = [];
    private readonly Dictionary<(string Text, bool Selected), FormattedText> _labels = [];
    private GraphLayout _layout = GraphLayout.Empty;
    private double _layoutWidth = double.NaN;
    private (int Season, int Episode)? _hover;
    private IBrush _labelBrush = Brushes.Gray;
    private IBrush _outlineBrush = Brushes.White;
    private IBrush _accent = Brushes.Teal;
    private IBrush _onAccent = Brushes.White;
    private IBrush _surface = Brushes.Gray;
    private IBrush _divider = Brushes.Gray;
    private IBrush _warningSurface = Brushes.Gold;
    private IBrush _warningText = Brushes.Black;
    private IBrush _text = Brushes.Black;
    private IBrush _surfaceAccent = Brushes.Teal;
    private Pen _dashed = new(Brushes.Gray, 1, DashStyle.Dash);

    static EpisodeGraph()
    {
        AffectsMeasure<EpisodeGraph>(SeasonsProperty, IsCompactProperty, ModeProperty);
        AffectsRender<EpisodeGraph>(SeasonsProperty, ModeProperty, SelectedSeasonProperty);
        ModeProperty.Changed.AddClassHandler<EpisodeGraph>((graph, _) => graph.OnModeChanged());
        SeasonsProperty.Changed.AddClassHandler<EpisodeGraph>((graph, _) => graph.OnSeasonsChanged());
        IsCompactProperty.Changed.AddClassHandler<EpisodeGraph>((graph, _) => graph.OnSizesChanged());
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

    public GraphMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public int? SelectedSeason
    {
        get => GetValue(SelectedSeasonProperty);
        set => SetValue(SelectedSeasonProperty, value);
    }

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public bool IsChoosable
    {
        get => GetValue(IsChoosableProperty);
        set => SetValue(IsChoosableProperty, value);
    }

    /// <summary>The pointer moved onto another episode, or off the cells.</summary>
    public event EventHandler<GraphHoverEventArgs>? HoverChanged;

    /// <summary>A cell or a season heading was pressed.</summary>
    public event EventHandler<GraphChosenEventArgs>? Chosen;

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
        _layout = GraphLayout.Compute(seasons.Select(s => s.Episodes.Count).ToList(), width, IsCompact ? GraphMetrics.Compact : GraphMetrics.Regular,
            labels: Mode != GraphMode.Progress); // a progress cell already shows its episode number
        return true;
    }

    private void OnSizesChanged()
    {
        _layoutWidth = double.NaN;
        _cellText.Clear();
        _hover = null;
    }

    // The labels come and go with the mode, which moves the columns.
    private void OnModeChanged()
    {
        _layoutWidth = double.NaN;
        _hover = null;
        HoverChanged?.Invoke(this, new GraphHoverEventArgs(null, default));
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

        var progress = Mode == GraphMode.Progress;
        foreach (var column in _layout.Columns)
        {
            var season = seasons[column.Season];
            var selected = SelectedSeason == season.Number;
            var m = _layout.Metrics;
            var headerTop = column.Top - m.HeaderHeight;
            DrawText(context, Label($"S{season.Number}", selected), column.CellsX, headerTop, m.CellWidth, m.HeaderHeight - 2);
            if (selected) context.DrawRectangle(_accent, null, new Rect(column.CellsX + 6, column.Top - 4, m.CellWidth - 12, 2), 1, 1);

            for (var row = 0; row < column.Count; row++)
            {
                var episode = season.Episodes[column.Start + row];
                var rect = _layout.CellRect(column, row);
                if (progress) DrawProgressCell(context, episode, rect);
                else DrawRatingCell(context, episode, rect);

                if (column.Rail)
                {
                    DrawText(context, Label($"E{column.Start + row + 1}", false), column.CellsX - m.RailWidth - m.Gap, rect.Y,
                        m.RailWidth, rect.Height);
                }
            }
        }

        if (_hover is { } h)
        {
            var column = ColumnOf(h);
            var rect = _layout.CellRect(column, h.Episode - column.Start).Inflate(1.5);
            context.DrawRectangle(null, new Pen(_outlineBrush, 2), rect, 6, 6);
        }
    }

    private void DrawRatingCell(DrawingContext context, GraphEpisode episode, Rect rect)
    {
        if (episode.Hidden)
        {
            using (context.PushOpacity(0.4)) DrawRatingCellCore(context, episode, rect);
        }
        else
        {
            DrawRatingCellCore(context, episode, rect);
        }
    }

    private void DrawRatingCellCore(DrawingContext context, GraphEpisode episode, Rect rect)
    {
        var band = RatingBands.Of(episode.RoundedRating);
        context.DrawRectangle(RatingBands.Fill(band), null, rect, 5, 5);
        DrawText(context, CellText(episode.RatingText, RatingBands.DarkText(band) ? Ink.Black : Ink.White), rect.X, rect.Y, rect.Width, rect.Height);
    }

    // Watched is the accent; started is part accent, in proportion; not watched is a plain tile; no file is a dashed outline.
    private void DrawProgressCell(DrawingContext context, GraphEpisode episode, Rect rect)
    {
        if (episode.Hidden)
        {
            using (context.PushOpacity(0.4)) DrawProgressCellCore(context, episode, rect);
        }
        else
        {
            DrawProgressCellCore(context, episode, rect);
        }
    }

    private void DrawProgressCellCore(DrawingContext context, GraphEpisode episode, Rect rect)
    {
        var text = episode.Number.ToString(CultureInfo.InvariantCulture);
        var inner = rect.Deflate(0.5);
        switch (episode.State)
        {
            case EpisodeState.Watched:
                context.DrawRectangle(_accent, null, rect, 5, 5);
                DrawText(context, CellText(text, Ink.OnAccent), rect.X, rect.Y, rect.Width, rect.Height);
                break;
            case EpisodeState.Started:
                context.DrawRectangle(_surface, new Pen(_divider, 1), inner, 5, 5);
                using (context.PushClip(new Rect(rect.X, rect.Y, rect.Width * Math.Clamp(episode.Progress, 0.08, 1), rect.Height)))
                    context.DrawRectangle(_surfaceAccent, null, rect, 5, 5);
                DrawText(context, CellText(text, Ink.Normal), rect.X, rect.Y, rect.Width, rect.Height);
                break;
            case EpisodeState.NoFile:
                context.DrawRectangle(null, _dashed, inner, 5, 5);
                DrawText(context, CellText(text, Ink.Muted), rect.X, rect.Y, rect.Width, rect.Height);
                break;
            case EpisodeState.Missing:
                context.DrawRectangle(_warningSurface, null, rect, 5, 5);
                DrawText(context, CellText(text, Ink.Warning), rect.X, rect.Y, rect.Width, rect.Height);
                break;
            default:
                context.DrawRectangle(_surface, new Pen(_divider, 1), inner, 5, 5);
                DrawText(context, CellText(text, Ink.Normal), rect.X, rect.Y, rect.Width, rect.Height);
                break;
        }
    }

    private GraphLayout.Column ColumnOf((int Season, int Episode) cell) =>
        _layout.Columns.First(c => c.Season == cell.Season && cell.Episode >= c.Start && cell.Episode < c.Start + c.Count);

    private static void DrawText(DrawingContext context, FormattedText text, double x, double y, double width, double height) =>
        context.DrawText(text, new Point(x + (width - text.Width) / 2, y + (height - text.Height) / 2));

    private FormattedText CellText(string text, Ink ink)
    {
        if (_cellText.TryGetValue((text, ink), out var cached)) return cached;

        var brush = ink switch
        {
            Ink.White => Brushes.White,
            Ink.Black => Brushes.Black,
            Ink.OnAccent => _onAccent,
            Ink.Muted => _labelBrush,
            Ink.Warning => _warningText,
            _ => _text,
        };
        return _cellText[(text, ink)] = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, CellFace, _layout.Metrics.CellFontSize, brush);
    }

    private FormattedText Label(string text, bool selected)
    {
        if (_labels.TryGetValue((text, selected), out var cached)) return cached;

        return _labels[(text, selected)] = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            selected ? CellFace : LabelFace, 12, selected ? _accent : _labelBrush);
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
        _text = _outlineBrush;
        _accent = FindBrush("AccentBrush") ?? Brushes.Teal;
        _onAccent = FindBrush("OnAccentBrush") ?? Brushes.White;
        _surface = FindBrush("PanelBrush") ?? Brushes.Gray;
        _divider = FindBrush("DividerBrush") ?? Brushes.Gray;
        _warningSurface = FindBrush("WarningSurfaceBrush") ?? Brushes.Gold;
        _warningText = FindBrush("WarningTextBrush") ?? Brushes.Black;
        _dashed = new Pen(_divider, 1, DashStyle.Dash);
        _surfaceAccent = _accent is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.45) : _accent;
    }

    private IBrush? FindBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var cell = _layout.HitTest(point);
        Hover(cell);
        Cursor = IsChoosable && (cell is not null || _layout.HitTestHeader(point) is not null) ? Hand : null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsChoosable || Seasons is not { } seasons || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var point = e.GetPosition(this);
        if (_layout.HitTest(point) is { } cell)
        {
            Chosen?.Invoke(this, new GraphChosenEventArgs(seasons[cell.Season].Number, seasons[cell.Season].Episodes[cell.Episode].Number));
            e.Handled = true;
        }
        else if (_layout.HitTestHeader(point) is { } season)
        {
            Chosen?.Invoke(this, new GraphChosenEventArgs(seasons[season].Number, null));
            e.Handled = true;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hover(null);
        Cursor = null;
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

        var column = ColumnOf(c);
        HoverChanged?.Invoke(this, new GraphHoverEventArgs(Seasons[c.Season].Episodes[c.Episode], _layout.CellRect(column, c.Episode - column.Start)));
    }
}
