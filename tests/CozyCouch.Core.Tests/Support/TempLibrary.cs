using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CozyCouch.Core.Tests.Support;

/// <summary>
/// A throwaway library folder. Files are sparse (marked so explicitly: NTFS reserves the space of a plain
/// SetLength), so a "400 MB" video costs nothing on disk.
/// </summary>
public sealed class TempLibrary : IDisposable
{
    // Above the 300 MB sample limit so ordinary names are never mistaken for sample clips.
    public const long DefaultSize = 400L * 1024 * 1024;

    private readonly List<string> _junctions = [];

    public TempLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), "cozycouch-lib-" + Guid.NewGuid().ToString("N"), "Library");
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

    /// <summary>A directory junction, which unlike a symbolic link needs no special rights to make.</summary>
    public void Junction(string relativePath, string target)
    {
        var path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", path, target])
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
        _junctions.Add(path);
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
        // A recursive delete refuses a junction without admin rights. Removing it alone leaves its target alone.
        foreach (var junction in _junctions.Where(Directory.Exists)) Directory.Delete(junction);
        var parent = Path.GetDirectoryName(Root)!;
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }
}
