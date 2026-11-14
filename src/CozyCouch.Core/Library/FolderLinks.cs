using System.Collections.Concurrent;

namespace CozyCouch.Core.Library;

/// <summary>
/// Spots files that a symbolic link or junction brings into a library folder from somewhere else. Picking a folder for
/// someone promises them that folder and nothing outside it, so the sharing server leaves such files out, while the
/// owner's own library still follows links. A hard link isn't found: it is the same file at a second path, and it sits
/// in the folder in its own right. One instance remembers each directory it read, so a whole library costs one read per
/// directory.
/// </summary>
internal sealed class FolderLinks
{
    private static readonly EnumerationOptions EveryEntry = new() { AttributesToSkip = FileAttributes.None, IgnoreInaccessible = true };

    private readonly ConcurrentDictionary<string, HashSet<string>> _linksIn = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="folder">The library folder. It may be a link itself: that's the place its owner chose.</param>
    public bool IsThroughLink(string folder, string relPath)
    {
        var path = folder;
        var parts = relPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (_linksIn.GetOrAdd(path, LinksIn).Contains(part)) return true;
            path = Path.Combine(path, part);
        }

        return false;
    }

    // LinkTarget tells a link from the other reparse points, such as a cloud placeholder. A directory that can't be read
    // can't be served either, and an unplugged drive has to go on showing as unplugged.
    private static HashSet<string> LinksIn(string directory)
    {
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", EveryEntry))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null) links.Add(entry.Name);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Whatever was read so far stands.
        }

        return links;
    }
}
