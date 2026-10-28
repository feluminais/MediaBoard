namespace MediaBoard.Core.Players;

/// <summary>What a player session reports while a file plays.</summary>
public abstract record PlaybackEvent
{
    /// <summary>The file is open; <paramref name="Duration"/> is zero when the player does not know it.</summary>
    public sealed record Loaded(TimeSpan Duration) : PlaybackEvent;

    public sealed record Position(TimeSpan At) : PlaybackEvent;

    public sealed record Paused : PlaybackEvent;

    public sealed record Playing : PlaybackEvent;

    /// <summary>Playback reached the end of the file. The player may stay open.</summary>
    public sealed record Ended : PlaybackEvent;

    /// <summary>The player closed. Nothing follows.</summary>
    public sealed record Closed : PlaybackEvent;

    /// <summary>The player could not start or could not open the file. Nothing follows.</summary>
    public sealed record Failed(string Message) : PlaybackEvent;
}
