namespace MediaBoard.Core.Activity;

public enum KeyState { Missing, Ok, Rejected }

/// <summary>Everything the status bar shows. Immutable: each change produces a new snapshot.</summary>
public sealed record StatusSnapshot
{
    /// <summary>Folder being scanned, or null when no scan is running.</summary>
    public string? ScanFolder { get; init; }

    public int ScanDone { get; init; }

    public int ScanTotal { get; init; }

    public int PendingIdentification { get; init; }

    public bool Online { get; init; }

    public KeyState KeyState { get; init; } = KeyState.Missing;

    /// <summary>Player currently running a file, or null when nothing is playing.</summary>
    public string? PlayerName { get; init; }

    public IReadOnlyList<string> UnavailableFolders { get; init; } = [];

    public bool IsScanning => ScanFolder is not null;
}
