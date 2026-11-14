using Avalonia;

namespace CozyCouch.Desktop.Controls;

/// <summary>The sizes the graph is laid out with. A library show uses the compact set, so its graph leaves room for the list under it.</summary>
public sealed record GraphMetrics(
    double CellWidth, double CellHeight, double Gap, double RailWidth, double HeaderHeight, double BandGap, int MaxRows, double CellFontSize)
{
    public static GraphMetrics Regular { get; } = new(44, 30, 4, 34, 24, 16, 60, 15);

    public static GraphMetrics Compact { get; } = new(38, 24, 3, 30, 22, 12, 30, 12);

    public double Pitch => CellHeight + Gap;
}

/// <summary>
/// Where the cells of the episode graph go. Each season is a column of cells, one per episode. A season longer than
/// <see cref="GraphMetrics.MaxRows"/> continues in more columns, and columns that don't fit the width wrap into a new band
/// below, so a show with a thousand episodes is a few screens of cells and never a scrolling strip. A column that doesn't
/// start at the same episode as the one before it carries its own episode labels. Nothing here knows about drawing, so
/// it is tested without a window.
/// </summary>
public sealed class GraphLayout
{
    /// <param name="Season">Index into the seasons the layout was computed for.</param>
    /// <param name="Start">The first episode (by position) in this column.</param>
    /// <param name="CellsX">Left edge of the cells.</param>
    /// <param name="Top">Top edge of the first cell.</param>
    /// <param name="Rail">Episode labels sit left of the cells.</param>
    public readonly record struct Column(int Season, int Start, int Count, double CellsX, double Top, bool Rail);

    public static GraphLayout Empty { get; } = new([], default, GraphMetrics.Regular);

    private GraphLayout(IReadOnlyList<Column> columns, Size size, GraphMetrics metrics)
    {
        Columns = columns;
        Size = size;
        Metrics = metrics;
    }

    public IReadOnlyList<Column> Columns { get; }

    public Size Size { get; }

    public GraphMetrics Metrics { get; }

    /// <param name="episodeCounts">Episodes per season, in order.</param>
    /// <param name="width">The width to fill before wrapping; infinity for one band.</param>
    /// <param name="labels">False leaves the episode labels out, for cells that already carry their episode number.</param>
    public static GraphLayout Compute(IReadOnlyList<int> episodeCounts, double width, GraphMetrics? metrics = null, bool labels = true)
    {
        var m = metrics ?? GraphMetrics.Regular;
        var columns = new List<Column>();
        double x = 0, bandTop = 0, right = 0;
        var bandRows = 0;
        var bandStart = -1; // the Start of the column before this one in the band; -1 at the start of a band

        for (var season = 0; season < episodeCounts.Count; season++)
        {
            for (var start = 0; start < episodeCounts[season]; start += m.MaxRows)
            {
                var count = Math.Min(m.MaxRows, episodeCounts[season] - start);
                var rail = labels && bandStart != start;
                var span = (rail ? m.RailWidth + m.Gap : 0) + m.CellWidth;
                if (bandStart >= 0 && x + span > width)
                {
                    bandTop += m.HeaderHeight + bandRows * m.Pitch - m.Gap + m.BandGap;
                    x = 0;
                    bandRows = 0;
                    rail = labels;
                    span = (rail ? m.RailWidth + m.Gap : 0) + m.CellWidth;
                }

                columns.Add(new Column(season, start, count, x + (rail ? m.RailWidth + m.Gap : 0), bandTop + m.HeaderHeight, rail));
                x += span + m.Gap;
                right = Math.Max(right, x - m.Gap);
                bandRows = Math.Max(bandRows, count);
                bandStart = start;
            }
        }

        var height = columns.Count == 0 ? 0 : bandTop + m.HeaderHeight + bandRows * m.Pitch - m.Gap;
        return new GraphLayout(columns, new Size(right, height), m);
    }

    public Rect CellRect(Column column, int row) =>
        new(column.CellsX, column.Top + row * Metrics.Pitch, Metrics.CellWidth, Metrics.CellHeight);

    /// <summary>The season whose heading is under a point, or null.</summary>
    public int? HitTestHeader(Point point)
    {
        foreach (var column in Columns)
        {
            var dx = point.X - column.CellsX;
            var dy = point.Y - (column.Top - Metrics.HeaderHeight);
            if (dx >= 0 && dx < Metrics.CellWidth && dy >= 0 && dy < Metrics.HeaderHeight) return column.Season;
        }

        return null;
    }

    /// <summary>The cell under a point: its season and its position in that season. Null on a gap, a label or empty space.</summary>
    public (int Season, int Episode)? HitTest(Point point)
    {
        foreach (var column in Columns)
        {
            var dx = point.X - column.CellsX;
            var dy = point.Y - column.Top;
            if (dx < 0 || dx >= Metrics.CellWidth || dy < 0) continue;

            var row = (int)(dy / Metrics.Pitch);
            if (row >= column.Count || dy - row * Metrics.Pitch >= Metrics.CellHeight) continue;

            return (column.Season, column.Start + row);
        }

        return null;
    }
}
