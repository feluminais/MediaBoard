namespace MediaBoard.Core.Settings;

public sealed record AppSettings
{
    public string? TmdbKey { get; init; }

    public string? DefaultPlayerId { get; init; }

    public string? CustomPlayerPath { get; init; }

    /// <summary>Full path to ffprobe.exe; null looks "ffprobe" up on PATH.</summary>
    public string? FfprobePath { get; init; }
}
