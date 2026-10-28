using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MediaBoard.Core.Tests.Support;

/// <summary>
/// A throwaway library folder. Files are sparse (marked so explicitly: NTFS reserves the space of a plain
/// SetLength), so a "400 MB" video costs nothing on disk.
/// </summary>
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
        MarkSparse(stream.SafeFileHandle);
        stream.SetLength(size);
        return path;
    }

    private const uint FsctlSetSparse = 0x000900C4;

    // Best effort: on a file system without sparse files the call fails and the file is simply allocated.
    private static void MarkSparse(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
            DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle handle, uint code, IntPtr inBuffer, uint inSize, IntPtr outBuffer, uint outSize,
        out uint bytesReturned, IntPtr overlapped);

    public void Resize(string relativePath, long size)
    {
        using var stream = new FileStream(PathOf(relativePath), FileMode.Open, FileAccess.Write, FileShare.None);
        MarkSparse(stream.SafeFileHandle);
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
