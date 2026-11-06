using Dapper;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Accounts;

public sealed class AccountServiceTests : IDisposable
{
    private const string Address = "203.0.113.5";
    private const string Good = "correct horse battery";
    private const string Bad = "wrong horse battery";
    private const string PasswordInNameBox = "this-could-be-a-password";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly FakeClock _clock = new();
    private readonly AccountStore _accounts;
    private readonly DeviceStore _devices;
    private readonly InviteStore _invites;
    private readonly SignInGuard _guard;
    private readonly SharingLog _log;
    private readonly AccountService _service;

    private int _checks;
    private Action _whileChecking = () => { };
    private Action _whileHashing = () => { };

    public AccountServiceTests()
    {
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _accounts = new AccountStore(_db, _clock);
        _devices = new DeviceStore(_db, _clock);
        _invites = new InviteStore(_db, _clock);
        _guard = new SignInGuard(_clock);
        _log = new SharingLog(_db, _clock);
        _service = NewService(new PasswordWork(
            password =>
            {
                _whileHashing();
                return "fake$" + password;
            },
            (password, stored) =>
            {
                Check();
                return stored == "fake$" + password;
            },
            _ => Check()));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private AccountService NewService(PasswordWork work) =>
        new(_db, _accounts, _devices, _invites, _guard, _log) { Work = work };

    private void Check()
    {
        Interlocked.Increment(ref _checks);
        _whileChecking();
    }

    private Account Joined(string name = "Anna")
    {
        var account = _service.AddPerson(name, null);
        Assert.NotNull(_service.Redeem(_invites.Create(account.Id), Good, "First", Address).Issued);
        return _accounts.Get(account.Id)!;
    }

    private IssuedDevice SignedIn(Account account) =>
        _service.SignIn(account.Name, Good, "Laptop", Address).Issued!;

    private void Trip(string name)
    {
        for (var i = 0; i < 5; i++) _service.SignIn(name, Bad, "Laptop", Address);
    }

    private bool IsUsed(string code) => _invites.Peek(code) is null;

    private List<SharingLogEntry> Logged(SharingEvent kind) =>
        _log.Recent(100).Where(e => e.Kind == kind).ToList();

    // Real threads: each one blocks inside the gate, which would starve the thread pool.
    private static void OnThreads(int count, Action<int> work)
    {
        using var start = new Barrier(count);
        var threads = Enumerable.Range(0, count).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            work(i);
        })).ToList();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();
    }

    // AddPerson and SetFolders

    [Fact]
    public void AddPerson_CreatesAViewer()
    {
        var account = _service.AddPerson("  Anna ", []);

        Assert.Equal("Anna", account.Name);
        Assert.Equal(AccountRole.Viewer, account.Role);
        Assert.False(account.HasPassword);
        Assert.Empty(account.FolderIds!);
    }

    [Fact]
    public void SetFolders_RaisesAccessChanged()
    {
        var account = _service.AddPerson("Anna", null);
        var changed = new List<int>();
        _service.AccessChanged += changed.Add;

        _service.SetFolders(account.Id, []);

        Assert.Equal([account.Id], changed);
        Assert.Empty(_accounts.Get(account.Id)!.FolderIds!);
    }

    [Fact]
    public void SetFolders_RefusesOwner_RaisingNothing()
    {
        var changed = new List<int>();
        _service.AccessChanged += changed.Add;

        Assert.Throws<InvalidOperationException>(() => _service.SetFolders(LibraryDatabase.OwnerId, []));

        Assert.Empty(changed);
    }

    // Redeem

    [Fact]
    public void Redeem_NewAccount_SetsPassword()
    {
        var realService = NewService(PasswordWork.Real);
        var account = realService.AddPerson("Anna", null);
        var code = _invites.Create(account.Id);

        var result = realService.Redeem(code, Good, "Laptop", Address);

        Assert.Null(result.Failure);
        Assert.Equal(account.Id, result.Issued!.Device.UserId);
        Assert.Equal("Laptop", result.Issued.Device.Name);
        Assert.Equal(result.Issued.Device, _devices.ByToken(result.Issued.Token));
        Assert.True(Passwords.Verify(Good, _accounts.PasswordHash(account.Id)!));
        Assert.True(IsUsed(code));
        Assert.Equal(account.Id, Logged(SharingEvent.Joined).Single().UserId);
        Assert.NotNull(realService.SignIn("Anna", Good, "Phone", Address).Issued);
    }

    [Fact]
    public void Redeem_DeadInvite_Fails()
    {
        var result = _service.Redeem("no-such-code", Good, "Laptop", Address);

        Assert.Equal(RedeemFailure.InviteDead, result.Failure);
        Assert.Null(result.Issued);
    }

    [Fact]
    public void Redeem_InviteUsedOnce_FailsTheSecondTime()
    {
        var account = _service.AddPerson("Anna", null);
        var code = _invites.Create(account.Id);
        _service.Redeem(code, Good, "Laptop", Address);

        Assert.Equal(RedeemFailure.InviteDead, _service.Redeem(code, Good, "Phone", Address).Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nine char")]
    public void Redeem_ShortPassword_DoesNotConsume(string? password)
    {
        var account = _service.AddPerson("Anna", null);
        var code = _invites.Create(account.Id);

        var result = _service.Redeem(code, password, "Laptop", Address);

        Assert.Equal(RedeemFailure.PasswordTooShort, result.Failure);
        Assert.False(IsUsed(code));
        Assert.False(_accounts.Get(account.Id)!.HasPassword);
        Assert.NotNull(_service.Redeem(code, "ten chars!", "Laptop", Address).Issued);
    }

    [Fact]
    public void Redeem_KnownAccount_NeedsPassword()
    {
        var account = Joined();
        var code = _invites.Create(account.Id);

        var result = _service.Redeem(code, null, "Phone", Address);

        Assert.Equal(RedeemFailure.WrongPassword, result.Failure);
        Assert.False(IsUsed(code));
        Assert.Single(_devices.ForUser(account.Id));

        var second = _service.Redeem(code, Good, "Phone", Address);

        Assert.Equal(2, _devices.ForUser(account.Id).Count);
        Assert.Equal("Phone", second.Issued!.Device.Name);
        Assert.True(IsUsed(code));
        Assert.Equal("fake$" + Good, _accounts.PasswordHash(account.Id));
        Assert.Equal(account.Id, Logged(SharingEvent.SignedIn).Single().UserId);
    }

    [Fact]
    public void Redeem_KnownAccount_NoPassword_IsNotAGuess()
    {
        var account = Joined();
        var code = _invites.Create(account.Id);

        for (var i = 0; i < 10; i++) _service.Redeem(code, null, "Phone", Address);

        Assert.Equal(0, _checks);
        Assert.Empty(_guard.LockedNames());
    }

    [Fact]
    public void Redeem_WrongPassword_CountsTowardLock_AndDoesNotConsume()
    {
        var account = Joined();
        var code = _invites.Create(account.Id);

        for (var i = 0; i < 5; i++)
            Assert.Equal(RedeemFailure.WrongPassword, _service.Redeem(code, Bad, "Phone", Address).Failure);

        Assert.False(IsUsed(code));
        Assert.Equal(RedeemFailure.Locked, _service.Redeem(code, Good, "Phone", Address).Failure);
        Assert.False(IsUsed(code));
        Assert.Equal(["Anna"], _guard.LockedNames());
        Assert.Equal(RedeemFailure.Locked, _service.SignIn("Anna", Good, "Phone", Address).Failure);
    }

    [Fact]
    public void Redeem_DisabledAccount_Fails()
    {
        var account = Joined();
        var code = _invites.Create(account.Id);
        _service.Disable(account.Id, true);

        var result = _service.Redeem(code, Good, "Phone", Address);

        Assert.Equal(RedeemFailure.AccountDisabled, result.Failure);
        Assert.False(IsUsed(code));
    }

    [Fact]
    public void Redeem_NewAccount_IsAllOrNothing()
    {
        var account = _service.AddPerson("Anna", null);
        var code = _invites.Create(account.Id);
        using (var connection = _db.Open())
            connection.Execute("CREATE TRIGGER no_devices BEFORE INSERT ON devices BEGIN SELECT RAISE(ABORT, 'boom'); END");

        Assert.ThrowsAny<SqliteException>(() => _service.Redeem(code, Good, "Laptop", Address));

        Assert.False(IsUsed(code));
        Assert.False(_accounts.Get(account.Id)!.HasPassword);
    }

    [Fact]
    public void Redeem_TwoInvitesRacing_KeepTheFirstPassword()
    {
        var account = _service.AddPerson("Anna", null);
        var first = _invites.Create(account.Id);
        var second = _invites.Create(account.Id);
        _whileHashing = () =>
        {
            _whileHashing = () => { };
            _service.Redeem(second, "the password that won", "Phone", Address);
        };

        var result = _service.Redeem(first, "the password that lost", "Laptop", Address);

        Assert.Equal(RedeemFailure.WrongPassword, result.Failure);
        Assert.False(IsUsed(first));
        Assert.Equal("fake$the password that won", _accounts.PasswordHash(account.Id));
    }

    [Fact]
    public void Redeem_KnownAccount_ResetDuringCheck_IssuesNothing()
    {
        var account = Joined();
        var code = _invites.Create(account.Id);
        _whileChecking = () => _service.ResetPassword(account.Id);

        var result = _service.Redeem(code, Good, "Phone", Address);

        Assert.Null(result.Issued);
        Assert.Empty(_devices.ForUser(account.Id));
    }

    // SignIn

    [Fact]
    public void SignIn_Works_AndLogs()
    {
        var account = Joined();

        var result = _service.SignIn("Anna", Good, "Phone", Address);

        Assert.Null(result.Failure);
        Assert.Equal("Phone", result.Issued!.Device.Name);
        Assert.Equal(result.Issued.Device, _devices.ByToken(result.Issued.Token));
        var entry = Assert.Single(Logged(SharingEvent.SignedIn));
        Assert.Equal(account.Id, entry.UserId);
        Assert.Contains("Phone", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SignIn_UnknownName_SameFailureAsWrongPassword()
    {
        Joined();
        _checks = 0;

        var unknown = _service.SignIn("Nobody", Good, "Phone", Address);
        var checksForUnknown = _checks;
        var wrong = _service.SignIn("Anna", Bad, "Phone", Address);

        Assert.Equal(wrong, unknown);
        Assert.Equal(RedeemFailure.WrongPassword, unknown.Failure);
        Assert.Equal(1, checksForUnknown);
        Assert.Equal(2, _checks);
    }

    [Fact]
    public void SignIn_NoPasswordYet_FailsLikeAWrongPassword()
    {
        _service.AddPerson("Anna", null);

        var result = _service.SignIn("Anna", "", "Phone", Address);

        Assert.Equal(RedeemFailure.WrongPassword, result.Failure);
        Assert.Equal(1, _checks);
    }

    [Fact]
    public void SignIn_UnknownName_CountsTowardLock()
    {
        Trip("Nobody");

        Assert.Equal(RedeemFailure.Locked, _service.SignIn("Nobody", Good, "Phone", Address).Failure);
    }

    [Fact]
    public void SignIn_Disabled_Fails()
    {
        var account = Joined();
        _service.Disable(account.Id, true);
        _checks = 0;

        var right = _service.SignIn("Anna", Good, "Phone", Address);
        var wrong = _service.SignIn("Anna", Bad, "Phone", Address);

        Assert.Equal(RedeemFailure.AccountDisabled, right.Failure);
        Assert.Null(right.Issued);
        Assert.Equal(RedeemFailure.WrongPassword, wrong.Failure);
        Assert.Equal(2, _checks);
        Assert.Empty(_devices.ForUser(account.Id));
    }

    [Theory]
    [InlineData("  Anna ")]
    [InlineData("anna")]
    [InlineData("ANNA\t")]
    public void SignIn_UsesTheSameNormalisedName_ForAccountAndGuard(string name)
    {
        Joined();

        Assert.NotNull(_service.SignIn(name, Good, "Phone", Address).Issued);

        Trip(name);

        Assert.Equal(["Anna"], _guard.LockedNames(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(RedeemFailure.Locked, _service.SignIn("Anna", Good, "Phone", Address).Failure);
    }

    [Fact]
    public void SignIn_NameOverTheLimit_FailsLikeAWrongPassword_WithoutBecomingAGuardKey()
    {
        var name = new string('x', AccountStore.MaxNameLength + 1);

        for (var i = 0; i < 6; i++)
            Assert.Equal(RedeemFailure.WrongPassword, _service.SignIn(name, Bad, "Phone", Address).Failure);

        Assert.Equal(6, _checks);
        Assert.Empty(_guard.LockedNames());
    }

    [Fact]
    public void SignIn_NameAtTheLimit_Works()
    {
        var name = new string('x', AccountStore.MaxNameLength);
        Joined(name);

        Assert.NotNull(_service.SignIn(name, Good, "Phone", Address).Issued);
    }

    [Fact]
    public void SignIn_NameOverTheLimit_StillCountsAgainstTheAddress()
    {
        var name = new string('x', AccountStore.MaxNameLength + 1);

        for (var i = 0; i < 20; i++) _service.SignIn(name, Bad, "Phone", Address);

        Assert.Equal(RedeemFailure.Locked, _service.SignIn(name, Bad, "Phone", Address).Failure);
        Assert.Equal(RedeemFailure.WrongPassword, _service.SignIn(name, Bad, "Phone", "198.51.100.7").Failure);
    }

    [Fact]
    public void SignIn_Locked_DoesNotHash()
    {
        Joined();
        Trip("Anna");
        _checks = 0;

        var result = _service.SignIn("Anna", Good, "Phone", Address);

        Assert.Equal(RedeemFailure.Locked, result.Failure);
        Assert.Equal(0, _checks);
    }

    [Fact]
    public void SignIn_Success_ForgetsFailures()
    {
        Joined();
        for (var i = 0; i < 4; i++) _service.SignIn("Anna", Bad, "Phone", Address);

        _service.SignIn("Anna", Good, "Phone", Address);
        _service.SignIn("Anna", Bad, "Phone", Address);

        Assert.Empty(_guard.LockedNames());
    }

    [Fact]
    public void SignIn_ResetDuringCheck_IssuesNothing()
    {
        var account = Joined();
        _whileChecking = () => _service.ResetPassword(account.Id);

        var result = _service.SignIn("Anna", Good, "Phone", Address);

        Assert.Null(result.Issued);
        Assert.Empty(_devices.ForUser(account.Id));
    }

    [Fact]
    public void SignIn_DisableDuringCheck_IssuesNothing()
    {
        var account = Joined();
        _whileChecking = () => _service.Disable(account.Id, true);

        var result = _service.SignIn("Anna", Good, "Phone", Address);

        Assert.Null(result.Issued);
        Assert.Empty(_devices.ForUser(account.Id));
    }

    [Fact]
    public void SignIn_RemoveDuringCheck_IssuesNothing()
    {
        var account = Joined();
        _whileChecking = () => _service.Remove(account.Id);

        var result = _service.SignIn("Anna", Good, "Phone", Address);

        Assert.Null(result.Issued);
    }

    [Fact]
    public void SignIn_RemoveDuringTheFailureThatLocks_StillAnswersWrongPassword()
    {
        var account = Joined();
        for (var i = 0; i < 4; i++) _service.SignIn("Anna", Bad, "Phone", Address);
        _whileChecking = () =>
        {
            _whileChecking = () => { };
            _service.Remove(account.Id);
        };

        var result = _service.SignIn("Anna", Bad, "Phone", Address);

        Assert.Equal(RedeemFailure.WrongPassword, result.Failure);
        Assert.Null(Assert.Single(Logged(SharingEvent.SignInLocked)).UserId);
    }

    [Fact]
    public void SignIn_RemoveDuringARightPassword_StillAnswersWrongPassword()
    {
        var account = Joined();
        _whileChecking = () => _service.Remove(account.Id);

        Assert.Equal(RedeemFailure.WrongPassword, _service.SignIn("Anna", Good, "Phone", Address).Failure);
    }

    [Fact]
    public void Results_DoNotPrintTheToken()
    {
        var account = Joined();

        var signIn = _service.SignIn("Anna", Good, "Phone", Address);
        var redeem = _service.Redeem(_invites.Create(account.Id), Good, "Tablet", Address);

        Assert.DoesNotContain(signIn.Issued!.Token, signIn.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(redeem.Issued!.Token, redeem.ToString(), StringComparison.Ordinal);
    }

    // The gate

    [Fact]
    public void ParallelGuesses_NeverExceedTheLimit()
    {
        Joined();
        _checks = 0;
        _whileChecking = () => Thread.Sleep(5);

        OnThreads(40, _ => _service.SignIn("Anna", Bad, "Phone", Address));

        Assert.Equal(5, _checks);
        Assert.Equal(RedeemFailure.Locked, _service.SignIn("Anna", Good, "Phone", Address).Failure);
    }

    [Fact]
    public void ParallelGuesses_ThroughAnInvite_NeverExceedTheLimit()
    {
        var account = Joined();
        var code = _invites.Create(account.Id);
        _checks = 0;
        _whileChecking = () => Thread.Sleep(5);

        OnThreads(40, _ => _service.Redeem(code, Bad, "Phone", Address));

        Assert.Equal(5, _checks);
        Assert.False(IsUsed(code));
    }

    [Fact]
    public void Overflow_IsRefusedWithoutHashing()
    {
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _whileChecking = () =>
        {
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        };
        var holder = new Thread(() => _service.SignIn("Holder", Bad, "Phone", Address));
        holder.Start();
        Assert.True(inside.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // The holder is the one inside, so 8 of these wait and the other 12 must come straight back refused.
        var results = new RedeemFailure?[20];
        var refused = 0;
        var threads = Enumerable.Range(0, results.Length).Select(i => new Thread(() =>
        {
            results[i] = _service.SignIn("Guest" + i, Bad, "Phone", Address).Failure;
            if (results[i] == RedeemFailure.Busy) Interlocked.Increment(ref refused);
        })).ToList();
        foreach (var thread in threads) thread.Start();

        var allRefused = SpinWait.SpinUntil(() => Volatile.Read(ref refused) == 12, TimeSpan.FromSeconds(10));
        var checksBeforeRelease = Volatile.Read(ref _checks);
        release.Set();
        holder.Join();
        foreach (var thread in threads) thread.Join();

        Assert.True(allRefused);
        Assert.Equal(1, checksBeforeRelease);
        Assert.Equal(9, _checks);
        Assert.Equal(12, results.Count(r => r == RedeemFailure.Busy));
        Assert.Equal(8, results.Count(r => r == RedeemFailure.WrongPassword));
        Assert.Empty(Logged(SharingEvent.SignInLocked));
    }

    [Fact]
    public async Task Locked_IsSaidWithoutWaitingInTheQueue()
    {
        Trip("Anna");
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _whileChecking = () =>
        {
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        };
        var holder = new Thread(() => _service.SignIn("Guest", Bad, "Phone", "198.51.100.1"));
        holder.Start();
        Assert.True(inside.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        try
        {
            // The gate is taken, so an answer in time can only have come from ahead of it.
            var answered = await Task.Run(() => _service.SignIn("Anna", Good, "Phone", "203.0.113.2"), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(RedeemFailure.Locked, answered.Failure);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    [Fact]
    public void Owner_WithNoPassword_FailsLikeAWrongPassword()
    {
        var owner = _accounts.Get(LibraryDatabase.OwnerId)!;

        var result = _service.SignIn(owner.Name, Good, "Phone", Address);

        Assert.Null(result.Issued);
        Assert.Equal(RedeemFailure.WrongPassword, result.Failure);
    }

    [Fact]
    public void NewPasswords_HashTwoAtOnce_AndPastTheQueueItIsBusy()
    {
        var account = _service.AddPerson("Anna", null);
        var code = _invites.Create(account.Id);
        var hashing = 0;
        var most = 0;
        using var release = new ManualResetEventSlim();
        _whileHashing = () =>
        {
            var now = Interlocked.Increment(ref hashing);
            lock (release) most = Math.Max(most, now);
            release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Interlocked.Decrement(ref hashing);
        };

        // Two hash and 8 wait; the other 6 must come straight back.
        var busy = 0;
        var threads = Enumerable.Range(0, 16).Select(_ => new Thread(() =>
        {
            if (_service.Redeem(code, Good, "Phone", Address).Failure == RedeemFailure.Busy) Interlocked.Increment(ref busy);
        })).ToList();
        foreach (var thread in threads) thread.Start();

        var allBusy = SpinWait.SpinUntil(() => Volatile.Read(ref busy) == 6, TimeSpan.FromSeconds(10));
        release.Set();
        foreach (var thread in threads) thread.Join();

        Assert.True(allBusy);
        Assert.Equal(2, most);
    }

    // What the log may hold

    [Fact]
    public void Lock_OnAKnownAccount_LogsTheAccountId_NotTheSubmittedName()
    {
        var account = Joined();

        Trip("  ANNA ");

        var entry = Assert.Single(Logged(SharingEvent.SignInLocked));
        Assert.Equal(account.Id, entry.UserId);
        Assert.DoesNotContain("ANNA", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Lock_OnAnUnknownName_LogsNothingIdentifying()
    {
        Trip(PasswordInNameBox);

        var entry = Assert.Single(Logged(SharingEvent.SignInLocked));
        Assert.Null(entry.UserId);
        Assert.Empty(entry.Detail);
    }

    [Fact]
    public void Lock_IsLoggedOnce_SoGuessesCantWashOutTheLog()
    {
        Trip(PasswordInNameBox);

        for (var i = 0; i < 50; i++) _service.SignIn(PasswordInNameBox, Bad, "Phone", "198.51.100.7");

        Assert.Single(Logged(SharingEvent.SignInLocked));
    }

    [Fact]
    public void FailedAttempts_LeaveNoRowsBelowTheLimit()
    {
        for (var i = 0; i < 4; i++) _service.SignIn(PasswordInNameBox, Bad, PasswordInNameBox, Address);

        Assert.Empty(_log.Recent(100));
    }

    // Disable

    [Fact]
    public void Disable_RevokesDevices_AndRaisesAccountClosed()
    {
        var account = Joined();
        var device = SignedIn(account);
        var closed = new List<int>();
        _service.AccountClosed += closed.Add;

        _service.Disable(account.Id, true);

        Assert.True(_accounts.Get(account.Id)!.Disabled);
        Assert.Null(_devices.ByToken(device.Token));
        Assert.Empty(_devices.ForUser(account.Id));
        Assert.Equal([account.Id], closed);
        Assert.Equal(account.Id, Logged(SharingEvent.Disabled).Single().UserId);
    }

    [Fact]
    public void Disable_RaisesAccountClosed_AfterTheTokensAreDead()
    {
        var account = Joined();
        var device = SignedIn(account);
        Device? seen = device.Device;
        _service.AccountClosed += _ => seen = _devices.ByToken(device.Token);

        _service.Disable(account.Id, true);

        Assert.Null(seen);
    }

    [Fact]
    public void Disable_Undone_LetsThePersonBackIn_RaisingNothing()
    {
        var account = Joined();
        _service.Disable(account.Id, true);
        var closed = new List<int>();
        _service.AccountClosed += closed.Add;

        _service.Disable(account.Id, false);

        Assert.False(_accounts.Get(account.Id)!.Disabled);
        Assert.Empty(closed);
        Assert.Single(Logged(SharingEvent.Disabled));
        Assert.NotNull(_service.SignIn("Anna", Good, "Phone", Address).Issued);
    }

    [Fact]
    public void Disable_Owner_IsRefused_LeavingDevicesAlone()
    {
        _service.SetOwnerPassword(Good);
        var device = _service.SignIn("Owner", Good, "Laptop", Address).Issued!;
        var closed = new List<int>();
        _service.AccountClosed += closed.Add;

        Assert.Throws<InvalidOperationException>(() => _service.Disable(LibraryDatabase.OwnerId, true));

        Assert.NotNull(_devices.ByToken(device.Token));
        Assert.Empty(closed);
        Assert.Empty(Logged(SharingEvent.Disabled));
    }

    [Fact]
    public void Disable_UnknownAccount_DoesNothing()
    {
        var closed = new List<int>();
        _service.AccountClosed += closed.Add;

        _service.Disable(999, true);

        Assert.Empty(closed);
        Assert.Empty(_log.Recent(10));
    }

    // Reset password

    [Fact]
    public void ResetPassword_ClearsHash_RevokesDevices()
    {
        var account = Joined();
        var device = SignedIn(account);
        var closed = new List<int>();
        _service.AccountClosed += closed.Add;

        _service.ResetPassword(account.Id);

        Assert.False(_accounts.Get(account.Id)!.HasPassword);
        Assert.Null(_devices.ByToken(device.Token));
        Assert.Equal([account.Id], closed);
        Assert.Equal(account.Id, Assert.Single(Logged(SharingEvent.PasswordReset)).UserId);
        Assert.Empty(Logged(SharingEvent.SignedOut));
        Assert.Equal(RedeemFailure.WrongPassword, _service.SignIn("Anna", Good, "Phone", Address).Failure);
    }

    [Fact]
    public void ResetPassword_KillsOlderInvites_SoTheyCantSetThePassword()
    {
        var account = Joined();
        var old = _invites.Create(account.Id);

        _service.ResetPassword(account.Id);

        Assert.Equal(RedeemFailure.InviteDead, _service.Redeem(old, "a stranger's password", "Phone", Address).Failure);
        Assert.False(_accounts.Get(account.Id)!.HasPassword);
    }

    [Fact]
    public void ResetPassword_ThenNewInvite_SetsANewPassword()
    {
        var account = Joined();
        _service.ResetPassword(account.Id);

        var result = _service.Redeem(_invites.Create(account.Id), "a brand new password", "Laptop", Address);

        Assert.NotNull(result.Issued);
        Assert.Equal("fake$a brand new password", _accounts.PasswordHash(account.Id));
    }

    // Sign out

    [Fact]
    public void SignOutDevice_PersonCanSignBackIn()
    {
        var account = Joined();
        var device = SignedIn(account);

        _service.SignOutDevice(device.Device.Id);

        Assert.Null(_devices.ByToken(device.Token));
        Assert.False(_accounts.Get(account.Id)!.Disabled);
        var entry = Assert.Single(Logged(SharingEvent.SignedOut));
        Assert.Equal(account.Id, entry.UserId);
        Assert.Contains("Laptop", entry.Detail, StringComparison.Ordinal);
        Assert.NotNull(_service.SignIn("Anna", Good, "Laptop", Address).Issued);
    }

    [Fact]
    public void SignOutDevice_KeepsOtherDevices()
    {
        var account = Joined();
        var device = SignedIn(account);

        _service.SignOutDevice(device.Device.Id);

        Assert.Equal(["First"], _devices.ForUser(account.Id).Select(d => d.Name));
    }

    [Fact]
    public void SignOutDevice_RaisesDeviceSignedOut_AfterTheTokenIsDead_LeavingTheAccountOpen()
    {
        var account = Joined();
        var device = SignedIn(account);
        var signedOut = new List<int>();
        var closed = new List<int>();
        Device? seen = device.Device;
        _service.DeviceSignedOut += id =>
        {
            signedOut.Add(id);
            seen = _devices.ByToken(device.Token);
        };
        _service.AccountClosed += closed.Add;

        _service.SignOutDevice(device.Device.Id);

        Assert.Equal([device.Device.Id], signedOut);
        Assert.Null(seen);
        Assert.Empty(closed);
    }

    [Fact]
    public void SignOutDevice_Unknown_DoesNothing()
    {
        var signedOut = new List<int>();
        _service.DeviceSignedOut += signedOut.Add;

        _service.SignOutDevice(999);

        Assert.Empty(signedOut);
        Assert.Empty(_log.Recent(10));
    }

    // Remove

    [Fact]
    public void Remove_DeletesTheAccount_EndsSessions_AndKeepsALogEntry()
    {
        var account = Joined();
        var device = SignedIn(account);
        var closed = new List<int>();
        _service.AccountClosed += closed.Add;

        _service.Remove(account.Id);

        Assert.Null(_accounts.Get(account.Id));
        Assert.Null(_devices.ByToken(device.Token));
        Assert.Equal([account.Id], closed);
        var entry = Assert.Single(Logged(SharingEvent.Removed));
        Assert.Null(entry.UserId);
        Assert.Contains("Anna", entry.Detail, StringComparison.Ordinal);
        Assert.Empty(Logged(SharingEvent.SignedOut));
    }

    [Fact]
    public void Remove_Owner_IsRefused_WithoutALogEntry()
    {
        var closed = new List<int>();
        _service.AccountClosed += closed.Add;

        Assert.Throws<InvalidOperationException>(() => _service.Remove(LibraryDatabase.OwnerId));

        Assert.Empty(closed);
        Assert.Empty(_log.Recent(10));
    }

    [Fact]
    public void Remove_Unknown_DoesNothing()
    {
        _service.Remove(999);

        Assert.Empty(_log.Recent(10));
    }

    // Passwords

    [Fact]
    public void SetOwnerPassword_LetsTheOwnerSignIn()
    {
        _service.SetOwnerPassword(Good);

        Assert.NotNull(_service.SignIn("Owner", Good, "Laptop", Address).Issued);
    }

    [Fact]
    public void SetOwnerPassword_TooShort_IsRefused()
    {
        var refused = Assert.Throws<PasswordRefusedException>(() => _service.SetOwnerPassword("nine char"));

        Assert.Equal(RedeemFailure.PasswordTooShort, refused.Failure);
        Assert.False(_accounts.Get(LibraryDatabase.OwnerId)!.HasPassword);
    }

    [Fact]
    public void ChangePassword_SwapsThePassword_KeepingOnlyTheDeviceThatChangedIt()
    {
        var account = Joined();
        var mine = SignedIn(account);
        var other = SignedIn(account);
        var signedOut = new List<int>();
        _service.DeviceSignedOut += signedOut.Add;

        _service.ChangePassword(account.Id, Good, "a brand new password", mine.Device.Id);

        Assert.Equal(RedeemFailure.WrongPassword, _service.SignIn("Anna", Good, "Phone", Address).Failure);
        Assert.NotNull(_service.SignIn("Anna", "a brand new password", "Phone", Address).Issued);
        Assert.NotNull(_devices.ByToken(mine.Token));
        Assert.Null(_devices.ByToken(other.Token));
        Assert.Contains(other.Device.Id, signedOut);
        Assert.DoesNotContain(mine.Device.Id, signedOut);
        Assert.Contains(Logged(SharingEvent.SignedOut), e => e.UserId == account.Id);
    }

    [Fact]
    public void SigningIn_KeepsTheNewest20Devices_AndSignsOutTheOldest()
    {
        var account = Joined();
        var oldest = _devices.ForUser(account.Id).Single();
        var signedOut = new List<int>();
        _service.DeviceSignedOut += signedOut.Add;

        for (var i = 0; i < 20; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            SignedIn(account);
        }

        Assert.Equal(20, _devices.ForUser(account.Id).Count);
        Assert.Equal([oldest.Id], signedOut);
    }

    [Fact]
    public void SigningIn_TheNewDeviceStays_EvenWhenTheClockWentBack()
    {
        var account = Joined();
        for (var i = 0; i < 19; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            SignedIn(account);
        }

        _clock.Advance(TimeSpan.FromHours(-2));
        var late = SignedIn(account);

        Assert.Equal(20, _devices.ForUser(account.Id).Count);
        Assert.NotNull(_devices.ByToken(late.Token));
    }

    [Fact]
    public void Redeeming_AlsoKeepsTheNewest20Devices()
    {
        var account = Joined();
        for (var i = 0; i < 25; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.NotNull(_service.Redeem(_invites.Create(account.Id), Good, "Phone", Address).Issued);
        }

        Assert.Equal(20, _devices.ForUser(account.Id).Count);
    }

    [Fact]
    public void SigningIn_FromOneDevice_LogsOnceADay()
    {
        var account = Joined();

        SignedIn(account);
        SignedIn(account);
        Assert.Single(Logged(SharingEvent.SignedIn));

        _clock.Advance(TimeSpan.FromHours(24));
        SignedIn(account);
        Assert.Equal(2, Logged(SharingEvent.SignedIn).Count);
    }

    [Fact]
    public void ChangePassword_ARefusedOne_SignsNobodyOut()
    {
        var account = Joined();
        var other = SignedIn(account);

        Assert.Throws<PasswordRefusedException>(() => _service.ChangePassword(account.Id, Bad, "a brand new password"));

        Assert.NotNull(_devices.ByToken(other.Token));
    }

    [Fact]
    public void ChangePassword_LeavesOtherPeoplesDevicesAlone()
    {
        var anna = Joined();
        var ben = Joined("Ben");
        var bens = SignedIn(ben);

        _service.ChangePassword(anna.Id, Good, "a brand new password");

        Assert.NotNull(_devices.ByToken(bens.Token));
    }

    [Fact]
    public void SetOwnerPassword_SignsOutTheOwnersDevices()
    {
        _service.SetOwnerPassword(Good);
        var device = _devices.Issue(LibraryDatabase.OwnerId, "Laptop", Address);
        var signedOut = new List<int>();
        _service.DeviceSignedOut += signedOut.Add;

        _service.SetOwnerPassword("a brand new password");

        Assert.Null(_devices.ByToken(device.Token));
        Assert.Equal([device.Device.Id], signedOut);
    }

    [Fact]
    public void ChangePassword_TooShort_IsRefused()
    {
        var account = Joined();

        var refused = Assert.Throws<PasswordRefusedException>(() => _service.ChangePassword(account.Id, Good, "nine char"));

        Assert.Equal(RedeemFailure.PasswordTooShort, refused.Failure);
        Assert.Equal("fake$" + Good, _accounts.PasswordHash(account.Id));
    }

    [Fact]
    public void ChangePassword_WrongCurrent_IsRefused()
    {
        var account = Joined();

        var refused = Assert.Throws<PasswordRefusedException>(() => _service.ChangePassword(account.Id, Bad, "a brand new password"));

        Assert.Equal(RedeemFailure.WrongPassword, refused.Failure);
        Assert.Equal("fake$" + Good, _accounts.PasswordHash(account.Id));
    }

    [Fact]
    public void ChangePassword_WrongCurrent_CountsTowardLock()
    {
        var account = Joined();

        for (var i = 0; i < 5; i++)
            Assert.Throws<PasswordRefusedException>(() => _service.ChangePassword(account.Id, Bad, "a brand new password"));

        var refused = Assert.Throws<PasswordRefusedException>(() => _service.ChangePassword(account.Id, Good, "a brand new password"));
        Assert.Equal(RedeemFailure.Locked, refused.Failure);
        Assert.Equal(["Anna"], _guard.LockedNames());
    }

    [Fact]
    public void ChangePassword_ResetDuringCheck_DoesNotUndoTheReset()
    {
        var account = Joined();
        _whileChecking = () => _service.ResetPassword(account.Id);

        Assert.Throws<PasswordRefusedException>(() => _service.ChangePassword(account.Id, Good, "a brand new password"));

        Assert.False(_accounts.Get(account.Id)!.HasPassword);
    }
}
