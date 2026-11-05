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
    private const int SqliteConstraintUnique = 2067;

    private const string Select =
        "SELECT id AS Id, name AS Name, role AS Role, disabled AS Disabled, password_hash IS NOT NULL AS HasPassword, " +
        "all_folders AS AllFolders, created_at AS CreatedAt, last_seen AS LastSeen FROM users";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <exception cref="DuplicateNameException">Another account has the name, ignoring case.</exception>
    public Account Create(string name, IReadOnlyCollection<int>? folderIds)
    {
        name = name.Trim();
        ArgumentException.ThrowIfNullOrEmpty(name);

        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
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
        var folders = FoldersByUser(connection);
        return connection.Query<Row>(Select + " ORDER BY id").Select(row => row.ToAccount(folders)).ToList();
    }

    public Account? Get(int id)
    {
        using var connection = db.Open();
        return connection.QuerySingleOrDefault<Row>(Select + " WHERE id = @id", new { id })?.ToAccount(FoldersByUser(connection));
    }

    public Account? ByName(string name)
    {
        using var connection = db.Open();
        return connection.QuerySingleOrDefault<Row>(Select + " WHERE name = @name", new { name })?.ToAccount(FoldersByUser(connection));
    }

    public string? PasswordHash(int id)
    {
        using var connection = db.Open();
        return connection.ExecuteScalar<string?>("SELECT password_hash FROM users WHERE id = @id", new { id });
    }

    /// <summary>Stores the hash from <see cref="Passwords.Hash"/>; null removes the password.</summary>
    public void SetPasswordHash(int id, string? hash)
    {
        using var connection = db.Open();
        connection.Execute("UPDATE users SET password_hash = @hash WHERE id = @id", new { id, hash });
    }

    /// <summary>Null gives access to all folders, including ones added later.</summary>
    public void SetFolders(int id, IReadOnlyCollection<int>? folderIds)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        connection.Execute("UPDATE users SET all_folders = @all WHERE id = @id", new { id, all = folderIds is null ? 1 : 0 }, transaction);
        connection.Execute("DELETE FROM user_folders WHERE user_id = @id", new { id }, transaction);
        WriteFolders(connection, transaction, id, folderIds);
        transaction.Commit();
    }

    public void SetDisabled(int id, bool disabled)
    {
        using var connection = db.Open();
        connection.Execute("UPDATE users SET disabled = @disabled WHERE id = @id", new { id, disabled = disabled ? 1 : 0 });
    }

    /// <summary>Deletes the account with its progress, devices and invites. The log keeps its entries without a user.</summary>
    /// <exception cref="InvalidOperationException">The account is the owner.</exception>
    public void Remove(int id)
    {
        if (id == LibraryDatabase.OwnerId) throw new InvalidOperationException("The owner account cannot be removed.");

        using var connection = db.Open();
        connection.Execute("DELETE FROM users WHERE id = @id AND role <> 'owner'", new { id });
    }

    public void Touch(int id)
    {
        using var connection = db.Open();
        connection.Execute("UPDATE users SET last_seen = @now WHERE id = @id", new { id, now = FileStore.FormatTime(_time.GetUtcNow()) });
    }

    public Viewer ViewerOf(Account account) =>
        new(account.Id, account.FolderIds?.ToHashSet());

    private static void WriteFolders(SqliteConnection connection, SqliteTransaction transaction, int id, IReadOnlyCollection<int>? folderIds)
    {
        if (folderIds is null) return;
        foreach (var folderId in folderIds.Distinct())
            connection.Execute("INSERT INTO user_folders(user_id, folder_id) VALUES (@id, @folderId)", new { id, folderId }, transaction);
    }

    private static Dictionary<int, List<int>> FoldersByUser(SqliteConnection connection) =>
        connection.Query<FolderRow>("SELECT user_id AS UserId, folder_id AS FolderId FROM user_folders ORDER BY folder_id")
            .GroupBy(x => x.UserId).ToDictionary(g => (int)g.Key, g => g.Select(x => (int)x.FolderId).ToList());

    private static DateTime ParseTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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
