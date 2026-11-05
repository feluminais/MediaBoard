using MediaBoard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Accounts;

public enum RedeemFailure { InviteDead, PasswordTooShort, WrongPassword, Locked, AccountDisabled }

/// <summary>Holds <see cref="Issued"/> on success and <see cref="Failure"/> otherwise.</summary>
public sealed record RedeemResult(IssuedDevice? Issued, RedeemFailure? Failure);

/// <summary>Holds <see cref="Issued"/> on success and <see cref="Failure"/> otherwise.</summary>
public sealed record SignInResult(IssuedDevice? Issued, RedeemFailure? Failure);

/// <summary>A password change that did not go through. <see cref="Failure"/> says why, in the same terms as a redeem.</summary>
public sealed class PasswordRefusedException(RedeemFailure failure) : Exception($"The password was refused: {failure}.")
{
    public RedeemFailure Failure { get; } = failure;
}

/// <summary>The slow password calls, as a seam so tests can count and pace them.</summary>
internal sealed record PasswordWork(Func<string, string> Hash, Func<string, string, bool> Verify, Action<string> VerifyNothing)
{
    public static PasswordWork Real { get; } = new(Passwords.Hash, Passwords.Verify, Passwords.VerifyNothing);
}

/// <summary>
/// Decides who gets in. <see cref="Redeem"/> and <see cref="SignIn"/> face the open internet, <see cref="ChangePassword"/>
/// serves a signed-in person, and the rest are the owner's actions. Password work is slow, so call it off the UI thread.
/// <para>
/// A password check takes a moment, and the owner can act during it. So nothing is issued on the strength of a check
/// alone: a write transaction first confirms the account still has the checked hash and is not disabled.
/// </para>
/// </summary>
public sealed class AccountService(
    LibraryDatabase db, AccountStore accounts, DeviceStore devices, InviteStore invites, SignInGuard guard, SharingLog log)
{
    // One check runs, 8 wait. That limits the hashing strangers can cause; a new account's password is hashed outside
    // the gate, but reaching that takes a live invite.
    private const int MaxAtGate = 9;

    private readonly Lock _gate = new();
    private int _atGate;

    /// <summary>Raised after an account's tokens are dead, with the id of the account whose sessions must end.</summary>
    public event Action<int>? AccountClosed;

    /// <summary>
    /// Raised after one device's token is dead, with the device id, so what it has open can end. The account stays open:
    /// the person's other devices carry on.
    /// </summary>
    public event Action<int>? DeviceSignedOut;

    /// <summary>Raised with the id of an account whose folders changed.</summary>
    public event Action<int>? AccessChanged;

    internal PasswordWork Work { get; init; } = PasswordWork.Real;

    public Account AddPerson(string name, IReadOnlyCollection<int>? folderIds) => accounts.Create(name, folderIds);

    public void SetFolders(int userId, IReadOnlyCollection<int>? folderIds)
    {
        accounts.SetFolders(userId, folderIds);
        AccessChanged?.Invoke(userId);
    }

    /// <summary>Ends one device's session. It isn't a ban: the person can sign back in with their password.</summary>
    public void SignOutDevice(int deviceId)
    {
        if (devices.Get(deviceId) is not { } device) return;

        devices.Revoke(deviceId);
        DeviceSignedOut?.Invoke(deviceId);
        log.Add(SharingEvent.SignedOut, device.UserId, device.Name);
    }

    /// <summary>The ban: every session ends and sign-in fails until this is undone. Everything else is kept.</summary>
    /// <exception cref="InvalidOperationException">The account is the owner.</exception>
    public void Disable(int userId, bool disabled)
    {
        if (accounts.Get(userId) is null) return;

        InTransaction((connection, transaction) =>
        {
            accounts.SetDisabled(connection, transaction, userId, disabled);
            if (disabled) DeviceStore.RevokeAll(connection, transaction, userId);
        });
        if (!disabled) return;

        // Before the log, so sessions end even if the log write fails.
        AccountClosed?.Invoke(userId);
        log.Add(SharingEvent.Disabled, userId, "");
    }

    /// <summary>
    /// Clears the password and signs out every device, so only a new invite gets the person back in. Older invites die
    /// too: with no password set, whoever held one could choose the password themselves.
    /// </summary>
    public void ResetPassword(int userId)
    {
        if (accounts.Get(userId) is null) return;

        InTransaction((connection, transaction) =>
        {
            AccountStore.SetPasswordHash(connection, transaction, userId, null);
            DeviceStore.RevokeAll(connection, transaction, userId);
            InviteStore.CancelAll(connection, transaction, userId);
        });
        AccountClosed?.Invoke(userId);
        log.Add(SharingEvent.PasswordReset, userId, "");
    }

    /// <exception cref="PasswordRefusedException">The password is too short.</exception>
    public void SetOwnerPassword(string password)
    {
        RefuseShort(password);
        accounts.SetPasswordHash(LibraryDatabase.OwnerId, Work.Hash(password));
    }

    /// <summary>Deletes the account with its progress, devices and invites. The log keeps its entries without a user.</summary>
    /// <exception cref="InvalidOperationException">The account is the owner.</exception>
    public void Remove(int userId)
    {
        if (accounts.Get(userId) is not { } account) return;

        accounts.Remove(userId);
        AccountClosed?.Invoke(userId);
        // The row for this id is gone, so the entry carries the name and no user.
        log.Add(SharingEvent.Removed, null, account.Name);
    }

    /// <summary>The person's own change. Their devices stay signed in. Wrong guesses count toward the name's lock.</summary>
    /// <exception cref="PasswordRefusedException">The new password is too short, or the current one is wrong or locked out.</exception>
    public void ChangePassword(int userId, string current, string next)
    {
        RefuseShort(next);
        var check = CheckPassword(accounts.Get(userId)?.Name, current, null);
        if (check.Failure is { } failure) throw new PasswordRefusedException(failure);

        var hash = Work.Hash(next);
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!AccountStore.IsCurrent(connection, transaction, userId, check.Hash)) throw new PasswordRefusedException(RedeemFailure.WrongPassword);

        AccountStore.SetPasswordHash(connection, transaction, userId, hash);
        transaction.Commit();
    }

    /// <summary>
    /// Turns an invite into a device. A new account chooses its password here. A known account proves its password, and
    /// a null one fails with <see cref="RedeemFailure.WrongPassword"/> at no cost, so a client can ask which form to show.
    /// A failed redeem leaves the invite usable.
    /// </summary>
    public RedeemResult Redeem(string code, string? password, string deviceName, string address)
    {
        if (invites.Peek(code) is not { } userId || accounts.Get(userId) is not { } account)
            return new RedeemResult(null, RedeemFailure.InviteDead);
        if (account.Disabled) return new RedeemResult(null, RedeemFailure.AccountDisabled);

        return account.HasPassword
            ? RedeemKnown(account, code, password, deviceName, address)
            : RedeemNew(account, code, password, deviceName, address);
    }

    /// <summary>
    /// A wrong password, an unknown name and an account with no password all give <see cref="RedeemFailure.WrongPassword"/>
    /// at the same cost. <see cref="RedeemFailure.AccountDisabled"/> comes only after a right password, so a stranger
    /// can't use it to find out which names exist.
    /// </summary>
    public SignInResult SignIn(string name, string password, string deviceName, string address)
    {
        name = name.Trim();
        // No account can have a longer name, and the guard's keys must stay short.
        var check = CheckPassword(name.Length <= AccountStore.MaxNameLength ? name : null, password, address);
        if (check.Failure is { } failure) return new SignInResult(null, failure);
        if (check.Account!.Disabled) return new SignInResult(null, RedeemFailure.AccountDisabled);

        var issued = IssueIfCurrent(check.Account.Id, check.Hash, deviceName, address);
        if (issued is null) return new SignInResult(null, RedeemFailure.WrongPassword);

        log.Add(SharingEvent.SignedIn, check.Account.Id, deviceName);
        return new SignInResult(issued, null);
    }

    private RedeemResult RedeemNew(Account account, string code, string? password, string deviceName, string address)
    {
        if (password is null || password.Length < Passwords.MinLength) return new RedeemResult(null, RedeemFailure.PasswordTooShort);

        var result = Join(account.Id, code, null, Work.Hash(password), deviceName, address);
        if (result.Issued is not null) log.Add(SharingEvent.Joined, account.Id, deviceName);
        return result;
    }

    private RedeemResult RedeemKnown(Account account, string code, string? password, string deviceName, string address)
    {
        if (password is null) return new RedeemResult(null, RedeemFailure.WrongPassword);

        var check = CheckPassword(account.Name, password, address);
        if (check.Failure is { } failure) return new RedeemResult(null, failure);

        var result = Join(account.Id, code, check.Hash, null, deviceName, address);
        if (result.Issued is not null) log.Add(SharingEvent.SignedIn, account.Id, deviceName);
        return result;
    }

    /// <summary>
    /// The one place a password is tested. The gate spans the lock check, the slow hash and the tally: with those
    /// apart, parallel guesses would all pass the check before any of them counted.
    /// </summary>
    /// <param name="name">The trimmed name, or null for one no account can have, which then counts only against the address.</param>
    /// <param name="address">Null when there is no remote address to count against.</param>
    private PasswordCheck CheckPassword(string? name, string password, string? address)
    {
        try
        {
            if (Interlocked.Increment(ref _atGate) > MaxAtGate) return new PasswordCheck(RedeemFailure.Locked, null, null);

            lock (_gate)
            {
                if (guard.IsLocked(name, address)) return new PasswordCheck(RedeemFailure.Locked, null, null);

                var account = name is null ? null : accounts.ByName(name);
                var hash = account is null ? null : accounts.PasswordHash(account.Id);
                if (hash is null) Work.VerifyNothing(password);
                else if (Work.Verify(password, hash))
                {
                    guard.RecordSuccess(name!);
                    return new PasswordCheck(null, account, hash);
                }

                guard.RecordFailure(name, address);
                // Only the failure that trips a lock is logged, so guessing can't wash real entries out of the capped
                // log. The submitted name stays out of it: it could be a password typed in the wrong box.
                if (guard.IsLocked(name, address)) log.Add(SharingEvent.SignInLocked, account?.Id, "");
                return new PasswordCheck(RedeemFailure.WrongPassword, null, null);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _atGate);
        }
    }

    private IssuedDevice? IssueIfCurrent(int userId, string? hash, string deviceName, string address)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!AccountStore.IsCurrent(connection, transaction, userId, hash)) return null;

        var issued = devices.Issue(connection, transaction, userId, deviceName, address);
        transaction.Commit();
        return issued;
    }

    // The invite, the password and the device land together, so a crash can't leave a used invite with no password.
    private RedeemResult Join(int userId, string code, string? expectedHash, string? newHash, string deviceName, string address)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!AccountStore.IsCurrent(connection, transaction, userId, expectedHash)) return new RedeemResult(null, RedeemFailure.WrongPassword);
        if (invites.Consume(connection, transaction, code) is null) return new RedeemResult(null, RedeemFailure.InviteDead);

        if (newHash is not null) AccountStore.SetPasswordHash(connection, transaction, userId, newHash);
        var issued = devices.Issue(connection, transaction, userId, deviceName, address);
        transaction.Commit();
        return new RedeemResult(issued, null);
    }

    private void InTransaction(Action<SqliteConnection, SqliteTransaction> work)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        work(connection, transaction);
        transaction.Commit();
    }

    private static void RefuseShort(string password)
    {
        if (password.Length < Passwords.MinLength) throw new PasswordRefusedException(RedeemFailure.PasswordTooShort);
    }

    /// <param name="Failure">Null when the password was right.</param>
    /// <param name="Account">The account as read in the gate; set when the password was right.</param>
    /// <param name="Hash">The hash the password was checked against; set when the password was right.</param>
    private readonly record struct PasswordCheck(RedeemFailure? Failure, Account? Account, string? Hash);
}
