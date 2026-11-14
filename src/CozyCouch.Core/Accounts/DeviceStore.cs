using System.Globalization;
using System.Text;
using Dapper;
using CozyCouch.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CozyCouch.Core.Accounts;

/// <param name="CreatedAt">In UTC.</param>
/// <param name="LastSeen">In UTC.</param>
public sealed record Device(int Id, int UserId, string Name, DateTime CreatedAt, DateTime? LastSeen, string? LastAddress);

/// <param name="Token">The raw token. It exists only here, for the caller to hand to the device once.</param>
public sealed record IssuedDevice(Device Device, string Token)
{
    // The generated ToString would print the token into any log that formats this record.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Device = {Device}");
        return true;
    }
}

/// <summary>
/// The devices signed in to an account. Only a hash of each token is stored, so a copy of the database lets nobody in.
/// Each call opens its own connection, so any thread may call it.
/// </summary>
public sealed class DeviceStore(LibraryDatabase db, TimeProvider? time = null)
{
    private const int TokenBytes = 32;

    private const string Select =
        "SELECT id AS Id, user_id AS UserId, name AS Name, created_at AS CreatedAt, last_seen AS LastSeen, last_address AS LastAddress FROM devices";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public IssuedDevice Issue(int userId, string name, string address)
    {
        using var connection = db.Open();
        return Issue(connection, null, userId, name, address);
    }

    internal IssuedDevice Issue(SqliteConnection connection, SqliteTransaction? transaction, int userId, string name, string address)
    {
        var token = Secrets.New(TokenBytes);
        var id = connection.ExecuteScalar<int>(
            """
            INSERT INTO devices(user_id, name, token_hash, created_at, last_seen, last_address)
            VALUES (@userId, @name, @hash, @now, @now, @address);
            SELECT last_insert_rowid()
            """,
            new { userId, name, hash = Secrets.Hash(token), now = FileStore.FormatTime(_time.GetUtcNow()), address }, transaction);
        return new IssuedDevice(connection.QuerySingle<Row>(Select + " WHERE id = @id", new { id }, transaction).ToDevice(), token);
    }

    public Device? ByToken(string token)
    {
        using var connection = db.Open();
        return connection.QuerySingleOrDefault<Row>(Select + " WHERE token_hash = @hash", new { hash = Secrets.Hash(token) })?.ToDevice();
    }

    public Device? Get(int deviceId)
    {
        using var connection = db.Open();
        return connection.QuerySingleOrDefault<Row>(Select + " WHERE id = @deviceId", new { deviceId })?.ToDevice();
    }

    public IReadOnlyList<Device> ForUser(int userId)
    {
        using var connection = db.Open();
        return connection.Query<Row>(Select + " WHERE user_id = @userId ORDER BY id", new { userId }).Select(row => row.ToDevice()).ToList();
    }

    public void Revoke(int deviceId)
    {
        using var connection = db.Open();
        connection.Execute("DELETE FROM devices WHERE id = @deviceId", new { deviceId });
    }

    public void RevokeAll(int userId)
    {
        using var connection = db.Open();
        RevokeAll(connection, null, userId);
    }

    internal static void RevokeAll(SqliteConnection connection, SqliteTransaction? transaction, int userId) =>
        connection.Execute("DELETE FROM devices WHERE user_id = @userId", new { userId }, transaction);

    /// <summary>Signs out whichever of the account's devices were used longest ago, past <paramref name="keep"/> of them.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="userId">The account.</param>
    /// <param name="newDeviceId">The device just issued, which stays whatever the clock did.</param>
    /// <param name="keep">How many devices the account keeps, that one included.</param>
    /// <returns>The devices that went, with their names.</returns>
    internal static IReadOnlyList<(long Id, string Name)> RevokeBeyond(SqliteConnection connection, SqliteTransaction transaction, int userId, int newDeviceId, int keep) =>
        connection.Query<(long Id, string Name)>(
            """
            DELETE FROM devices WHERE id IN
                (SELECT id FROM devices WHERE user_id = @userId AND id <> @newDeviceId ORDER BY last_seen DESC, id DESC LIMIT -1 OFFSET @others)
            RETURNING id, name
            """,
            new { userId, newDeviceId, others = keep - 1 },
            transaction).ToList();

    /// <param name="keepDeviceId">The device that stays signed in, or null for none.</param>
    /// <returns>The devices that went, with their names.</returns>
    internal static IReadOnlyList<(long Id, string Name)> RevokeOthers(SqliteConnection connection, SqliteTransaction transaction, int userId, int? keepDeviceId) =>
        connection.Query<(long Id, string Name)>(
            "DELETE FROM devices WHERE user_id = @userId AND (@keepDeviceId IS NULL OR id <> @keepDeviceId) RETURNING id, name",
            new { userId, keepDeviceId },
            transaction).ToList();

    public void Touch(int deviceId, string address)
    {
        using var connection = db.Open();
        connection.Execute(
            "UPDATE devices SET last_seen = @now, last_address = @address WHERE id = @deviceId",
            new { deviceId, address, now = FileStore.FormatTime(_time.GetUtcNow()) });
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public long UserId { get; set; }
        public string Name { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string? LastSeen { get; set; }
        public string? LastAddress { get; set; }

        public Device ToDevice() => new(
            (int)Id, (int)UserId, Name, FileStore.ParseTime(CreatedAt),
            LastSeen is null ? null : FileStore.ParseTime(LastSeen), LastAddress);
    }
}
