namespace CozyCouch.Core.Library;

public static class PaletteCommands
{
    /// <summary>The commands whose label has a word starting with <paramref name="filter"/> (case-insensitive); all of them for an empty filter.</summary>
    public static IReadOnlyList<PaletteCommand> Filter(IReadOnlyList<PaletteCommand> all, string filter)
    {
        var prefix = filter.Trim();
        if (prefix.Length == 0) return all;

        return all
            .Where(c => c.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || c.Label.Split(' ').Any(w => w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
