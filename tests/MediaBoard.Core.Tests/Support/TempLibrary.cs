namespace MediaBoard.Core.Tests.Support;

/// <summary>A throwaway library folder. Files are sparse, so a "400 MB" video costs nothing on disk.</summary>
public sealed class TempLibrary : IDisposable
{
    // Above the 300 MB sample limit so ordinary names are never mistaken for sample clips.
    public const long DefaultSize = 400L * 1024 * 1024;

    public TempLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), "mediaboard-lib-" + Guid.NewGuid().ToString("N"), "Library");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string relativePath) => Path.Combine(Root, relativePath);

    public string Folder(string relativePath)
    {
        var path = PathOf(relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relativePath, long size = DefaultSize)
    {
        var path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(size);
        return path;
    }

    public void Resize(string relativePath, long size)
    {
        using var stream = new FileStream(PathOf(relativePath), FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(size);
    }

    public void Delete(string relativePath) => System.IO.File.Delete(PathOf(relativePath));

    public void Move(string from, string to)
    {
        var target = PathOf(to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        System.IO.File.Move(PathOf(from), target);
    }

    public void Hide(string relativePath) =>
        System.IO.File.SetAttributes(PathOf(relativePath), FileAttributes.Hidden);

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(Root)!;
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }
}
