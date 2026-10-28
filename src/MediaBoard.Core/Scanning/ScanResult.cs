namespace MediaBoard.Core.Scanning;

/// <param name="IgnoredFolders">Top-level folders with no video file anywhere below them, as relative paths.</param>
/// <param name="SkippedLocked">Files another program holds open (a download in progress); the next scan picks them up.</param>
/// <param name="FolderUnavailable">The folder could not be read, so its files were marked missing and nothing else changed.</param>
public sealed record ScanResult(
    int Added,
    int Changed,
    int MarkedMissing,
    int Restored,
    int Relinked,
    IReadOnlyList<string> IgnoredFolders,
    IReadOnlyList<string> SkippedLocked,
    bool FolderUnavailable);
