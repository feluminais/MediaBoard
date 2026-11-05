using System.Globalization;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Storage;

/// <param name="LastPlayed">When the file was last played, in UTC.</param>
public sealed record Progress(int FileId, double PositionS, double DurationS, bool Watched, DateTime LastPlayed);

/// <summary>One report of where a player got to.</summary>
/// <param name="DurationS">0 means "unknown" and keeps the stored one.</param>
/// <param name="Watched">Null keeps the stored flag.</param>
/// <param name="At">When the report was made, by the reporting device's clock.</param>
/// <remarks>Only <see cref="Watched"/> may be left out of the JSON: a position left out would bind to 0 and clear the saved one.</remarks>
public sealed record ProgressEntry(
    [property: JsonRequired] double PositionS,
    [property: JsonRequired] double DurationS,
    bool? Watched,
    [property: JsonRequired] DateTimeOffset At);

/// <summary>
/// Where each file was left and whether it counts as watched. Writes are single upserts, so any thread may call them.
/// An episode is watched when a file linked to it through file_episodes is: one file can cover several episodes.
/// </summary>
public sealed class ProgressStore(LibraryDatabase db, TimeProvider? timeProvider = null)
{
    private const int SqliteConstraintForeignKey = 787;

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
    public void SetWatched(int userId, int fileId, bool watched) => SetWatched(userId, [fileId], watched);

    /// <summary>All of them or none, so a failure can't leave half a show marked.</summary>
    public void SetWatched(int userId, IReadOnlyCollection<int> fileIds, bool watched)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var fileId in fileIds)
        {
            if (watched)
            {
                connection.Execute(
                    """
                    INSERT INTO progress(user_id, file_id, position_s, duration_s, watched, last_played)
                    VALUES (@userId, @fileId, 0, 0, 1, @now)
                    ON CONFLICT(user_id, file_id) DO UPDATE SET watched = 1
                    """,
                    new { userId, fileId, now = Now() },
                    transaction);
            }
            else
            {
                connection.Execute(
                    "UPDATE progress SET watched = 0, position_s = 0 WHERE user_id = @userId AND file_id = @fileId",
                    new { userId, fileId },
                    transaction);
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Stores an entry unless a newer one is already there, so devices settle by the newest time however their reports
    /// arrive. A time ahead of this clock counts as now: taken as given, a phone an hour fast would beat every honest
    /// entry for that hour. A tie goes to the later arrival, which two reports from such a phone can be.
    /// </summary>
    /// <returns>Whether the entry was stored.</returns>
    /// <exception cref="ArgumentException">The file or the user doesn't exist.</exception>
    public bool SaveIfNewer(int userId, int fileId, ProgressEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var now = _time.GetUtcNow();
        using var connection = db.Open();
        try
        {
            return connection.Execute(
                """
                INSERT INTO progress(user_id, file_id, position_s, duration_s, watched, last_played)
                VALUES (@userId, @fileId, @positionS, @durationS, COALESCE(@watched, 0), @at)
                ON CONFLICT(user_id, file_id) DO UPDATE SET
                    position_s = excluded.position_s,
                    duration_s = CASE WHEN excluded.duration_s > 0 THEN excluded.duration_s ELSE progress.duration_s END,
                    watched = COALESCE(@watched, progress.watched),
                    last_played = excluded.last_played
                WHERE progress.last_played IS NULL OR excluded.last_played >= progress.last_played
                """,
                new
                {
                    userId,
                    fileId,
                    positionS = entry.PositionS,
                    durationS = entry.DurationS,
                    watched = entry.Watched,
                    at = FileStore.FormatTime(entry.At < now ? entry.At : now),
                }) > 0;
        }
        catch (Exception e) when (IsUnknownFile(e))
        {
            throw new ArgumentException($"There is no file {fileId} for this user to save.", nameof(fileId), e);
        }
    }

    /// <summary>
    /// Whether a save failed because its file or user has gone, which only the write can find out when it goes at the
    /// moment a request gets here. <see cref="SetWatched"/> lets this exception through for callers that handle every
    /// database failure alike.
    /// </summary>
    public static bool IsUnknownFile(Exception exception) => exception is SqliteException { SqliteExtendedErrorCode: SqliteConstraintForeignKey };

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
