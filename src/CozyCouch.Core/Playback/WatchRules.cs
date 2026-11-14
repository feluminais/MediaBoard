namespace CozyCouch.Core.Playback;

/// <summary>When a file counts as watched.</summary>
public static class WatchRules
{
    /// <summary>A position at or past 90% of the duration. An unknown duration (0 or less) is never watched.</summary>
    public static bool IsWatched(double positionS, double durationS) =>
        durationS > 0 && positionS * 10 >= durationS * 9;

    /// <summary>
    /// Whether the player's Ended event means the viewer saw the file out: the last known position is unknown, the
    /// duration is unknown, or the position is within 10% of the duration plus 15 s of the end. A player that stops
    /// early (VLC raises Ended when the user presses Stop) does not count.
    /// </summary>
    public static bool EndedCountsAsWatched(double? lastPositionS, double durationS)
    {
        if (lastPositionS is not { } position || durationS <= 0) return true;
        return durationS - position <= durationS * 0.1 + 15;
    }
}
