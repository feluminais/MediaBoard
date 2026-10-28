using Microsoft.VisualBasic.FileIO;

namespace MediaBoard.Core.Library;

/// <summary>Sends a file to the Windows Recycle Bin instead of deleting it for good.</summary>
public static class RecycleBin
{
    public static void Send(string path) =>
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
}
