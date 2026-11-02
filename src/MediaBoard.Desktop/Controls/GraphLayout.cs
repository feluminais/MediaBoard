using Avalonia;

namespace MediaBoard.Desktop.Controls;

/// <summary>
/// Where the cells of the episode graph go. Each season is a column of cells, one per episode. A season longer than
/// <see cref="MaxRows"/> continues in more columns, and columns that don't fit the width wrap into a new band below,
/// so a show with a thousand episodes is a few screens of cells and never a scrolling strip. A column that doesn't
/// start at the same episode as the one before it carries its own episode labels. Nothing here knows about drawing, so
/// it is tested without a window.
/// </summary>
public sealed class GraphLayout
{
    public const double CellWidth = 44;
    public const double CellHeight = 30;
    public const double Gap = 4;
    public const double RailWidth = 34;
    public const double HeaderHeight = 24;
    public const double BandGap = 16;
    public const double Pitch = CellHeight + Gap;
    public const int MaxRows = 60;

    /// <param name="Season">Index into the seasons the layout was computed for.</param>
    /// <param name="Start">The first episode (by position) in this column.</param>
    /// <param name="CellsX">Left edge of the cells.</param>
    /// <param name="Top">Top edge of the first cell.</param>
    /// <param name="Rail">Episode labels sit left of the cells.</param>
    public readonly record struct Column(int Season, int Start, int Count, double CellsX, double Top, bool Rail);

    public static GraphLayout Empty { get; } = new([], default);

    private GraphLayout(IReadOnlyList<Column> columns, Size size)
    {
        Columns = columns;
        Size = size;
    }

    public IReadOnlyList<Column> Columns { get; }

    public Size Size { get; }

    /// <param name="episodeCounts">Episodes per season, in order.</param>
    /// <param name="width">The width to fill before wrapping; infinity for one band.</param>
    public static GraphLayout Compute(IReadOnlyList<int> episodeCounts, double width)
    {
        var columns = new List<Column>();
        double x = 0, bandTop = 0, right = 0;
        var bandRows = 0;
        var bandStart = -1; // the Start of the column before this one in the band; -1 at the start of a band

        for (var season = 0; season < episodeCounts.Count; season++)
        {
            for (var start = 0; start < episodeCounts[season]; start += MaxRows)
            {
                var count = Math.Min(MaxRows, episodeCounts[season] - start);
                var rail = bandStart != start;
                var span = (rail ? RailWidth + Gap : 0) + CellWidth;
                if (bandStart >= 0 && x + span > width)
                {
                    bandTop += HeaderHeight + bandRows * Pitch - Gap + BandGap;
                    x = 0;
                    bandRows = 0;
                    rail = true;
                    span = RailWidth + Gap + CellWidth;
                }

                columns.Add(new Column(season, start, count, x + (rail ? RailWidth + Gap : 0), bandTop + HeaderHeight, rail));
                x += span + Gap;
                right = Math.Max(right, x - Gap);
                bandRows = Math.Max(bandRows, count);
                bandStart = start;
            }
        }

        var height = columns.Count == 0 ? 0 : bandTop + HeaderHeight + bandRows * Pitch - Gap;
        return new GraphLayout(columns, new Size(right, height));
    }

    public Rect CellRect(Column column, int row) =>
        new(column.CellsX, column.Top + row * Pitch, CellWidth, CellHeight);

    /// <summary>The cell under a point: its season and its position in that season. Null on a gap, a label or empty space.</summary>
    public (int Season, int Episode)? HitTest(Point point)
    {
        foreach (var column in Columns)
        {
            var dx = point.X - column.CellsX;
            var dy = point.Y - column.Top;
            if (dx < 0 || dx >= CellWidth || dy < 0) continue;

            var row = (int)(dy / Pitch);
            if (row >= column.Count || dy - row * Pitch >= CellHeight) continue;

            return (column.Season, column.Start + row);
        }

        return null;
    }
}
