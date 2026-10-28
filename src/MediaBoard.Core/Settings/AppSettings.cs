namespace MediaBoard.Core.Settings;

public sealed record AppSettings
{
    public string? TmdbKey { get; init; }

    public string? DefaultPlayerId { get; init; }

    public string? CustomPlayerPath { get; init; }
}
