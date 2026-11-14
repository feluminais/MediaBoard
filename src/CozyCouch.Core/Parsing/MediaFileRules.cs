namespace CozyCouch.Core.Parsing;

public static class MediaFileRules
{
    private const long SampleLimitBytes = 300L * 1024 * 1024;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".flv", ".ogm", ".divx",
    };

    // Files a torrent client is still writing.
    private static readonly HashSet<string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".parts", ".part", ".!qB", ".crdownload", ".tmp",
    };

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// True for files the scanner should skip: anything under a dot-named folder, partial downloads
    /// and small sample clips. <paramref name="relativePath"/> may use either separator.
    /// </summary>
    public static bool IsIgnored(string relativePath, long size)
    {
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return true;

        if (segments.Any(s => s.StartsWith('.')))
            return true;

        var fileName = segments[^1];
        if (PartialExtensions.Contains(Path.GetExtension(fileName)))
            return true;

        return size < SampleLimitBytes && fileName.Contains("sample", StringComparison.OrdinalIgnoreCase);
    }
}
