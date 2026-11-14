using Dapper;
using CozyCouch.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CozyCouch.Core.Accounts;

/// <param name="CreatedAt">In UTC.</param>
/// <param name="ExpiresAt">In UTC.</param>
public sealed record PendingInvite(int Id, int UserId, DateTime CreatedAt, DateTime ExpiresAt);

/// <summary>
/// One-use invites that die after 24 hours. Only a hash of each code is stored. Each call opens its own connection,
/// so any thread may call it.
/// </summary>
public sealed class InviteStore(LibraryDatabase db, TimeProvider? time = null)
{
    private const int CodeBytes = 16;

    private static readonly TimeSpan Life = TimeSpan.FromHours(24);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <returns>The raw code, which is not stored and can't be read back.</returns>
    public string Create(int userId)
    {
        var code = Secrets.New(CodeBytes);
        var now = _time.GetUtcNow();
        using var connection = db.Open();
        connection.Execute(
            "INSERT INTO invites(user_id, code_hash, created_at, expires_at) VALUES (@userId, @hash, @created, @expires)",
            new { userId, hash = Secrets.Hash(code), created = FileStore.FormatTime(now), expires = FileStore.FormatTime(now + Life) });
        return code;
    }

    /// <returns>The account a live invite belongs to, or null. Leaves the invite as it is.</returns>
    public int? Peek(string code)
    {
        using var connection = db.Open();
        return connection.ExecuteScalar<int?>(
            "SELECT user_id FROM invites WHERE code_hash = @hash AND used_at IS NULL AND expires_at > @now",
            new { hash = Secrets.Hash(code), now = Now() });
    }

    /// <summary>Uses a live invite up. Two callers can't both get it: the update is what decides.</summary>
    /// <returns>The account the invite belongs to, or null if it was not live.</returns>
    public int? Consume(string code)
    {
        using var connection = db.Open();
        return Consume(connection, null, code);
    }

    internal int? Consume(SqliteConnection connection, SqliteTransaction? transaction, string code) =>
        connection.ExecuteScalar<int?>(
            "UPDATE invites SET used_at = @now WHERE code_hash = @hash AND used_at IS NULL AND expires_at > @now RETURNING user_id",
            new { hash = Secrets.Hash(code), now = Now() }, transaction);

    public IReadOnlyList<PendingInvite> Pending(int userId)
    {
        using var connection = db.Open();
        return connection.Query<Row>(
                """
                SELECT id AS Id, user_id AS UserId, created_at AS CreatedAt, expires_at AS ExpiresAt
                FROM invites WHERE user_id = @userId AND used_at IS NULL AND expires_at > @now ORDER BY id
                """,
                new { userId, now = Now() })
            .Select(row => row.ToInvite()).ToList();
    }

    /// <returns>How many live invites each account has. An account with none isn't listed.</returns>
    public IReadOnlyDictionary<int, int> PendingCounts()
    {
        using var connection = db.Open();
        return connection.Query<CountRow>(
                "SELECT user_id AS UserId, COUNT(*) AS Count FROM invites WHERE used_at IS NULL AND expires_at > @now GROUP BY user_id",
                new { now = Now() })
            .ToDictionary(row => (int)row.UserId, row => (int)row.Count);
    }

    public void Cancel(int inviteId)
    {
        using var connection = db.Open();
        connection.Execute("DELETE FROM invites WHERE id = @inviteId", new { inviteId });
    }

    internal static void CancelAll(SqliteConnection connection, SqliteTransaction transaction, int userId) =>
        connection.Execute("DELETE FROM invites WHERE user_id = @userId", new { userId }, transaction);

    private string Now() => FileStore.FormatTime(_time.GetUtcNow());

    private sealed class CountRow
    {
        public long UserId { get; set; }
        public long Count { get; set; }
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public long UserId { get; set; }
        public string CreatedAt { get; set; } = "";
        public string ExpiresAt { get; set; } = "";

        public PendingInvite ToInvite() =>
            new((int)Id, (int)UserId, FileStore.ParseTime(CreatedAt), FileStore.ParseTime(ExpiresAt));
    }
}
