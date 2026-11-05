using System.Data;
using System.Globalization;
using Dapper;
using MediaBoard.Core.Library;
using MediaBoard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Accounts;

/// <summary>
/// The people who may use the library. Each call opens its own connection, so any thread may call it.
/// Removing an account deletes its progress through the foreign keys.
/// </summary>
public sealed class AccountStore(LibraryDatabase db, TimeProvider? time = null)
{
    public const int MaxNameLength = 64;

    private const int SqliteConstraintUnique = 2067;

    private const string Select =
        "SELECT id AS Id, name AS Name, role AS Role, disabled AS Disabled, password_hash IS NOT NULL AS HasPassword, " +
        "all_folders AS AllFolders, created_at AS CreatedAt, last_seen AS LastSeen FROM users";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Names are compared here, not by SQLite, whose NOCASE folds ASCII only: "Анна" and "АННА" must clash.
    /// The check and the insert share one write transaction, so two creates can't both pass it.
    /// </summary>
    /// <exception cref="DuplicateNameException">Another account has the name, ignoring case.</exception>
    /// <exception cref="ArgumentException">The name is empty or longer than <see cref="MaxNameLength"/>.</exception>
    public Account Create(string name, IReadOnlyCollection<int>? folderIds)
    {
        name = name.Trim();
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Length > MaxNameLength) throw new ArgumentException($"A name is at most {MaxNameLength} characters.", nameof(name));

        using var connection = db.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (FindByName(connection, transaction, name) is not null)
            throw new DuplicateNameException($"There is already an account named \"{name}\".");

        int id;
        try
        {
            id = connection.ExecuteScalar<int>(
                """
                INSERT INTO users(name, role, all_folders, created_at) VALUES (@name, 'viewer', @all, @now);
                SELECT last_insert_rowid()
                """,
                new { name, all = folderIds is null ? 1 : 0, now = FileStore.FormatTime(_time.GetUtcNow()) }, transaction);
        }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            throw new DuplicateNameException($"There is already an account named \"{name}\".", e);
        }

        WriteFolders(connection, transaction, id, folderIds);
        transaction.Commit();
        return Get(id)!;
    }

    public IReadOnlyList<Account> All()
    {
        using var connection = db.Open();
        var folders = FoldersByUser(connection, null);
        return connection.Query<Row>(Select + " ORDER BY id").Select(row => row.ToAccount(folders)).ToList();
    }

    public Account? Get(int id)
    {
        using var connection = db.Open();
        return GetOn(connection, id);
    }

    /// <summary>Ignores case (in any script) and surrounding spaces.</summary>
    public Account? ByName(string name)
    {
        using var connection = db.Open();
        return FindByName(connection, null, name.Trim()) is { } id ? GetOn(connection, id) : null;
    }

    public string? PasswordHash(int id)
    {
        using var connection = db.Open();
        return connection.ExecuteScalar<string?>("SELECT password_hash FROM users WHERE id = @id", new { id });
    }

    /// <summary>
    /// Stores the hash from <see cref="Passwords.Hash"/>; null removes the password. Internal because a password change
    /// can go with signing devices out: outside callers go through <see cref="AccountService"/>.
    /// </summary>
    internal void SetPasswordHash(int id, string? hash)
    {
        using var connection = db.Open();
        SetPasswordHash(connection, null, id, hash);
    }

    internal static void SetPasswordHash(SqliteConnection connection, SqliteTransaction? transaction, int id, string? hash) =>
        connection.Execute("UPDATE users SET password_hash = @hash WHERE id = @id", new { id, hash }, transaction);

    /// <summary>Whether the account exists, is not disabled, and still has this password hash (null for none).</summary>
    internal static bool IsCurrent(SqliteConnection connection, SqliteTransaction transaction, int id, string? hash) =>
        connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM users WHERE id = @id AND disabled = 0 AND password_hash IS @hash", new { id, hash }, transaction) != 0;

    /// <summary>Null gives access to all folders, including ones added later.</summary>
    /// <exception cref="InvalidOperationException">The account is the owner, who always reaches every folder.</exception>
    public void SetFolders(int id, IReadOnlyCollection<int>? folderIds)
    {
        RefuseOwner(id, "The owner always reaches every folder.");

        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        connection.Execute("UPDATE users SET all_folders = @all WHERE id = @id", new { id, all = folderIds is null ? 1 : 0 }, transaction);
        connection.Execute("DELETE FROM user_folders WHERE user_id = @id", new { id }, transaction);
        WriteFolders(connection, transaction, id, folderIds);
        transaction.Commit();
    }

    /// <summary>
    /// Takes the caller's transaction, which must also revoke the devices. Internal so disabling can't skip that:
    /// outside callers go through <see cref="AccountService"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The account is the owner.</exception>
    internal void SetDisabled(SqliteConnection connection, SqliteTransaction transaction, int id, bool disabled)
    {
        RefuseOwner(id, "The owner account cannot be disabled.");

        connection.Execute("UPDATE users SET disabled = @disabled WHERE id = @id", new { id, disabled = disabled ? 1 : 0 }, transaction);
    }

    /// <summary>Deletes the account with its progress, devices and invites. The log keeps its entries without a user.</summary>
    /// <exception cref="InvalidOperationException">The account is the owner.</exception>
    public void Remove(int id)
    {
        RefuseOwner(id, "The owner account cannot be removed.");

        using var connection = db.Open();
        connection.Execute("DELETE FROM users WHERE id = @id AND role <> 'owner'", new { id });
    }

    public void Touch(int id)
    {
        using var connection = db.Open();
        connection.Execute("UPDATE users SET last_seen = @now WHERE id = @id", new { id, now = FileStore.FormatTime(_time.GetUtcNow()) });
    }

    /// <summary>The viewer a device of this account acts as.</summary>
    public Viewer ViewerOf(Account account) =>
        new(account.Id, account.FolderIds?.ToHashSet(), OverNetwork: true);

    private static void RefuseOwner(int id, string message)
    {
        if (id == LibraryDatabase.OwnerId) throw new InvalidOperationException(message);
    }

    private static Account? GetOn(SqliteConnection connection, int id) =>
        connection.QuerySingleOrDefault<Row>(Select + " WHERE id = @id", new { id })?.ToAccount(FoldersByUser(connection, id));

    // The accounts table is small, so comparing in code costs nothing and handles every script.
    private static int? FindByName(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        foreach (var row in connection.Query<NameRow>("SELECT id AS Id, name AS Name FROM users", transaction: transaction))
            if (string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)) return (int)row.Id;
        return null;
    }

    private static void WriteFolders(SqliteConnection connection, SqliteTransaction transaction, int id, IReadOnlyCollection<int>? folderIds)
    {
        if (folderIds is null) return;
        foreach (var folderId in folderIds.Distinct())
            connection.Execute("INSERT INTO user_folders(user_id, folder_id) VALUES (@id, @folderId)", new { id, folderId }, transaction);
    }

    private static Dictionary<int, List<int>> FoldersByUser(SqliteConnection connection, int? userId) =>
        connection.Query<FolderRow>(
                "SELECT user_id AS UserId, folder_id AS FolderId FROM user_folders WHERE @userId IS NULL OR user_id = @userId ORDER BY folder_id",
                new { userId })
            .GroupBy(x => x.UserId).ToDictionary(g => (int)g.Key, g => g.Select(x => (int)x.FolderId).ToList());

    private static DateTime ParseTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class NameRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class FolderRow
    {
        public long UserId { get; set; }
        public long FolderId { get; set; }
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public long Disabled { get; set; }
        public long HasPassword { get; set; }
        public long AllFolders { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? LastSeen { get; set; }

        public Account ToAccount(Dictionary<int, List<int>> folders) => new(
            (int)Id, Name, Role == "owner" ? AccountRole.Owner : AccountRole.Viewer, Disabled != 0, HasPassword != 0,
            AllFolders != 0 ? null : folders.GetValueOrDefault((int)Id) ?? [],
            ParseTime(CreatedAt), LastSeen is null ? null : ParseTime(LastSeen));
    }
}
