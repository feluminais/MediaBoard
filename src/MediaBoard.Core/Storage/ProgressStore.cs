using System.Globalization;
using Dapper;

namespace MediaBoard.Core.Storage;

/// <param name="LastPlayed">When the file was last played, in UTC.</param>
public sealed record Progress(int FileId, double PositionS, double DurationS, bool Watched, DateTime LastPlayed);

/// <summary>
/// Where each file was left and whether it counts as watched. Writes are single upserts, so any thread may call them.
/// An episode is watched when a file linked to it through file_episodes is: one file can cover several episodes.
/// </summary>
public sealed class ProgressStore(LibraryDatabase db, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Progress? Get(int userId, int fileId)
    {
        using var connection = db.Open();
        return connection.QuerySingleOrDefault<Row>(
            "SELECT file_id AS FileId, position_s AS PositionS, duration_s AS DurationS, watched AS Watched, " +
            "last_played AS LastPlayed FROM progress WHERE user_id = @userId AND file_id = @fileId", new { userId, fileId })?.ToProgress();
    }

    /// <summary>
    /// Stores where the file was left and stamps it as just played. The watched flag is kept as it is. A duration
    /// of 0 means "unknown" and keeps the stored one.
    /// </summary>
    public void Save(int userId, int fileId, double positionS, double durationS)
    {
        using var connection = db.Open();
        connection.Execute(
            """
            INSERT INTO progress(user_id, file_id, position_s, duration_s, watched, last_played)
            VALUES (@userId, @fileId, @positionS, @durationS, 0, @now)
            ON CONFLICT(user_id, file_id) DO UPDATE SET
                position_s = excluded.position_s,
                duration_s = CASE WHEN excluded.duration_s > 0 THEN excluded.duration_s ELSE progress.duration_s END,
                last_played = excluded.last_played
            """,
            new { userId, fileId, positionS, durationS, now = Now() });
    }

    /// <summary>
    /// Marks the file watched, keeping its position. Marking it unwatched also forgets the position, so the next play
    /// starts at the beginning instead of the end.
    /// </summary>
    public void SetWatched(int userId, int fileId, bool watched)
    {
        using var connection = db.Open();
        if (watched)
        {
            connection.Execute(
                """
                INSERT INTO progress(user_id, file_id, position_s, duration_s, watched, last_played)
                VALUES (@userId, @fileId, 0, 0, 1, @now)
                ON CONFLICT(user_id, file_id) DO UPDATE SET watched = 1
                """,
                new { userId, fileId, now = Now() });
        }
        else
        {
            connection.Execute(
                "UPDATE progress SET watched = 0, position_s = 0 WHERE user_id = @userId AND file_id = @fileId",
                new { userId, fileId });
        }
    }

    /// <summary>
    /// Takes the title off Continue watching until something of it is played after <paramref name="playedAt"/>. The
    /// progress rows are not touched.
    /// </summary>
    public void DismissContinue(int userId, int titleId, DateTime playedAt)
    {
        using var connection = db.Open();
        connection.Execute(
            """
            INSERT INTO continue_dismissed(user_id, title_id, played_at) VALUES (@userId, @titleId, @playedAt)
            ON CONFLICT(user_id, title_id) DO UPDATE SET played_at = excluded.played_at
            """,
            new { userId, titleId, playedAt = FileStore.FormatTime(new DateTimeOffset(DateTime.SpecifyKind(playedAt, DateTimeKind.Utc))) });
    }

    /// <summary>The ids of the episodes covered by a watched file, each once.</summary>
    public IReadOnlyList<int> WatchedEpisodeIds(int userId)
    {
        using var connection = db.Open();
        return connection.Query<int>(
            "SELECT DISTINCT fe.episode_id FROM file_episodes fe " +
            "JOIN progress p ON p.file_id = fe.file_id WHERE p.user_id = @userId AND p.watched = 1 ORDER BY fe.episode_id",
            new { userId }).ToList();
    }

    private string Now() => FileStore.FormatTime(_time.GetUtcNow());

    private sealed class Row
    {
        public long FileId { get; set; }
        public double PositionS { get; set; }
        public double DurationS { get; set; }
        public long Watched { get; set; }
        public string? LastPlayed { get; set; }

        public Progress ToProgress() => new(
            (int)FileId, PositionS, DurationS, Watched != 0,
            LastPlayed is null
                ? DateTime.MinValue
                : DateTime.Parse(LastPlayed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }
}
