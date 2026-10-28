using System.Runtime.InteropServices;

namespace MediaBoard.Core.Library;

public enum RecycleOutcome
{
    /// <summary>The file is in the Recycle Bin.</summary>
    Recycled,

    /// <summary>Windows asked (for example "this file is too big for the Recycle Bin, delete it for good?") and the answer was no.</summary>
    Declined,

    /// <summary>The file was not removed.</summary>
    Failed,
}

/// <param name="Detail">Why a <see cref="RecycleOutcome.Failed"/> delete failed, in words the user can read.</param>
public sealed record RecycleResult(RecycleOutcome Outcome, string? Detail = null)
{
    public static RecycleResult Recycled { get; } = new(RecycleOutcome.Recycled);

    public static RecycleResult Declined { get; } = new(RecycleOutcome.Declined);
}

/// <summary>
/// Sends a file to the Windows Recycle Bin. Windows deletes a file for good when it is bigger than the bin allows
/// or the drive has no bin (a network or removable drive). FOF_WANTNUKEWARNING makes it ask first instead, and a
/// "no" comes back as <see cref="RecycleOutcome.Declined"/>, so the caller never reports a permanent delete as a
/// recycle.
/// </summary>
public static partial class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofWantNukeWarning = 0x4000;

    public static RecycleResult Send(string path)
    {
        // pFrom is a list of paths, each ending in a null, with one more null after the last.
        var from = Marshal.StringToHGlobalUni(path + "\0");
        try
        {
            var operation = new ShFileOpStruct
            {
                wFunc = FoDelete,
                pFrom = from,
                fFlags = FofAllowUndo | FofNoConfirmation | FofWantNukeWarning | FofSilent,
            };
            var code = SHFileOperationW(ref operation);
            if (operation.fAnyOperationsAborted != 0 || code == DeCancelled) return RecycleResult.Declined;
            return code == 0
                ? RecycleResult.Recycled
                : new RecycleResult(RecycleOutcome.Failed, $"Windows could not move the file to the Recycle Bin (error 0x{code:X}).");
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    private const int DeCancelled = 0x75;

    // SHFILEOPSTRUCTW as laid out for a 64-bit process (the app is x64 only).
    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public nint hwnd;
        public uint wFunc;
        public nint pFrom;
        public nint pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public nint lpszProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(ref ShFileOpStruct operation);
}
