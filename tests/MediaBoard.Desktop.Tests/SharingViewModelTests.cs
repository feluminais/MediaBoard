using System.Globalization;
using Avalonia.Headless.XUnit;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Library;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using MediaBoard.Desktop.Sharing;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Server;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop.Tests;

public sealed class SharingViewModelTests
{
    private const string Address = "media.example.org";

    /// <summary>Stands in for the owner at the dialogs.</summary>
    internal sealed class OwnerAtDialogs
    {
        public List<string> Asked { get; } = [];

        /// <summary>The yes buttons asked with the danger look.</summary>
        public List<string> Dangerous { get; } = [];

        public List<SharingPanel> Panels { get; } = [];

        public bool Answer { get; set; } = true;

        public string? Copied { get; set; }

        public bool CopyFails { get; set; }

        /// <summary>What the owner does while a panel is open. The panel stays "open" until this ends.</summary>
        public Func<SharingPanel, Task>? InPanel { get; set; }

        public SharingDialogs Dialogs => new(
            (question, yes, danger) =>
            {
                Asked.Add(question);
                if (danger) Dangerous.Add(yes);
                return Task.FromResult(Answer);
            },
            async panel =>
            {
                Panels.Add(panel);
                if (InPanel is { } script) await script(panel);
            },
            text =>
            {
                if (CopyFails) throw new IOException("The clipboard is busy.");
                Copied = text;
                return Task.CompletedTask;
            });
    }

    private static SharingViewModel Open(SharingWorld world, OwnerAtDialogs? owner = null, TimeProvider? clock = null)
    {
        var page = Build(world, owner, clock);
        page.Opened();
        return page;
    }

    internal static SharingViewModel Build(SharingWorld world, OwnerAtDialogs? owner = null, TimeProvider? clock = null, ILibrary? library = null)
    {
        world.OpenDatabase();
        var services = world.Services;
        return new SharingViewModel(
            new SharingServices(
                world.Sharing,
                world.Accounts,
                services.GetRequiredService<AccountStore>(),
                services.GetRequiredService<DeviceStore>(),
                services.GetRequiredService<InviteStore>(),
                world.Streams,
                services.GetRequiredService<SharingLog>(),
                services.GetRequiredService<SignInGuard>(),
                library ?? services.GetRequiredService<ILibrary>(),
                world.Settings,
                services.GetRequiredService<StartWithWindows>(),
                world.Hub,
                clock ?? TimeProvider.System),
            (owner ?? new OwnerAtDialogs()).Dialogs);
    }

    private static async Task<SharingViewModel> OpenLoaded(SharingWorld world, OwnerAtDialogs? owner = null, TimeProvider? clock = null)
    {
        var page = Open(world, owner, clock);
        await Ui.WaitUntil(() => page.IsLoaded);
        return page;
    }

    private static InviteStore Invites(SharingWorld world) => world.Services.GetRequiredService<InviteStore>();

    private static AccountStore People(SharingWorld world) => world.Services.GetRequiredService<AccountStore>();

    private static async Task<PersonRow> RowOf(SharingViewModel page, string name)
    {
        await page.RefreshAsync();
        return page.People.Single(row => row.Name == name);
    }

    private static async Task<PersonRow> OpenRow(SharingViewModel page, string name)
    {
        var row = await RowOf(page, name);
        await page.ToggleRowCommand.ExecuteAsync(row);
        return row;
    }

    // ---- The switch ----

    [AvaloniaFact]
    public async Task Switch_ReflectsController()
    {
        await using var world = new SharingWorld();
        var page = Open(world);
        Assert.False(page.IsOn);

        await world.Sharing.SetOnAsync(true);
        await Ui.WaitUntil(() => page.StatusLine.StartsWith("Sharing on", StringComparison.Ordinal));

        Assert.Equal($"Sharing on port {world.Port}", page.StatusLine);
    }

    [AvaloniaFact]
    public async Task Switch_TurnsSharingOnAndOff()
    {
        await using var world = new SharingWorld();
        var page = Open(world);

        page.IsOn = true;
        await Ui.WaitUntil(() => world.Sharing.Status == SharingStatus.On);

        page.IsOn = false;
        await Ui.WaitUntil(() => world.Sharing.Status == SharingStatus.Off);
        await Ui.WaitUntil(() => page.StatusLine == SharingViewModel.OffLine);
    }

    [AvaloniaFact]
    public async Task Off_SaysNothingCanConnect()
    {
        await using var world = new SharingWorld();

        var page = Open(world);

        Assert.Equal("Sharing is off. Nothing can connect.", page.StatusLine);
        Assert.False(page.HasProblem);
    }

    [AvaloniaFact]
    public async Task Failed_ShowsProblem()
    {
        await using var world = new SharingWorld();
        var page = Open(world);
        await File.WriteAllBytesAsync(world.Paths.Certificate, [1, 2, 3], TestContext.Current.CancellationToken);

        page.IsOn = true;
        await Ui.WaitUntil(() => page.HasProblem);

        Assert.Equal(world.Sharing.Problem, page.Problem);
        Assert.False(page.IsOn); // it isn't on, so the switch mustn't say so
    }

    [AvaloniaFact]
    public async Task FailedResume_ShowsTheSwitchOn_SoItCanBeTurnedOff()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { On = true });
        var page = Open(world);
        await File.WriteAllBytesAsync(world.Paths.Certificate, [1, 2, 3], TestContext.Current.CancellationToken);

        await world.Sharing.ResumeAsync();
        await Ui.WaitUntil(() => page.HasProblem);

        Assert.Equal(SharingStatus.Failed, world.Sharing.Status);
        Assert.True(page.IsOn);
        Assert.True(world.Settings.Load().Sharing.On);

        page.IsOn = false;
        await Ui.WaitUntil(() => world.Sharing.Status == SharingStatus.Off);

        Assert.False(world.Settings.Load().Sharing.On); // the next launch stays off
        await Ui.WaitUntil(() => !page.IsOn);
        Assert.False(page.HasProblem);
    }

    [AvaloniaFact]
    public async Task Problem_ShowsWhileOn_AndWhileOff()
    {
        await using var world = new SharingWorld();
        var page = Open(world);
        await world.Sharing.SetOnAsync(true);

        // A file nobody else may open stops the certificate being saved.
        await using (File.Open(world.Paths.Certificate, FileMode.Open, FileAccess.Read, FileShare.None))
            await world.Sharing.ReplaceCertificateAsync();

        await Ui.WaitUntil(() => page.HasProblem);
        Assert.Equal(SharingStatus.On, world.Sharing.Status);
        Assert.Equal(world.Sharing.Problem, page.Problem);

        await world.Sharing.SetOnAsync(false);
        await using (File.Open(world.Paths.Certificate, FileMode.Open, FileAccess.Read, FileShare.None))
            await world.Sharing.ReplaceCertificateAsync();

        await Ui.WaitUntil(() => page.HasProblem);
        Assert.Equal(SharingStatus.Off, world.Sharing.Status);
    }

    [AvaloniaFact]
    public async Task PublicNetwork_ShowsWarning()
    {
        await using var world = new SharingWorld { OnPublicNetwork = true };
        var page = Open(world);

        await world.Sharing.SetOnAsync(true);
        await Ui.WaitUntil(() => page.HasWarning);

        Assert.Equal(world.Sharing.Warning, page.Warning);
    }

    [AvaloniaFact]
    public async Task On_ShowsTheAddressAndWhoIsWatching()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        var page = await OpenLoaded(world);
        await world.Sharing.SetOnAsync(true);
        await Ui.WaitUntil(() => page.StatusLine.StartsWith("Sharing on", StringComparison.Ordinal));

        world.OpenStream();

        await Ui.WaitUntil(() => page.StatusDetail.Contains("1 watching", StringComparison.Ordinal));
        Assert.Contains(Address, page.StatusDetail);
    }

    [AvaloniaFact]
    public async Task FirstStart_MentionsFirewallPrompt()
    {
        await using var world = new SharingWorld();
        var page = Open(world);

        Assert.True(page.ShowFirewallNote);
        Assert.Contains("firewall", page.FirewallNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Private networks", page.FirewallNote);

        await world.Sharing.SetOnAsync(true);
        await Ui.WaitUntil(() => page.StatusLine.StartsWith("Sharing on", StringComparison.Ordinal));

        Assert.False(page.ShowFirewallNote);
    }

    [AvaloniaFact]
    public async Task TurningOff_WithSomeoneWatching_Asks()
    {
        await using var world = new SharingWorld();
        var owner = new OwnerAtDialogs { Answer = false };
        var page = Open(world, owner);
        await world.Sharing.SetOnAsync(true);
        await Ui.WaitUntil(() => page.StatusLine.StartsWith("Sharing on", StringComparison.Ordinal));
        world.OpenStream();

        page.IsOn = false;
        await Ui.WaitUntil(() => owner.Asked.Count == 1);
        await Ui.WaitUntil(() => page.StatusLine.StartsWith("Sharing on", StringComparison.Ordinal));

        Assert.Equal(SharingStatus.On, world.Sharing.Status);
        Assert.Contains("watching", owner.Asked[0]);
    }

    // ---- Watching now ----

    [AvaloniaFact]
    public async Task WatchingNow_EmptyState()
    {
        await using var world = new SharingWorld();

        var page = Open(world);

        Assert.Empty(page.Watching);
        Assert.True(page.IsNobodyWatching);
        Assert.Equal("Nobody is watching.", page.NobodyWatching);
    }

    [AvaloniaFact]
    public async Task WatchingNow_UpdatesFromRegistry()
    {
        await using var world = new SharingWorld();
        var page = Open(world);

        var lease = world.OpenStream();
        await Ui.WaitUntil(() => page.Watching.Count == 1);

        var row = page.Watching[0];
        Assert.False(page.IsNobodyWatching);
        Assert.Equal("Anna", row.Person);
        Assert.Equal("Phone", row.Device);
        Assert.Equal("Movie", row.Title);

        var session = world.Streams.Current[0];
        await Task.Run(() => world.Streams.Heartbeat(session.DeviceId, session.FileId, 725, 5400));
        await Ui.WaitUntil(() => row.Position == "12:05 of 1:30:00");

        world.Streams.Close(lease.Session);
        await Ui.WaitUntil(() => page.Watching.Count == 0);
        Assert.True(page.IsNobodyWatching);
    }

    [AvaloniaFact]
    public async Task WatchingNow_RegistryBurst_RefreshesOnceASecond()
    {
        await using var world = new SharingWorld();
        var clock = new ManualClock();
        var page = Open(world, clock: clock);
        world.OpenStream();
        await Ui.WaitUntil(() => page.Watching.Count == 1);
        var row = page.Watching[0];
        var session = world.Streams.Current[0];

        for (var position = 1; position <= 90; position++) world.Streams.Heartbeat(session.DeviceId, session.FileId, position, 0);
        Ui.Flush();
        Assert.Equal("0:00", row.Position); // nothing moves until the second is up

        clock.Advance(TimeSpan.FromSeconds(1));
        Ui.Flush();
        Assert.Equal("1:30", row.Position);
    }

    [AvaloniaFact]
    public async Task WatchingNow_SpeedMoves_WithNoEvent()
    {
        await using var world = new SharingWorld();
        var clock = new ManualClock();
        var page = Open(world, clock: clock);
        var lease = world.OpenStream();
        await Ui.WaitUntil(() => page.Watching.Count == 1);

        world.Streams.AddBytes(lease.Session, 1_000_000);
        clock.Advance(TimeSpan.FromSeconds(1));
        Ui.Flush();

        Assert.Equal("8.0 Mbit/s", page.Watching[0].Speed);
    }

    [AvaloniaFact]
    public async Task WatchingNow_StopsTicking_OnceThePageCloses()
    {
        await using var world = new SharingWorld();
        var clock = new ManualClock();
        var page = Open(world, clock: clock);
        world.OpenStream();
        await Ui.WaitUntil(() => page.Watching.Count == 1);

        page.Closed();
        clock.Advance(TimeSpan.FromSeconds(1));
        Ui.Flush();

        Assert.Equal(0, clock.PendingTimers);
    }

    [AvaloniaFact]
    public async Task Stop_CallsRegistry()
    {
        await using var world = new SharingWorld();
        var page = await OpenLoaded(world);
        world.OpenStream();
        await Ui.WaitUntil(() => page.Watching.Count == 1);

        await page.StopCommand.ExecuteAsync(page.Watching[0]);

        Assert.Empty(world.Streams.Current);
        await Ui.WaitUntil(() => page.Watching.Count == 0);
        await page.RefreshAsync();
        Assert.Contains(page.Activity, row => row.Text == "Anna's stream was stopped: Movie");
    }

    [AvaloniaFact]
    public async Task Stop_StreamAlreadyGone_LogsNothing()
    {
        await using var world = new SharingWorld();
        var page = await OpenLoaded(world);
        var lease = world.OpenStream();
        await Ui.WaitUntil(() => page.Watching.Count == 1);
        var row = page.Watching[0];
        world.Streams.Close(lease.Session);

        await page.StopCommand.ExecuteAsync(row);

        await page.RefreshAsync();
        Assert.DoesNotContain(page.Activity, line => line.Text.Contains("stopped", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Stop_SecondPress_LogsOnce()
    {
        await using var world = new SharingWorld();
        var page = await OpenLoaded(world);
        world.OpenStream();
        await Ui.WaitUntil(() => page.Watching.Count == 1);
        var row = page.Watching[0];

        await Task.WhenAll(page.StopCommand.ExecuteAsync(row), page.StopCommand.ExecuteAsync(row));

        await page.RefreshAsync();
        Assert.Single(page.Activity, line => line.Text.Contains("stopped", StringComparison.Ordinal));
    }

    // ---- People ----

    [AvaloniaFact]
    public async Task AddPerson_ShowsInviteOnce_WithCopy()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        string? invite = null;
        var owner = new OwnerAtDialogs();
        owner.InPanel = async panel =>
        {
            var step = Assert.IsType<AddPersonStep>(panel.Step);
            step.Name = "Anna";
            await step.CreateCommand.ExecuteAsync(null);

            var shown = Assert.IsType<InviteStep>(panel.Step);
            invite = shown.Invite;
            await shown.CopyCommand.ExecuteAsync(null);
            Assert.Equal("Copied", shown.CopyLabel);
        };
        var page = await OpenLoaded(world, owner);

        await page.AddPersonCommand.ExecuteAsync(null);

        Assert.StartsWith("mb1-", invite);
        Assert.Equal(invite, owner.Copied);
        var panel = Assert.Single(owner.Panels);
        Assert.Null(panel.Step); // the code exists in no other place, so it goes with the panel
        Assert.Contains(page.People, row => row.Name == "Anna");
    }

    [AvaloniaFact]
    public async Task AddPerson_CopyFails_SaysSo()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        string? said = null;
        var owner = new OwnerAtDialogs { CopyFails = true };
        owner.InPanel = async panel =>
        {
            var step = (AddPersonStep)panel.Step!;
            step.Name = "Anna";
            await step.CreateCommand.ExecuteAsync(null);
            var shown = (InviteStep)panel.Step!;
            await shown.CopyCommand.ExecuteAsync(null);
            said = shown.Message;
        };
        var page = await OpenLoaded(world, owner);

        await page.AddPersonCommand.ExecuteAsync(null);

        Assert.Contains("Ctrl+C", said);
    }

    [AvaloniaFact]
    public async Task AddPerson_WithChosenFolders_LimitsTheAccount()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        var movies = world.AddFolder("Movies");
        world.AddFolder("Shows");
        var owner = new OwnerAtDialogs();
        owner.InPanel = async panel =>
        {
            var step = (AddPersonStep)panel.Step!;
            step.Name = "Anna";
            step.AllFolders = false;
            step.Folders.Single(f => f.Id == movies).IsChecked = true;
            await step.CreateCommand.ExecuteAsync(null);
        };
        var page = await OpenLoaded(world, owner);

        await page.AddPersonCommand.ExecuteAsync(null);

        Assert.Equal([movies], People(world).ByName("Anna")!.FolderIds);
        Assert.Equal("1 folder", (await RowOf(page, "Anna")).Access);
    }

    [AvaloniaFact]
    public async Task AddPerson_DuplicateName_ShowsMessage()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        world.Accounts.AddPerson("Анна", null);
        string? said = null;
        var owner = new OwnerAtDialogs();
        owner.InPanel = async panel =>
        {
            var step = (AddPersonStep)panel.Step!;
            step.Name = "АННА";
            await step.CreateCommand.ExecuteAsync(null);
            said = step.Message;
            Assert.IsType<AddPersonStep>(panel.Step); // still asking, so another name can go in
        };
        var page = await OpenLoaded(world, owner);

        await page.AddPersonCommand.ExecuteAsync(null);

        Assert.Contains("АННА", said);
        Assert.Equal(2, People(world).All().Count);
    }

    [AvaloniaFact]
    public async Task AddPerson_WithNoAddress_PointsAtConnection()
    {
        await using var world = new SharingWorld();
        var owner = new OwnerAtDialogs();
        var page = await OpenLoaded(world, owner);
        var pointed = 0;
        page.ConnectionNeeded += () => pointed++;

        await page.AddPersonCommand.ExecuteAsync(null);

        Assert.True(page.IsConnectionOpen);
        Assert.Equal(1, pointed);
        Assert.Contains("address", page.ConnectionMessage);
        Assert.Empty(owner.Panels); // nothing asked for a name it couldn't use
        Assert.Single(People(world).All());
    }

    [AvaloniaFact]
    public async Task AddPerson_UnusableAddress_KeepsThePerson_AndPointsAtConnection()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = "http://" });
        var owner = new OwnerAtDialogs();
        owner.InPanel = async panel =>
        {
            var step = (AddPersonStep)panel.Step!;
            step.Name = "Anna";
            await step.CreateCommand.ExecuteAsync(null);
        };
        var page = await OpenLoaded(world, owner);

        await page.AddPersonCommand.ExecuteAsync(null);

        Assert.Contains("New invite", page.PeopleMessage);
        Assert.True(page.IsConnectionOpen);
        Assert.NotNull(People(world).ByName("Anna"));
    }

    [AvaloniaFact]
    public async Task NewInvite_UnreadableCertificate_SaysSo()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        world.Accounts.AddPerson("Anna", null);
        await File.WriteAllBytesAsync(world.Paths.Certificate, [1, 2, 3], TestContext.Current.CancellationToken);
        var owner = new OwnerAtDialogs();
        var page = await OpenLoaded(world, owner);
        var row = await OpenRow(page, "Anna");

        await page.NewInviteCommand.ExecuteAsync(row);

        Assert.Contains("certificate", row.Message);
        Assert.Empty(owner.Panels);
    }

    [AvaloniaFact]
    public async Task Row_ShowsFolderSummary()
    {
        await using var world = new SharingWorld();
        var movies = world.AddFolder("Movies");
        var shows = world.AddFolder("Shows");
        world.Accounts.AddPerson("All", null);
        world.Accounts.AddPerson("One", [movies]);
        world.Accounts.AddPerson("Two", [movies, shows]);
        world.Accounts.AddPerson("None", []);

        var page = await OpenLoaded(world);

        Assert.Equal("All folders", (await RowOf(page, "All")).Access);
        Assert.Equal("1 folder", (await RowOf(page, "One")).Access);
        Assert.Equal("2 folders", (await RowOf(page, "Two")).Access);
        Assert.Equal("No folders", (await RowOf(page, "None")).Access);
    }

    [AvaloniaFact]
    public async Task Row_KeepsItsObject_AcrossARefresh()
    {
        await using var world = new SharingWorld();
        world.Accounts.AddPerson("Anna", null);
        var page = await OpenLoaded(world);
        var row = await RowOf(page, "Anna");

        world.Accounts.AddPerson("Boris", null);
        await page.RefreshAsync();

        Assert.Same(row, page.People.Single(r => r.Name == "Anna")); // a focused button in the row stays
        Assert.Equal(3, page.People.Count);
    }

    [AvaloniaFact]
    public async Task Row_NeverJoined_SaysSo_AndLastSeenFollowsTheAccount()
    {
        await using var world = new SharingWorld();
        var anna = world.Accounts.AddPerson("Anna", null);
        var page = await OpenLoaded(world);

        Assert.Equal("Hasn't joined yet", (await RowOf(page, "Anna")).Seen);

        People(world).Touch(anna.Id);

        Assert.Equal("Seen just now", (await RowOf(page, "Anna")).Seen);
    }

    [AvaloniaFact]
    public async Task Folders_Change_AppliesAtOnce()
    {
        await using var world = new SharingWorld();
        var movies = world.AddFolder("Movies");
        var shows = world.AddFolder("Shows");
        var anna = world.Accounts.AddPerson("Anna", null);
        var page = await OpenLoaded(world);
        var row = await OpenRow(page, "Anna");
        var changes = new List<int>();
        world.Accounts.AccessChanged += changes.Add;

        // Leaving "All folders" keeps what the person could see, so nothing vanishes until a folder is unticked.
        row.Detail!.AllFolders = false;
        await Ui.WaitUntil(() => People(world).Get(anna.Id)!.FolderIds is not null);
        Assert.Equal([movies, shows], People(world).Get(anna.Id)!.FolderIds);

        row.Detail.Folders.Single(f => f.Id == shows).IsChecked = false;
        await Ui.WaitUntil(() => People(world).Get(anna.Id)!.FolderIds is { Count: 1 });
        await Ui.WaitUntil(() => row.Access == "1 folder");

        Assert.Equal([movies], People(world).Get(anna.Id)!.FolderIds);
        Assert.Equal([anna.Id, anna.Id], changes); // through AccountService, so streams lose what the person lost

        row.Detail.AllFolders = true;
        await Ui.WaitUntil(() => People(world).Get(anna.Id)!.FolderIds is null);
    }

    [AvaloniaFact]
    public async Task Folders_QuickChanges_LandInOrder()
    {
        await using var world = new SharingWorld();
        var ids = Enumerable.Range(0, 6).Select(i => world.AddFolder($"Folder {i}")).ToList();
        var anna = world.Accounts.AddPerson("Anna", ids);
        var page = await OpenLoaded(world);
        var row = await OpenRow(page, "Anna");

        foreach (var folder in row.Detail!.Folders) folder.IsChecked = false;

        await Ui.WaitUntil(() => People(world).Get(anna.Id)!.FolderIds is { Count: 0 });
        await Ui.WaitUntil(() => row.Access == "No folders");
        Assert.All(row.Detail.Folders, folder => Assert.False(folder.IsChecked));
    }

    [AvaloniaFact]
    public async Task Remove_ConfirmNamesProgressLoss()
    {
        await using var world = new SharingWorld();
        var anna = world.Accounts.AddPerson("Anna", null);
        var owner = new OwnerAtDialogs { Answer = false };
        var page = await OpenLoaded(world, owner);
        var row = await OpenRow(page, "Anna");

        await page.RemoveCommand.ExecuteAsync(row);

        var question = Assert.Single(owner.Asked);
        Assert.Contains("Anna", question);
        Assert.Contains("progress", question);
        Assert.NotNull(People(world).Get(anna.Id));

        owner.Answer = true;
        await page.RemoveCommand.ExecuteAsync(row);

        Assert.Null(People(world).Get(anna.Id));
        Assert.DoesNotContain(page.People, r => r.Name == "Anna");
    }

    [AvaloniaFact]
    public async Task Remove_FocusedRow_NamesTheNextRow()
    {
        await using var world = new SharingWorld();
        world.Accounts.AddPerson("Anna", null);
        world.Accounts.AddPerson("Boris", null);
        var page = await OpenLoaded(world);
        var row = await OpenRow(page, "Anna");
        PersonRow? next = null;
        page.RowsRemoved += near => next = near;

        await page.RemoveCommand.ExecuteAsync(row);

        Assert.Equal("Boris", next?.Name);
    }

    [AvaloniaFact]
    public async Task Disable_Confirms()
    {
        await using var world = new SharingWorld();
        var anna = world.Accounts.AddPerson("Anna", null);
        var owner = new OwnerAtDialogs { Answer = false };
        var page = await OpenLoaded(world, owner);
        var row = await OpenRow(page, "Anna");
        Assert.Equal("Disable", row.DisableLabel);

        await page.ToggleDisabledCommand.ExecuteAsync(row);

        var question = Assert.Single(owner.Asked);
        Assert.Contains("Anna", question);
        Assert.Contains("devices sign out", question);
        Assert.Contains("sign in again", question); // enabling doesn't bring the sessions back
        Assert.False(People(world).Get(anna.Id)!.Disabled);

        owner.Answer = true;
        await page.ToggleDisabledCommand.ExecuteAsync(row);

        Assert.True(People(world).Get(anna.Id)!.Disabled);
        Assert.True(row.IsDisabled);
        Assert.Equal("Enable", row.DisableLabel);
        Assert.False(row.CanInvite); // an invite would fail for a disabled account

        await page.ToggleDisabledCommand.ExecuteAsync(row);

        Assert.False(People(world).Get(anna.Id)!.Disabled);
        Assert.Equal(2, owner.Asked.Count); // undoing the ban asks nothing
    }

    [AvaloniaFact]
    public async Task ResetPassword_ShowsNewInvite()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { HomeAddress = "192.168.1.20" });
        var device = world.NewDevice();
        Invites(world).Create(device.Device.UserId);
        var owner = new OwnerAtDialogs();
        var page = await OpenLoaded(world, owner);
        var row = await OpenRow(page, "Anna");

        await page.ResetPasswordCommand.ExecuteAsync(row);

        Assert.Contains("Anna", Assert.Single(owner.Asked));
        Assert.Single(owner.Panels);
        // The reset deletes invites, so the one it shows is made after it.
        Assert.Single(Invites(world).Pending(device.Device.UserId));
        Assert.Empty(world.Services.GetRequiredService<DeviceStore>().ForUser(device.Device.UserId));
    }

    [AvaloniaFact]
    public async Task ResetPassword_WithNoAddress_PointsAtConnection_BeforeResetting()
    {
        await using var world = new SharingWorld();
        var device = world.NewDevice();
        var owner = new OwnerAtDialogs();
        var page = await OpenLoaded(world, owner);
        var row = await OpenRow(page, "Anna");

        await page.ResetPasswordCommand.ExecuteAsync(row);

        Assert.True(page.IsConnectionOpen);
        Assert.Empty(owner.Asked);
        Assert.Single(world.Services.GetRequiredService<DeviceStore>().ForUser(device.Device.UserId));
    }

    [AvaloniaFact]
    public async Task NewInvite_ShowsInvite()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        var anna = world.Accounts.AddPerson("Anna", null);
        var owner = new OwnerAtDialogs();
        owner.InPanel = panel =>
        {
            Assert.StartsWith("mb1-", Assert.IsType<InviteStep>(panel.Step).Invite);
            return Task.CompletedTask;
        };
        var page = await OpenLoaded(world, owner);
        var row = await OpenRow(page, "Anna");

        await page.NewInviteCommand.ExecuteAsync(row);

        Assert.Single(owner.Panels);
        Assert.Single(Invites(world).Pending(anna.Id));
    }

    [AvaloniaFact]
    public async Task NewInvite_ReplacesOlderPendingInvites()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        var anna = world.Accounts.AddPerson("Anna", null);
        var older = Invites(world).Create(anna.Id);
        Invites(world).Create(anna.Id);
        var page = await OpenLoaded(world);
        var row = await OpenRow(page, "Anna");

        await page.NewInviteCommand.ExecuteAsync(row);

        Assert.Single(Invites(world).Pending(anna.Id));
        Assert.Null(Invites(world).Peek(older));
    }

    [AvaloniaFact]
    public async Task NewInvite_Fails_KeepsOlderInvites()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = "http://" });
        var anna = world.Accounts.AddPerson("Anna", null);
        Invites(world).Create(anna.Id);
        var page = await OpenLoaded(world);
        var row = await OpenRow(page, "Anna");

        await page.NewInviteCommand.ExecuteAsync(row);

        Assert.Single(Invites(world).Pending(anna.Id));
    }

    [AvaloniaFact]
    public async Task NewInvite_WithNoAddress_PointsAtConnection()
    {
        await using var world = new SharingWorld();
        var anna = world.Accounts.AddPerson("Anna", null);
        var owner = new OwnerAtDialogs();
        var page = await OpenLoaded(world, owner);
        var row = await OpenRow(page, "Anna");

        await page.NewInviteCommand.ExecuteAsync(row);

        Assert.True(page.IsConnectionOpen);
        Assert.Empty(owner.Panels);
        Assert.Empty(Invites(world).Pending(anna.Id));
    }

    [AvaloniaFact]
    public async Task Invite_WithSharingOff_SaysItWaitsForSharing_AndNotOnceItsOn()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        world.Accounts.AddPerson("Anna", null);
        string? note = null;
        var owner = new OwnerAtDialogs();
        owner.InPanel = panel =>
        {
            note = ((InviteStep)panel.Step!).Note;
            return Task.CompletedTask;
        };
        var page = await OpenLoaded(world, owner);
        var row = await OpenRow(page, "Anna");

        await page.NewInviteCommand.ExecuteAsync(row);
        Assert.Contains("won't work until you turn it on", note);

        await world.Sharing.SetOnAsync(true);
        await page.NewInviteCommand.ExecuteAsync(row);
        Assert.DoesNotContain("turn it on", note);
    }

    [AvaloniaFact]
    public async Task Row_ShowsPendingInvites_AndCancelClearsThem_OnEveryRow()
    {
        await using var world = new SharingWorld();
        var anna = world.Accounts.AddPerson("Anna", null);
        Invites(world).Create(anna.Id);
        Invites(world).Create(anna.Id);
        Invites(world).Create(LibraryDatabase.OwnerId);
        var page = await OpenLoaded(world);
        var row = await OpenRow(page, "Anna");
        var ownerRow = page.People[0];

        Assert.Equal("2 invites pending", row.PendingInvites);
        Assert.Equal("1 invite pending", ownerRow.PendingInvites);

        await page.CancelInvitesCommand.ExecuteAsync(row);
        await page.CancelInvitesCommand.ExecuteAsync(ownerRow);

        Assert.Empty(Invites(world).Pending(anna.Id));
        Assert.Empty(Invites(world).Pending(LibraryDatabase.OwnerId));
        Assert.False(row.HasPendingInvites);
        Assert.False(ownerRow.HasPendingInvites);
    }

    [AvaloniaFact]
    public async Task Row_ClosingClearsItsMessage()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = "http://" });
        world.Accounts.AddPerson("Anna", null);
        var page = await OpenLoaded(world);
        var row = await OpenRow(page, "Anna");
        await page.NewInviteCommand.ExecuteAsync(row);
        Assert.True(row.HasMessage);

        await page.ToggleRowCommand.ExecuteAsync(row);

        Assert.False(row.HasMessage);
    }

    [AvaloniaFact]
    public async Task OwnerRow_ListsTheirDevices_AndSignsThemOut()
    {
        await using var world = new SharingWorld();
        var devices = world.Services.GetRequiredService<DeviceStore>();
        var page = await OpenLoaded(world);
        await page.ToggleRowCommand.ExecuteAsync(page.People[0]);
        var row = page.People[0];
        await Ui.WaitUntil(() => row.Detail!.DevicesLoaded);
        Assert.False(row.Detail!.ShowsDevices); // none, so nothing to list
        Assert.False(row.Detail.HasNoDevices);

        devices.Issue(LibraryDatabase.OwnerId, "Laptop", "198.51.100.2");
        await page.RefreshAsync();
        Assert.True(row.Detail.ShowsDevices);

        await page.SignOutCommand.ExecuteAsync(row.Detail.Devices[0]);

        Assert.Empty(devices.ForUser(LibraryDatabase.OwnerId));
        Assert.False(row.Detail.ShowsDevices);
    }

    [AvaloniaFact]
    public async Task OwnerRow_HasNoDisableRemoveFoldersOrReset()
    {
        await using var world = new SharingWorld();
        world.Accounts.AddPerson("Anna", null);
        var owner = new OwnerAtDialogs();
        var page = await OpenLoaded(world, owner);
        var row = page.People[0];

        Assert.True(row.IsOwner); // listed first, whatever the names sort to
        Assert.False(row.CanManage);
        Assert.False(row.CanResetPassword);
        Assert.True(row.CanInvite);

        await page.ToggleDisabledCommand.ExecuteAsync(row);
        await page.RemoveCommand.ExecuteAsync(row);
        await page.ResetPasswordCommand.ExecuteAsync(row);

        Assert.Empty(owner.Asked);
        Assert.NotNull(People(world).Get(LibraryDatabase.OwnerId));
        Assert.True(page.People.Single(r => r.Name == "Anna").CanManage);
    }

    [AvaloniaFact]
    public void AddStep_StaysOpenWhileItRuns_AndSaysSo()
    {
        var running = new TaskCompletionSource();
        var panel = new SharingPanel();
        var step = new AddPersonStep([], _ => running.Task) { Name = "Anna" };
        panel.Step = step;

        var create = step.CreateCommand.ExecuteAsync(null);

        Assert.Equal("Adding…", step.AddLabel);
        Assert.False(panel.MayClose());

        running.SetResult();
        create.GetAwaiter().GetResult();

        Assert.Equal("Add", step.AddLabel);
        Assert.True(panel.MayClose());
    }

    [AvaloniaFact]
    public void PasswordStep_StaysOpenWhileItRuns_AndSaysSo()
    {
        var running = new TaskCompletionSource();
        var panel = new SharingPanel();
        var step = new SetPasswordStep(_ => running.Task);
        panel.Step = step;

        var save = step.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Saving…", step.SaveLabel);
        Assert.False(panel.MayClose());

        running.SetResult();
        save.GetAwaiter().GetResult();

        Assert.True(panel.MayClose());
    }

    [AvaloniaFact]
    public async Task Invite_NotCopied_WarnsOnce_ThenCloses()
    {
        var panel = new SharingPanel { Step = new InviteStep("Invite for Anna", "Send this.", "mb1-x", _ => Task.CompletedTask) };
        var step = (InviteStep)panel.Step;

        Assert.False(panel.MayClose());
        Assert.Contains("won't see it again", step.Message);
        Assert.Equal("Close anyway", step.DoneLabel);
        Assert.True(panel.MayClose());

        var copied = new SharingPanel { Step = new InviteStep("Invite for Anna", "Send this.", "mb1-x", _ => Task.CompletedTask) };
        await ((InviteStep)copied.Step).CopyCommand.ExecuteAsync(null);

        Assert.True(copied.MayClose());
    }

    [AvaloniaFact]
    public async Task Panel_FinishedByItsStep_ClosesWhileBusy()
    {
        var closed = 0;
        var panel = new SharingPanel();
        panel.CloseRequested += () => closed++;
        panel.Step = new AddPersonStep([], _ =>
        {
            panel.Finish();
            Assert.True(panel.MayClose());
            return Task.CompletedTask;
        }) { Name = "Anna" };

        await ((AddPersonStep)panel.Step).CreateCommand.ExecuteAsync(null);

        Assert.Equal(1, closed);
    }

    [AvaloniaFact]
    public async Task NoFolders_SaysSo_WhenLeavingAllFolders()
    {
        await using var world = new SharingWorld();
        world.Accounts.AddPerson("Anna", null);
        var page = await OpenLoaded(world);
        var row = await OpenRow(page, "Anna");
        Assert.False(row.Detail!.HasNoFolders);

        row.Detail.AllFolders = false;

        Assert.True(row.Detail.HasNoFolders);
        Assert.True(new AddPersonStep([], _ => Task.CompletedTask) { AllFolders = false }.HasNoFolders);
    }

    [AvaloniaFact]
    public async Task DangerousAnswers_AreMarked()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        world.Accounts.AddPerson("Anna", null);
        var owner = new OwnerAtDialogs();
        var page = await OpenLoaded(world, owner);
        var row = await OpenRow(page, "Anna");

        await page.ResetPasswordCommand.ExecuteAsync(row);
        await page.ToggleDisabledCommand.ExecuteAsync(row);
        await page.ToggleDisabledCommand.ExecuteAsync(row);
        await page.RemoveCommand.ExecuteAsync(row);
        await page.ReplaceCertificateCommand.ExecuteAsync(null);

        Assert.Equal(["Reset", "Disable", "Remove", "Replace"], owner.Dangerous);
    }

    [AvaloniaFact]
    public async Task LoadFails_SaysSo_AndAGoodLoadClearsIt()
    {
        await using var world = new SharingWorld();
        var library = new FakeLibrary { OnQuery = _ => throw new IOException("The disk is gone.") };
        var page = Build(world, library: library);

        page.Opened();
        await Ui.WaitUntil(() => page.HasLoadProblem);

        Assert.False(page.IsLoaded);
        Assert.Contains("Couldn't load", page.LoadProblem);

        library.OnQuery = null;
        page.Opened();
        await Ui.WaitUntil(() => page.IsLoaded);

        Assert.False(page.HasLoadProblem);
    }

    [AvaloniaFact]
    public async Task SetPassword_SavesTheOwnersPassword_RefusesAShortOne()
    {
        await using var world = new SharingWorld();
        var messages = new List<string?>();
        var owner = new OwnerAtDialogs();
        owner.InPanel = async panel =>
        {
            var step = Assert.IsType<SetPasswordStep>(panel.Step);
            step.Password = "short";
            step.Repeat = "short";
            await step.SaveCommand.ExecuteAsync(null);
            messages.Add(step.Message);

            step.Password = "long enough now";
            step.Repeat = "not the same";
            await step.SaveCommand.ExecuteAsync(null);
            messages.Add(step.Message);

            step.Repeat = "long enough now";
            await step.SaveCommand.ExecuteAsync(null);
            messages.Add(step.Message);
        };
        var page = await OpenLoaded(world, owner);

        await page.SetPasswordCommand.ExecuteAsync(page.People[0]);

        Assert.Contains("10", messages[0]);
        Assert.Contains("match", messages[1]);
        Assert.Null(messages[2]);
        Assert.True(People(world).Get(LibraryDatabase.OwnerId)!.HasPassword);
    }

    [AvaloniaFact]
    public async Task SignOut_EndsThatDevice_AndKeepsTheOthers()
    {
        await using var world = new SharingWorld();
        var phone = world.NewDevice();
        var devices = world.Services.GetRequiredService<DeviceStore>();
        devices.Issue(phone.Device.UserId, "Laptop", "198.51.100.2");
        var page = await OpenLoaded(world);
        var row = await OpenRow(page, "Anna");
        await Ui.WaitUntil(() => row.Detail!.Devices.Count == 2);

        await page.SignOutCommand.ExecuteAsync(row.Detail!.Devices[0]);

        Assert.Equal(["Laptop"], devices.ForUser(phone.Device.UserId).Select(d => d.Name));
        await Ui.WaitUntil(() => row.Detail!.Devices.Count == 1);
    }

    [AvaloniaFact]
    public async Task LockedName_Shows()
    {
        await using var world = new SharingWorld();
        world.Accounts.AddPerson("Anna", null);
        world.Accounts.AddPerson("Boris", null);
        var guard = world.Services.GetRequiredService<SignInGuard>();
        for (var i = 0; i < 5; i++)
        {
            guard.RecordFailure("anna", null);
            guard.RecordFailure("nobody-by-this-name", null);
        }

        var page = await OpenLoaded(world);

        Assert.True((await RowOf(page, "Anna")).IsLocked); // typed in any case
        Assert.False((await RowOf(page, "Boris")).IsLocked);
        Assert.Equal(3, page.People.Count); // a guess at a name that doesn't exist adds nothing
    }

    // ---- Activity ----

    [AvaloniaFact]
    public async Task Activity_ShowsLast50()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        var log = world.Services.GetRequiredService<SharingLog>();
        var anna = world.Accounts.AddPerson("Anna", null);
        for (var i = 0; i < 60; i++) log.Add(SharingEvent.SignedIn, anna.Id, string.Create(CultureInfo.InvariantCulture, $"Device {i}"));
        ActivityStep? all = null;
        var owner = new OwnerAtDialogs();
        owner.InPanel = panel =>
        {
            all = Assert.IsType<ActivityStep>(panel.Step);
            return Task.CompletedTask;
        };

        var page = await OpenLoaded(world, owner);

        Assert.Equal(50, page.Activity.Count);
        Assert.Equal("Anna signed in on Device 59", page.Activity[0].Text);

        await page.ShowAllActivityCommand.ExecuteAsync(null);

        Assert.Equal(60, all?.Rows.Count);
    }

    [AvaloniaFact]
    public async Task Activity_IsEmpty_SaysSo()
    {
        await using var world = new SharingWorld();

        var page = await OpenLoaded(world);

        Assert.True(page.HasNoActivity);
    }

    [AvaloniaTheory]
    [InlineData(SharingEvent.Joined, "Phone", "Anna joined on Phone")]
    [InlineData(SharingEvent.SignedIn, "Phone", "Anna signed in on Phone")]
    [InlineData(SharingEvent.SignInLocked, "", "Anna's sign-in was locked after too many tries")]
    [InlineData(SharingEvent.StartedWatching, "Movie", "Anna started Movie")]
    [InlineData(SharingEvent.FinishedWatching, "Movie", "Anna finished Movie")]
    [InlineData(SharingEvent.StreamStopped, "Movie", "Anna's stream was stopped: Movie")]
    [InlineData(SharingEvent.SignedOut, "Phone", "Anna's Phone was signed out")]
    [InlineData(SharingEvent.Disabled, "", "Anna was disabled")]
    [InlineData(SharingEvent.PasswordReset, "", "Anna's password was reset")]
    public async Task Activity_SaysEachKindInPlainWords(SharingEvent kind, string detail, string words)
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        var anna = world.Accounts.AddPerson("Anna", null);
        world.Services.GetRequiredService<SharingLog>().Add(kind, anna.Id, detail);

        var page = await OpenLoaded(world);

        Assert.Equal(words, Assert.Single(page.Activity).Text);
    }

    [AvaloniaFact]
    public async Task Activity_RemovedPerson_KeepsTheirName_FromTheEntry()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        var anna = world.Accounts.AddPerson("Anna", null);
        world.Accounts.Remove(anna.Id);

        var page = await OpenLoaded(world);

        Assert.Equal("Anna was removed", Assert.Single(page.Activity).Text);
    }

    [AvaloniaFact]
    public async Task Activity_LockedGuess_NamesNobody()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.Services.GetRequiredService<SharingLog>().Add(SharingEvent.SignInLocked, null, "");

        var page = await OpenLoaded(world);

        Assert.Equal("A sign-in was locked after too many tries", Assert.Single(page.Activity).Text);
    }

    // ---- Connection ----

    [AvaloniaFact]
    public async Task Connection_ShowsAndSavesTheSettings()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        var page = await OpenLoaded(world);
        Assert.Equal(Address, page.PublicAddress);
        Assert.Equal(world.Port.ToString(CultureInfo.InvariantCulture), page.PortText);

        page.HomeAddress = " 192.168.1.20 ";
        page.PublicAddress = "";
        page.ServerName = "The den";
        page.PortText = "48000";
        await page.SaveConnectionCommand.ExecuteAsync(null);

        var saved = world.Settings.Load().Sharing;
        Assert.Equal("192.168.1.20", saved.HomeAddress);
        Assert.Null(saved.PublicAddress);
        Assert.Equal("The den", saved.ServerName);
        Assert.Equal(48000, saved.Port);
        Assert.Equal("Saved", page.ConnectionMessage);
    }

    [AvaloniaFact]
    public async Task Connection_BadPort_IsRefused()
    {
        await using var world = new SharingWorld();
        var page = await OpenLoaded(world);

        page.PortText = "70000";
        await page.SaveConnectionCommand.ExecuteAsync(null);

        Assert.Equal(world.Port, world.Settings.Load().Sharing.Port);
        Assert.Contains("65535", page.ConnectionMessage);
    }

    [AvaloniaFact]
    public async Task Connection_NewPortWhileOn_SaysWhenItApplies()
    {
        await using var world = new SharingWorld();
        var page = await OpenLoaded(world);
        await world.Sharing.SetOnAsync(true);
        await Ui.WaitUntil(() => page.StatusLine.StartsWith("Sharing on", StringComparison.Ordinal));

        page.PortText = SharingWorld.FreePort().ToString(CultureInfo.InvariantCulture);
        await page.SaveConnectionCommand.ExecuteAsync(null);

        Assert.Contains("next", page.ConnectionMessage);
        Assert.Equal($"Sharing on port {world.Port}", page.StatusLine); // the port in use, not the one saved
    }

    [AvaloniaFact]
    public async Task Connection_TypedText_SurvivesARefresh()
    {
        await using var world = new SharingWorld();
        var page = await OpenLoaded(world);

        page.PublicAddress = "half-typed.exa";
        await page.RefreshAsync();
        page.Opened();
        await page.RefreshAsync();

        Assert.Equal("half-typed.exa", page.PublicAddress);
    }

    [AvaloniaFact]
    public async Task Connection_StepsNameThePort()
    {
        await using var world = new SharingWorld();
        var page = await OpenLoaded(world);

        Assert.Contains(page.RouterSteps, step => step.Contains(world.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Connection_UnusableAddress_IsRefused()
    {
        await using var world = new SharingWorld();
        var page = await OpenLoaded(world);

        page.PublicAddress = "http://";
        await page.SaveConnectionCommand.ExecuteAsync(null);

        Assert.Null(world.Settings.Load().Sharing.PublicAddress);
        Assert.Contains("can't be used", page.ConnectionMessage);
    }

    [AvaloniaFact]
    public async Task Connection_NewNameWhileOn_SaysWhenItApplies_AndWhileOffSaysSaved()
    {
        await using var world = new SharingWorld();
        var page = await OpenLoaded(world);

        page.ServerName = "The den";
        await page.SaveConnectionCommand.ExecuteAsync(null);
        Assert.Equal("Saved", page.ConnectionMessage);

        await world.Sharing.SetOnAsync(true);
        await Ui.WaitUntil(() => page.StatusLine.StartsWith("Sharing on", StringComparison.Ordinal));
        page.ServerName = "The attic";
        await page.SaveConnectionCommand.ExecuteAsync(null);
        Assert.Contains("library name", page.ConnectionMessage);
        Assert.Contains("next time", page.ConnectionMessage);

        page.ServerName = "The cellar";
        page.PortText = SharingWorld.FreePort().ToString(CultureInfo.InvariantCulture);
        await page.SaveConnectionCommand.ExecuteAsync(null);
        Assert.Contains("port and library name", page.ConnectionMessage);
    }

    [AvaloniaFact]
    public async Task CheckConnection_UnsavedAddress_SaysSaveFirst_AndEditingClearsTheResult()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        var page = await OpenLoaded(world);
        await world.Sharing.SetOnAsync(true);
        await Ui.WaitUntil(() => page.StatusLine.StartsWith("Sharing on", StringComparison.Ordinal));

        page.PublicAddress = "other.example.org";
        await page.CheckConnectionCommand.ExecuteAsync(null);
        Assert.Equal("Save first.", page.CheckResult);

        page.PublicAddress = "another.example.org";
        Assert.Null(page.CheckResult);
    }

    [AvaloniaFact]
    public async Task CheckConnection_SharingOff_SaysToTurnItOn()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = Address });
        var page = await OpenLoaded(world);

        await page.CheckConnectionCommand.ExecuteAsync(null);

        Assert.Contains("Turn sharing on", page.CheckResult);
    }

    [AvaloniaFact]
    public async Task CheckConnection_NoAddress_SaysSo()
    {
        await using var world = new SharingWorld();
        var page = await OpenLoaded(world);
        await world.Sharing.SetOnAsync(true);
        await Ui.WaitUntil(() => page.StatusLine.StartsWith("Sharing on", StringComparison.Ordinal));

        await page.CheckConnectionCommand.ExecuteAsync(null);

        Assert.Contains("public address", page.CheckResult);
    }

    [AvaloniaFact]
    public async Task CheckConnection_Pass_And_NotConclusive()
    {
        await using var world = new SharingWorld();
        var page = await OpenLoaded(world);
        await world.Sharing.SetOnAsync(true);
        await Ui.WaitUntil(() => page.StatusLine.StartsWith("Sharing on", StringComparison.Ordinal));

        world.SetSharing(s => s with { PublicAddress = $"127.0.0.1:{world.Port}" });
        await page.CheckConnectionCommand.ExecuteAsync(null);
        Assert.Contains("looks right", page.CheckResult);

        world.SetSharing(s => s with { PublicAddress = $"127.0.0.1:{SharingWorld.FreePort()}" });
        await page.CheckConnectionCommand.ExecuteAsync(null);
        Assert.Contains("loop back", page.CheckResult);
    }

    [AvaloniaFact]
    public async Task StartWithWindows_ShowsTheSavedValue_WithoutWritingIt()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { StartWithWindows = true });

        var page = await OpenLoaded(world);

        Assert.True(page.StartsWithWindows);
        Assert.Null(world.RunKey.Command); // showing it must not write it
    }

    [AvaloniaFact]
    public async Task StartWithWindows_OwnersChange_IsWritten()
    {
        await using var world = new SharingWorld();
        var page = await OpenLoaded(world);

        page.StartsWithWindows = true;

        await Ui.WaitUntil(() => world.RunKey.Command is not null);
        await Ui.WaitUntil(() => world.Settings.Load().Sharing.StartWithWindows);
    }

    [AvaloniaFact]
    public async Task StartWithWindows_Refused_GoesBack_WithoutWritingAgain()
    {
        await using var world = new SharingWorld();
        world.RunKey.Refuses = true;
        var page = await OpenLoaded(world);

        page.StartsWithWindows = true;

        await Ui.WaitUntil(() => !page.StartsWithWindows);
        Assert.Contains("Start with Windows", page.ConnectionMessage);
        Assert.False(world.Settings.Load().Sharing.StartWithWindows);
    }

    [AvaloniaFact]
    public async Task ReplaceCertificate_WarnsAboutInvites()
    {
        await using var world = new SharingWorld();
        var owner = new OwnerAtDialogs { Answer = false };
        var page = await OpenLoaded(world, owner);
        var before = await ReadCertificate(world);

        await page.ReplaceCertificateCommand.ExecuteAsync(null);

        Assert.Contains("new invite", Assert.Single(owner.Asked));
        Assert.Equal(before, await ReadCertificate(world));
    }

    [AvaloniaFact]
    public async Task ReplaceCertificate_CancelsEveryPendingInvite()
    {
        await using var world = new SharingWorld();
        var anna = world.Accounts.AddPerson("Anna", null);
        var boris = world.Accounts.AddPerson("Boris", null);
        Invites(world).Create(anna.Id);
        Invites(world).Create(boris.Id);
        Invites(world).Create(LibraryDatabase.OwnerId);
        var page = await OpenLoaded(world);

        await page.ReplaceCertificateCommand.ExecuteAsync(null);

        Assert.Empty(Invites(world).Pending(anna.Id));
        Assert.Empty(Invites(world).Pending(boris.Id));
        Assert.Empty(Invites(world).Pending(LibraryDatabase.OwnerId));
        Assert.Contains("new invite", page.ConnectionMessage);
    }

    [AvaloniaFact]
    public async Task ReplaceCertificate_Failed_KeepsInvites()
    {
        await using var world = new SharingWorld();
        var anna = world.Accounts.AddPerson("Anna", null);
        Invites(world).Create(anna.Id);
        var page = await OpenLoaded(world);
        await ReadCertificate(world);

        await using (File.Open(world.Paths.Certificate, FileMode.Open, FileAccess.Read, FileShare.None))
            await page.ReplaceCertificateCommand.ExecuteAsync(null);

        await Ui.WaitUntil(() => page.HasProblem);
        Assert.Single(Invites(world).Pending(anna.Id));
    }

    [AvaloniaFact]
    public async Task ReplaceCertificate_RestartFails_StillCancelsInvites()
    {
        await using var world = new SharingWorld();
        var anna = world.Accounts.AddPerson("Anna", null);
        var page = await OpenLoaded(world);
        await world.Sharing.SetOnAsync(true);
        Invites(world).Create(anna.Id);
        world.SetSharing(s => s with { Port = 70000 }); // the restart can't bind
        await page.ReplaceCertificateCommand.ExecuteAsync(null);

        Assert.Equal(SharingStatus.Failed, world.Sharing.Status);
        Assert.Empty(Invites(world).Pending(anna.Id));
    }

    // ---- The reload ----

    [AvaloniaFact]
    public async Task Reload_RunsEvery5Seconds_WhileOpenAndOn()
    {
        await using var world = new SharingWorld();
        var clock = new ManualClock();
        var page = await OpenLoaded(world, clock: clock);
        await world.Sharing.SetOnAsync(true);

        world.Accounts.AddPerson("Anna", null);
        clock.Advance(TimeSpan.FromSeconds(5));

        await Ui.WaitUntil(() => page.People.Any(row => row.Name == "Anna"));

        world.Accounts.AddPerson("Boris", null); // the timer rearms itself
        clock.Advance(TimeSpan.FromSeconds(5));

        await Ui.WaitUntil(() => page.People.Any(row => row.Name == "Boris"));
    }

    [AvaloniaFact]
    public async Task Reload_Skipped_WhileSharingIsOff()
    {
        await using var world = new SharingWorld();
        var clock = new ManualClock();
        var page = await OpenLoaded(world, clock: clock);

        world.Accounts.AddPerson("Anna", null);
        clock.Advance(TimeSpan.FromSeconds(5));
        Ui.Flush();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Ui.Flush();

        Assert.DoesNotContain(page.People, row => row.Name == "Anna");
        Assert.Equal(1, clock.PendingTimers); // still armed, for when sharing comes on
    }

    [AvaloniaFact]
    public async Task Reload_Stops_WhenThePageCloses()
    {
        await using var world = new SharingWorld();
        var clock = new ManualClock();
        var page = await OpenLoaded(world, clock: clock);
        await world.Sharing.SetOnAsync(true);

        page.Closed();
        world.Accounts.AddPerson("Anna", null);
        clock.Advance(TimeSpan.FromSeconds(5));
        Ui.Flush();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Ui.Flush();

        Assert.DoesNotContain(page.People, row => row.Name == "Anna");
        Assert.Equal(0, clock.PendingTimers);
    }

    // Reading the certificate makes it if it isn't there yet.
    private static async Task<byte[]> ReadCertificate(SharingWorld world)
    {
        world.Services.GetRequiredService<CertificateStore>().LoadOrCreate().Dispose();
        return await File.ReadAllBytesAsync(world.Paths.Certificate, TestContext.Current.CancellationToken);
    }
}
