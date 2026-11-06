using System.Globalization;
using Dapper;
using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Accounts;

public enum SharingEvent { Joined, SignedIn, SignInLocked, StartedWatching, FinishedWatching, StreamStopped, SignedOut, Disabled, PasswordReset, Removed, NewAddress }

/// <param name="UserId">Null once the account was removed.</param>
/// <param name="At">In UTC.</param>
public sealed record SharingLogEntry(long Id, DateTime At, int? UserId, SharingEvent Kind, string Detail);

/// <summary>
/// What happened on the server. It keeps about the newest 5,000 rows: trimming runs on the first insert and then
/// every 100th, so the table may hold up to 99 more in between.
/// </summary>
public sealed class SharingLog(LibraryDatabase db, TimeProvider? time = null)
{
    public const int Cap = 5_000;
    private const int TrimEvery = 100;

    // A detail can be whatever a viewer sends, so repeats alone don't bound it. A long day of episodes stays under this.
    private const int MostOfAKindPerDay = 50;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _inserts;

    /// <param name="userId">An account that has gone by now is stored as no user, so a removal racing a sign-in can't fail it.</param>
    public void Add(SharingEvent kind, int? userId, string detail)
    {
        using var connection = db.Open();
        connection.Execute(
            "INSERT INTO sharing_log(at, user_id, kind, detail) VALUES (@at, (SELECT id FROM users WHERE id = @userId), @kind, @detail)",
            new { at = FileStore.FormatTime(_time.GetUtcNow()), userId, kind = kind.ToString(), detail });

        if (Interlocked.Increment(ref _inserts) % TrimEvery != 1) return;
        connection.Execute(
            "DELETE FROM sharing_log WHERE id NOT IN (SELECT id FROM sharing_log ORDER BY id DESC LIMIT @Cap)", new { Cap });
    }

    /// <summary>
    /// For what a viewer can repeat at will, such as marking a title watched and back: the log has a cap, and one viewer
    /// mustn't be able to push everything else out of it. An account also gets at most 50 entries of a kind a day.
    /// </summary>
    public void AddOncePerDay(SharingEvent kind, int userId, string detail)
    {
        using (var connection = db.Open())
        {
            var today = connection.QuerySingle<(long Same, long All)>(
                """
                SELECT COUNT(CASE WHEN detail = @detail THEN 1 END), COUNT(*) FROM sharing_log
                WHERE kind = @kind AND user_id = @userId AND at > @since
                """,
                new { kind = kind.ToString(), userId, detail, since = FileStore.FormatTime(_time.GetUtcNow().AddDays(-1)) });
            if (today.Same > 0 || today.All >= MostOfAKindPerDay) return;
        }

        Add(kind, userId, detail);
    }

    /// <summary>The newest entries first.</summary>
    public IReadOnlyList<SharingLogEntry> Recent(int limit)
    {
        using var connection = db.Open();
        var entries = new List<SharingLogEntry>();
        foreach (var r in connection.Query<Row>(
                     "SELECT id AS Id, at AS At, user_id AS UserId, kind AS Kind, detail AS Detail FROM sharing_log ORDER BY id DESC LIMIT @limit",
                     new { limit }))
        {
            // A kind this version doesn't know (written by a newer one) is skipped instead of failing the whole read.
            if (!Enum.TryParse<SharingEvent>(r.Kind, out var kind) || !Enum.IsDefined(kind)) continue;
            entries.Add(new SharingLogEntry(
                r.Id, DateTime.Parse(r.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.UserId is { } id ? (int)id : null, kind, r.Detail));
        }

        return entries;
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public string At { get; set; } = "";
        public long? UserId { get; set; }
        public string Kind { get; set; } = "";
        public string Detail { get; set; } = "";
    }
}
