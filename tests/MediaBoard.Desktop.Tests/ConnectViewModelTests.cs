using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop.Tests;

/// <summary>The Connect screen against a real server on loopback: the only stand-in is the restart.</summary>
public sealed class ConnectViewModelTests
{
    private const string Password = "correct horse battery";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Holds whatever a task posts back, so a test can act between the answer arriving and the code resuming.</summary>
    private sealed class HeldContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _posted = [];

        public override void Post(SendOrPostCallback d, object? state) => _posted.Add((d, state));

        public void WaitForPost()
        {
            Assert.True(SpinWait.SpinUntil(() => _posted.Count > 0, TimeSpan.FromSeconds(30)));
        }

        public void RunUntil(Task task)
        {
            while (!task.IsCompleted)
            {
                if (_posted.TryTake(out var next, 100)) next.Callback(next.State);
            }
        }
    }

    private sealed class Room : IAsyncDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("mediaboard-connect-").FullName;
        private readonly List<ConnectViewModel> _opened = [];

        private Room(SharingWorld world)
        {
            World = world;
            Viewer = new SettingsStore(SettingsPath);
        }

        public SharingWorld World { get; }

        public SettingsStore Viewer { get; }

        public string SettingsPath => Path.Combine(_dir, "settings.json");

        public int Restarts { get; private set; }

        public bool RestartWorks { get; set; } = true;

        public string Address => $"127.0.0.1:{World.Port}";

        public static async Task<Room> OpenAsync()
        {
            var world = new SharingWorld();
            world.OpenDatabase();
            world.SetSharing(s => s with { HomeAddress = "127.0.0.1" });
            await world.Sharing.SetOnAsync(true);
            return new Room(world);
        }

        public Account Person(string name = "Sam") => World.Accounts.AddPerson(name, null);

        public InvitePayload Invite(Account account)
        {
            Assert.True(InviteString.TryDecode(World.Sharing.CreateInvite(account.Id), out var payload));
            return payload;
        }

        /// <summary>A person who has set a password on another device.</summary>
        public Account KnownPerson(string name = "Sam")
        {
            var account = Person(name);
            Assert.NotNull(World.Accounts.Redeem(Invite(account).Code, Password, "Phone", "198.51.100.1").Issued);
            return account;
        }

        public ConnectViewModel Connect(StoredConnection? saved = null)
        {
            var connect = new ConnectViewModel(Viewer, "Laptop", () =>
            {
                Restarts++;
                return RestartWorks;
            }, saved);
            _opened.Add(connect);
            return connect;
        }

        public StoredConnection SignedOut(string name = "Sam") =>
            new(new ServerEndpoint("Library", [Address], Invite(Person("Throwaway" + Guid.NewGuid().ToString("N")[..8])).Fingerprint), name, "Laptop", null);

        public async ValueTask DisposeAsync()
        {
            foreach (var connect in _opened) connect.Dispose();
            await World.DisposeAsync();
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static Task Paste(ConnectViewModel connect, InvitePayload invite) => Paste(connect, InviteString.Encode(invite));

    private static Task Paste(ConnectViewModel connect, string text)
    {
        connect.InviteText = text;
        return connect.ContinueCommand.ExecuteAsync(null);
    }

    private static Task SubmitPassword(ConnectViewModel connect, string password, string? repeat = null)
    {
        connect.Password = password;
        connect.Repeat = repeat ?? password;
        return connect.ContinueCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task BadInvite_Message()
    {
        await using var room = await Room.OpenAsync();
        var connect = room.Connect();

        await Paste(connect, "mb1-this-is-not-one");

        Assert.Equal(ConnectStep.Invite, connect.Step);
        Assert.Contains("mb1-", connect.Message, StringComparison.Ordinal);
        Assert.False(connect.CanRetry);
        Assert.Equal(0, room.Restarts);
    }

    [Fact]
    public async Task DeadInvite_Message()
    {
        await using var room = await Room.OpenAsync();
        var connect = room.Connect();

        await Paste(connect, room.Invite(room.Person()) with { Code = "already-used" });

        Assert.Equal(ConnectStep.Invite, connect.Step);
        Assert.Equal("This invite no longer works. Ask for a new one.", connect.Message);
        Assert.False(connect.CanRetry);
    }

    [Fact]
    public async Task NewAccount_AsksPassword_Twice_Mismatch()
    {
        await using var room = await Room.OpenAsync();
        var account = room.Person();
        var connect = room.Connect();
        var invite = room.Invite(account);

        await Paste(connect, invite);

        Assert.Equal(ConnectStep.NewPassword, connect.Step);
        Assert.Contains(invite.ServerName, connect.Note, StringComparison.Ordinal);

        await SubmitPassword(connect, Password, "correct horse batterz");

        Assert.Equal("The passwords don't match.", connect.Message);
        Assert.Equal(ConnectStep.NewPassword, connect.Step);
        Assert.Equal(0, room.Restarts);
        Assert.Null(room.World.Accounts.Redeem(invite.Code, "wrong", "Phone", "198.51.100.1").Issued);

        await SubmitPassword(connect, Password);

        Assert.Equal(1, room.Restarts);
    }

    [Fact]
    public async Task NewAccount_ShortPassword_StaysWithoutAsking()
    {
        await using var room = await Room.OpenAsync();
        var connect = room.Connect();
        await Paste(connect, room.Invite(room.Person()));

        await SubmitPassword(connect, "short");

        Assert.Contains("10", connect.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectStep.NewPassword, connect.Step);
    }

    [Fact]
    public async Task KnownAccount_AsksPassword()
    {
        await using var room = await Room.OpenAsync();
        var account = room.KnownPerson();
        var connect = room.Connect();

        await Paste(connect, room.Invite(account));

        Assert.Equal(ConnectStep.KnownPassword, connect.Step);

        await SubmitPassword(connect, "not the password");

        Assert.Equal("Wrong password. Try again.", connect.Message);
        Assert.Equal("", connect.Password);
        Assert.Equal(0, room.Restarts);

        await SubmitPassword(connect, Password);

        Assert.Equal(1, room.Restarts);
    }

    [Fact]
    public async Task SignedOutDevice_ShowsNameAndPassword()
    {
        await using var room = await Room.OpenAsync();
        room.KnownPerson();
        var connect = room.Connect(room.SignedOut());

        Assert.Equal(ConnectStep.SignIn, connect.Step);
        Assert.Equal("Sam", connect.Name);
        Assert.Equal("", connect.Password);
        Assert.Contains("Library", connect.Note, StringComparison.Ordinal);

        await SubmitPassword(connect, Password);

        Assert.Equal(1, room.Restarts);
        var saved = room.Viewer.Load();
        Assert.Equal(AppMode.Server, saved.Mode);
        Assert.NotNull(saved.Connection?.ProtectedToken);
    }

    [Fact]
    public async Task SignedOutDevice_WrongPassword_SaysNameOrPassword()
    {
        await using var room = await Room.OpenAsync();
        room.KnownPerson();
        var connect = room.Connect(room.SignedOut());

        await SubmitPassword(connect, "not the password");

        Assert.Equal("Wrong name or password.", connect.Message);
        Assert.Equal("", connect.Password);
    }

    [Fact]
    public async Task SignedOutDevice_UseAnInvite_GoesToTheInviteStep()
    {
        await using var room = await Room.OpenAsync();
        var connect = room.Connect(room.SignedOut());
        connect.Password = Password;

        connect.UseInviteCommand.Execute(null);

        Assert.Equal(ConnectStep.Invite, connect.Step);
        Assert.Equal("", connect.Password);
    }

    [Theory]
    [InlineData("zz")]
    [InlineData("not hex at all, and long enough to be read as a fingerprint if nobody looked at it")]
    public async Task SavedServer_WithADamagedFingerprint_AsksForAnInvite(string fingerprint)
    {
        await using var room = await Room.OpenAsync();
        var damaged = new StoredConnection(new ServerEndpoint("Library", [room.Address], fingerprint), "Sam", "Laptop", "blob");

        var connect = room.Connect(damaged);

        Assert.Equal(ConnectStep.Invite, connect.Step);
        Assert.Contains("new invite", connect.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locked_Message()
    {
        await using var room = await Room.OpenAsync();
        room.KnownPerson();
        var guard = room.World.Services.GetRequiredService<SignInGuard>();
        for (var attempt = 0; attempt < 5; attempt++) guard.RecordFailure("Sam", null);
        var connect = room.Connect(room.SignedOut());

        await SubmitPassword(connect, Password);

        Assert.Equal("Too many tries. Wait 15 minutes.", connect.Message);
        Assert.False(connect.CanRetry);
    }

    [Fact]
    public async Task Disabled_Message()
    {
        await using var room = await Room.OpenAsync();
        var account = room.Person();
        var invite = room.Invite(account);
        room.World.Accounts.Disable(account.Id, true);
        var connect = room.Connect();

        await Paste(connect, invite);

        Assert.Contains("disabled", connect.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_SavesConnection_AndRestarts()
    {
        await using var room = await Room.OpenAsync();
        var connect = room.Connect();
        var invite = room.Invite(room.Person());
        await Paste(connect, invite);

        await SubmitPassword(connect, Password);

        Assert.Equal(1, room.Restarts);
        var saved = room.Viewer.Load();
        Assert.Equal(AppMode.Server, saved.Mode);
        var connection = saved.Connection!;
        Assert.Equal(invite.Addresses, connection.Endpoint.Addresses);
        Assert.Equal(invite.Fingerprint, connection.Endpoint.Fingerprint);
        Assert.Equal(invite.ServerName, connection.Endpoint.ServerName);
        Assert.Equal("Sam", connection.AccountName);
        Assert.Equal("Laptop", connection.DeviceName);

        var token = RemoteSession.TryUnprotect(connection.ProtectedToken);
        using var client = new ApiClient(connection.Endpoint, () => token, _ => { });
        Assert.Equal("Sam", (await client.GetAsync<MeReply>("me", Ct)).Name);

        Assert.Equal("", connect.Password);
        Assert.Equal("", connect.Repeat);
        Assert.Equal("", connect.InviteText);
        var written = await File.ReadAllTextAsync(room.SettingsPath, Ct);
        Assert.DoesNotContain(Password, written, StringComparison.Ordinal);
        Assert.DoesNotContain(token!, written, StringComparison.Ordinal);
        Assert.DoesNotContain(invite.Code, written, StringComparison.Ordinal);
    }

    // The invite is spent by now, so the token is all that's left of it.
    [Fact]
    public async Task SaveFails_RetryFinishes_WithoutTheInviteOrPassword()
    {
        await using var room = await Room.OpenAsync();
        room.Viewer.Save(new AppSettings());
        var connect = room.Connect();
        await Paste(connect, room.Invite(room.Person()));

        using (new FileStream(room.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            await SubmitPassword(connect, Password);

            Assert.True(connect.CanRetry);
            Assert.Equal(0, room.Restarts);
            Assert.Equal("", connect.Password);
        }

        await connect.ContinueCommand.ExecuteAsync(null);

        Assert.Null(connect.Message);
        Assert.Equal(1, room.Restarts);
        Assert.Equal(AppMode.Server, room.Viewer.Load().Mode);
    }

    [Fact]
    public async Task RestartFails_SaysToStartItAgain()
    {
        await using var room = await Room.OpenAsync();
        room.RestartWorks = false;
        var connect = room.Connect();
        await Paste(connect, room.Invite(room.Person()));

        await SubmitPassword(connect, Password);

        Assert.Contains("Restart MediaBoard", connect.Message, StringComparison.Ordinal);
        Assert.False(connect.CanRetry);
        Assert.Equal(AppMode.Server, room.Viewer.Load().Mode);
    }

    // By normal clicks, and the screen names the first server all the while.
    [Fact]
    public async Task SignIn_AfterOpeningAnotherServersInvite_GoesToTheSavedServer()
    {
        await using var saved = await Room.OpenAsync();
        await using var other = await Room.OpenAsync();
        saved.KnownPerson();
        var otherGuard = other.World.Services.GetRequiredService<SignInGuard>();
        for (var attempt = 0; attempt < 4; attempt++) otherGuard.RecordFailure("Sam", null);
        var connection = saved.SignedOut();
        var connect = saved.Connect(connection);

        connect.UseInviteCommand.Execute(null);
        await Paste(connect, other.Invite(other.Person("Anna")));
        Assert.Equal(ConnectStep.NewPassword, connect.Step);
        connect.BackCommand.Execute(null);
        connect.BackCommand.Execute(null);
        Assert.Equal(ConnectStep.SignIn, connect.Step);

        await SubmitPassword(connect, Password);

        // One more failure would have locked the name there.
        Assert.False(otherGuard.IsLocked("Sam", null));
        Assert.Equal(1, saved.Restarts);
        Assert.Equal(connection.Endpoint.Fingerprint, saved.Viewer.Load().Connection!.Endpoint.Fingerprint);
    }

    // The invite is spent, and another one pasted now would be ignored while the app restarted into this server.
    [Fact]
    public async Task HeldToken_LeavesOnlyRetry()
    {
        await using var room = await Room.OpenAsync();
        room.Viewer.Save(new AppSettings());
        var connect = room.Connect();
        var invite = room.Invite(room.Person());
        await Paste(connect, invite);
        Assert.True(connect.BackCommand.CanExecute(null));

        using (new FileStream(room.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            await SubmitPassword(connect, Password);

            Assert.True(connect.CanRetry);
            Assert.False(connect.BackCommand.CanExecute(null));
            Assert.False(connect.UseInviteCommand.CanExecute(null));
            Assert.True(connect.ContinueCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task ClosingAsTheAnswerArrives_KeepsTheToken_AndStaysClosed()
    {
        await using var room = await Room.OpenAsync();
        var connect = room.Connect();
        await Paste(connect, room.Invite(room.Person()));
        connect.Password = Password;
        connect.Repeat = Password;
        var outer = SynchronizationContext.Current;
        var held = new HeldContext();
        SynchronizationContext.SetSynchronizationContext(held);
        try
        {
            var running = connect.ContinueCommand.ExecuteAsync(null);
            held.WaitForPost();

            connect.Dispose();

            held.RunUntil(running);
            await running;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(outer);
        }

        Assert.Equal(0, room.Restarts);
        var saved = room.Viewer.Load();
        Assert.Equal(AppMode.Server, saved.Mode);
        Assert.Equal("Sam", saved.Connection?.AccountName);
        Assert.NotNull(saved.Connection?.ProtectedToken);
    }

    [Fact]
    public async Task CantReach_ShowsRetry()
    {
        await using var room = await Room.OpenAsync();
        var connect = room.Connect();
        var invite = room.Invite(room.Person());
        await room.World.Sharing.SetOnAsync(false);

        await Paste(connect, invite);

        Assert.Equal(
            $"Can't reach {invite.ServerName}. Their PC may be off or asleep, or sharing is off.",
            connect.Message);
        Assert.True(connect.CanRetry);
        Assert.Equal("Retry", connect.ActionLabel);

        await room.World.Sharing.SetOnAsync(true);
        await connect.ContinueCommand.ExecuteAsync(null);

        Assert.Equal(ConnectStep.NewPassword, connect.Step);
        Assert.Null(connect.Message);
        Assert.False(connect.CanRetry);
        Assert.NotEqual("Retry", connect.ActionLabel);
    }

    [Fact]
    public async Task IdentityChanged_ShowsRetry()
    {
        await using var room = await Room.OpenAsync();
        var connect = room.Connect();

        await Paste(connect, room.Invite(room.Person()) with { Fingerprint = new string('A', 64) });

        Assert.Equal("This server's identity changed. Ask for a new invite.", connect.Message);
        Assert.True(connect.CanRetry);
    }

    [Fact]
    public async Task UsesHomeAddressThenPublic()
    {
        await using var room = await Room.OpenAsync();
        var home = new TcpListener(IPAddress.Loopback, 0);
        home.Start();
        var homeAddress = $"127.0.0.1:{((IPEndPoint)home.LocalEndpoint).Port}";
        var tried = home.AcceptTcpClientAsync(Ct).AsTask();

        // Taken, then dropped: the home address is there first, doesn't work, and the public one still gets its turn.
        var dropping = tried.ContinueWith(accepted => accepted.Result.Dispose(), Ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        var connect = room.Connect();
        var invite = room.Invite(room.Person()) with { Addresses = [homeAddress, room.Address] };

        await Paste(connect, invite);

        Assert.True(tried.IsCompletedSuccessfully);
        await dropping;
        home.Stop();
        Assert.Equal(ConnectStep.NewPassword, connect.Step);

        await SubmitPassword(connect, Password);

        Assert.Equal([homeAddress, room.Address], room.Viewer.Load().Connection!.Endpoint.Addresses);
    }

    [Fact]
    public async Task Back_FromAPasswordStep_ForgetsThePassword()
    {
        await using var room = await Room.OpenAsync();
        var connect = room.Connect();
        await Paste(connect, room.Invite(room.Person()));
        connect.Password = Password;
        connect.Repeat = Password;

        connect.BackCommand.Execute(null);

        Assert.Equal(ConnectStep.Invite, connect.Step);
        Assert.Equal("", connect.Password);
        Assert.Equal("", connect.Repeat);
    }

    [Fact]
    public async Task Back_FromTheInviteStep_Leaves()
    {
        await using var room = await Room.OpenAsync();
        var connect = room.Connect();
        var left = 0;
        connect.Left += () => left++;

        connect.BackCommand.Execute(null);

        Assert.Equal(1, left);
    }

    [Fact]
    public async Task Dispose_ForgetsWhatWasTyped()
    {
        await using var room = await Room.OpenAsync();
        var connect = room.Connect();
        await Paste(connect, room.Invite(room.Person()));
        connect.Password = Password;

        connect.Dispose();

        Assert.Equal("", connect.Password);
        Assert.Equal("", connect.InviteText);
    }

    [Fact]
    public async Task EditingTheInvite_ClearsTheMessage()
    {
        await using var room = await Room.OpenAsync();
        var connect = room.Connect();
        await Paste(connect, "nonsense");

        connect.InviteText = "nonsens";

        Assert.Null(connect.Message);
    }
}
