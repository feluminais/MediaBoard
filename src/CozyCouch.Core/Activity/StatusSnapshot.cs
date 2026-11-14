namespace CozyCouch.Core.Activity;

public enum KeyState { Missing, Ok, Rejected }

public enum FolderScanState { Waiting, Scanning }

/// <summary>Where one folder's scan is. <paramref name="Total"/> is 0 until the scan has listed the folder.</summary>
public sealed record FolderScan(FolderScanState State, int Done, int Total);

/// <summary>Everything the status bar shows. Immutable: each change produces a new snapshot.</summary>
public sealed record StatusSnapshot
{
    /// <summary>
    /// Folder being scanned, or null when no scan is running. When scans overlap, the one that started last.
    /// It is a name, so two folders called "Movies" look the same here; <see cref="FolderScans"/> tells them apart.
    /// </summary>
    public string? ScanFolder { get; init; }

    public int ScanDone { get; init; }

    public int ScanTotal { get; init; }

    public int PendingIdentification { get; init; }

    public bool Online { get; init; }

    public KeyState KeyState { get; init; } = KeyState.Missing;

    /// <summary>Player currently running a file, or null when nothing is playing.</summary>
    public string? PlayerName { get; init; }

    /// <summary>"Sharing: 2 watching" while sharing is on, or null.</summary>
    public string? Sharing { get; init; }

    public IReadOnlyList<string> UnavailableFolders { get; init; } = [];

    /// <summary>The library database could not be opened: what to do about it. Null once it opened.</summary>
    public string? LibraryError { get; init; }

    /// <summary>Folders whose last scan threw; a folder leaves the list when a scan of it succeeds.</summary>
    public IReadOnlyList<string> FailedScans { get; init; } = [];

    /// <summary>The folders of <see cref="FailedScans"/> by id.</summary>
    public IReadOnlyList<int> FailedScanIds { get; init; } = [];

    /// <summary>Folders with a scan running or waiting to run, by folder id. A folder that is not here is idle.</summary>
    public IReadOnlyDictionary<int, FolderScan> FolderScans { get; init; } = new Dictionary<int, FolderScan>();

    public bool IsScanning => ScanFolder is not null;

    public FolderScan? ScanOf(int folderId) => FolderScans.GetValueOrDefault(folderId);
}
