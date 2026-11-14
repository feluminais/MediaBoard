namespace CozyCouch.Core.Players;

/// <summary>Comparing the file paths players report with each other.</summary>
internal static class PlayerPaths
{
    /// <summary>
    /// Whether two reported paths name the same file: slashes, <c>.</c> segments and case do not matter, as on
    /// Windows. Two missing paths are the same; a missing and a given one are not. A path that is not rooted is
    /// compared as given, since resolving it here would use this program's folder.
    /// </summary>
    public static bool Same(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.IsPathRooted(path) ? Path.GetFullPath(path) : path;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
