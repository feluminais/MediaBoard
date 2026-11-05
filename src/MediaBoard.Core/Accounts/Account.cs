namespace MediaBoard.Core.Accounts;

public enum AccountRole { Owner, Viewer }

/// <param name="HasPassword">Whether a password is set. The hash itself is read through <see cref="AccountStore.PasswordHash"/>.</param>
/// <param name="FolderIds">The folders the account may reach; null means all of them, including ones added later.</param>
/// <param name="CreatedAt">In UTC.</param>
/// <param name="LastSeen">In UTC; null until the account has been seen.</param>
public sealed record Account(
    int Id, string Name, AccountRole Role, bool Disabled, bool HasPassword,
    IReadOnlyList<int>? FolderIds, DateTime CreatedAt, DateTime? LastSeen);
