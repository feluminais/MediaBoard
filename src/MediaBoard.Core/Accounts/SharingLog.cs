using System.Globalization;
using Dapper;
using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Accounts;

public enum SharingEvent { Joined, SignedIn, SignInLocked, StartedWatching, FinishedWatching, StreamStopped, SignedOut, Disabled }

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

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _inserts;

    public void Add(SharingEvent kind, int? userId, string detail)
    {
        using var connection = db.Open();
        connection.Execute(
            "INSERT INTO sharing_log(at, user_id, kind, detail) VALUES (@at, @userId, @kind, @detail)",
            new { at = FileStore.FormatTime(_time.GetUtcNow()), userId, kind = kind.ToString(), detail });

        if (Interlocked.Increment(ref _inserts) % TrimEvery != 1) return;
        connection.Execute(
            "DELETE FROM sharing_log WHERE id NOT IN (SELECT id FROM sharing_log ORDER BY id DESC LIMIT @Cap)", new { Cap });
    }

    /// <summary>The newest entries first.</summary>
    public IReadOnlyList<SharingLogEntry> Recent(int limit)
    {
        using var connection = db.Open();
        return connection.Query<Row>(
                "SELECT id AS Id, at AS At, user_id AS UserId, kind AS Kind, detail AS Detail FROM sharing_log ORDER BY id DESC LIMIT @limit",
                new { limit })
            .Select(r => new SharingLogEntry(
                r.Id, DateTime.Parse(r.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.UserId is { } id ? (int)id : null, Enum.Parse<SharingEvent>(r.Kind), r.Detail))
            .ToList();
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
