using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Players;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using MediaBoard.Desktop.Remote;
using MediaBoard.Desktop.Sharing;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;
using MediaBoard.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Viewer = MediaBoard.Desktop.Tests.RemoteCompositionTests.Viewer;

namespace MediaBoard.Desktop.Tests;

/// <summary>The app in remote mode as a person meets it: what's gone, what each connection state says, and how closing works.</summary>
public sealed class RemoteUiTests : IDisposable
{
    private const string Server = "Anna's library";
    private const string SamPassword = "correct horse battery";

    private static readonly ServerEndpoint Endpoint = new(Server, ["127.0.0.1:1"], new string('A', 64));

    private readonly string _dir = Directory.CreateTempSubdirectory("mediaboard-remote-ui-").FullName;
    private readonly List<IDisposable> _links = [];
    private readonly ActivityHub _hub = new();
    private readonly FakeLibrary _library = new();
    private readonly Titles _titles;
    private int _signIns;

    public RemoteUiTests()
    {
        _titles = new Titles(_library, _hub);
        _library.FolderRows.Add(FakeLibrary.Folder(0, ""));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        foreach (var link in _links) link.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A monitor with no network under it: a test moves it with <see cref="ConnectionMonitor.Report"/>.</summary>
    private ConnectionMonitor Monitor()
    {
        ConnectionMonitor? monitor = null;
        var client = new ApiClient(Endpoint, () => null, happened => monitor?.Report(happened));
        _links.Add(client);
        return monitor = new ConnectionMonitor(client);
    }

    private (MainWindowViewModel Shell, ConnectionPanelViewModel Panel, ConnectionMonitor Monitor) RemoteShell()
    {
        var monitor = Monitor();
        var capabilities = AppCapabilities.Remote(monitor);
        var panel = new ConnectionPanelViewModel(monitor, Server, () =>
        {
            _signIns++;
            return true;
        });
        var shell = new MainWindowViewModel(
            _library, new StatusBarViewModel(_hub), new ActivityPanelViewModel(_hub), _hub, null,
            NewSettings(capabilities), Posters.None(), _titles.Services() with { Capabilities = capabilities },
            capabilities: capabilities, connection: panel);
        return (shell, panel, monitor);
    }

    private SettingsViewModel NewSettings(AppCapabilities? capabilities = null, Func<Task<string?>>? connectToServer = null) => new(
        new SettingsStore(SettingsPath), new PlayerDetector(_ => false, () => [], _hub), _hub,
        () => Task.FromResult<string?>(null), _ => { }, capabilities, null, connectToServer);

    private static IReadOnlyList<string> ButtonsShown(Control view) =>
        view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content).OfType<string>().ToList();

    private static IReadOnlyList<string> TextShown(Control view) =>
        view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();

    private static Window Show(Control view)
    {
        var window = new Window { Width = 1200, Height = 900, Content = view };
        window.Show();
        Ui.Flush();
        window.UpdateLayout();
        Ui.Flush();
        return window;
    }

    // ---- What's gone ----

    [AvaloniaFact]
    public async Task Sidebar_Remote_HasNoReviewFoldersSharing()
    {
        var (shell, _, _) = RemoteShell();
        _library.Review.Add(FakeLibrary.ReviewFile(1, "a.mkv"));

        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => shell.CurrentPage is HomeViewModel { IsLoaded: true });

        Assert.Equal([MainWindowViewModel.HomePage, MainWindowViewModel.MoviesPage, MainWindowViewModel.ShowsPage, MainWindowViewModel.SettingsPage],
            shell.NavItems.Select(i => i.Key));
        Assert.False(shell.HasReview);

        var home = shell.CurrentPage;
        foreach (var gone in new[] { MainWindowViewModel.ReviewPage, MainWindowViewModel.FoldersPage, MainWindowViewModel.SharingPage })
        {
            shell.NavigateCommand.Execute(gone);
            Assert.Same(home, shell.CurrentPage);
        }
    }

    [AvaloniaFact]
    public async Task Title_Remote_HidesEdits_KeepsPlayAndWatched()
    {
        var capabilities = AppCapabilities.Remote(Monitor());
        _library.Details[1] = FakeLibrary.Show(1, "Slow Horses", FakeLibrary.Season(1, 1, FakeLibrary.Episode(10, 1, 1, "Failure's Contagious", fileId: 7)));
        _library.Next[1] = 7;
        var page = new TitleViewModel(1, _titles.Services() with { Capabilities = capabilities }, _titles.Navigation());
        await page.RefreshAsync();

        var window = Show(new TitleView { DataContext = page });
        var buttons = ButtonsShown((Control)window.Content!);
        window.Close();

        foreach (var edit in new[] { "Rename", "Hide", "Show", "Fix match", "Not a movie or show", "Delete from disk" }) Assert.DoesNotContain(edit, buttons);
        Assert.Contains(page.PlayText, buttons);
        Assert.Contains("Play", buttons);
        Assert.Contains("Mark watched", buttons);
    }

    // Every command that ends in a member RemoteLibrary refuses, on every kind of row: hiding a button doesn't stop a key.
    [AvaloniaFact]
    public async Task Title_Remote_NoRefusedEditCanRun()
    {
        var capabilities = AppCapabilities.Remote(Monitor());
        _library.Details[1] = FakeLibrary.Show(1, "Slow Horses", FakeLibrary.Season(1, 1, FakeLibrary.Episode(10, 1, 1, "Failure's Contagious", fileId: 7)));
        var page = new TitleViewModel(1, _titles.Services() with { Capabilities = capabilities }, _titles.Navigation());
        await page.RefreshAsync();
        var season = page.ListedSeason!;
        var episode = season.Episodes[0];

        Assert.False(page.CanEdit);
        Assert.False(page.ToggleHiddenCommand.CanExecute(null));
        Assert.False(page.FixMatchCommand.CanExecute(null));
        Assert.False(page.NotMediaCommand.CanExecute(null));
        Assert.False(page.DeleteCommand.CanExecute(null));
        foreach (var item in new EditableItem[] { page.Header, season, episode })
        {
            Assert.False(item.CanEdit);
            Assert.False(item.BeginRenameCommand.CanExecute(null));
            Assert.False(item.CommitRenameCommand.CanExecute(null));
            Assert.False(item.ToggleHiddenCommand.CanExecute(null));
        }

        Assert.True(page.ToggleWatchedCommand.CanExecute(null));
        Assert.True(episode.ToggleWatchedCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Title_Local_KeepsEveryEdit()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Heat", fileId: 7);
        var page = _titles.Page(1);
        await page.RefreshAsync();

        Assert.True(page.CanEdit);
        Assert.True(page.DeleteCommand.CanExecute(null));
        Assert.True(page.Header.BeginRenameCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Grid_Remote_NoHiddenSwitch_NoFolderHint()
    {
        var capabilities = AppCapabilities.Remote(Monitor());
        var grid = new TitleGridViewModel(TitleKind.Movie, _library, _hub, Posters.None(), new PageActions(_ => { }, _ => { }, _ => { }), capabilities);
        await grid.RefreshAsync();

        Assert.False(grid.CanShowHidden);
        Assert.Equal("No movies yet", grid.EmptyTitle);
        Assert.False(grid.HasEmptyHint);
    }

    [AvaloniaFact]
    public async Task Settings_Remote_HidesKey_ShowsAccount()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var settings = viewer.Services.GetRequiredService<SettingsViewModel>();

        var window = Show(new SettingsView { DataContext = settings });
        var view = (Control)window.Content!;
        var shown = TextShown(view);
        var buttons = ButtonsShown(view);
        window.Close();

        Assert.False(settings.ShowTmdbKey);
        Assert.False(settings.ShowOtherPlayers);
        Assert.DoesNotContain("TMDB key", shown);
        Assert.DoesNotContain("Other players", shown);
        Assert.Contains("Player", shown);
        Assert.Contains("Display", shown);
        Assert.Contains("Account", shown);
        Assert.Contains(viewer.Connection.Endpoint.ServerName, shown);
        Assert.Contains("Sam", shown);
        Assert.Contains("Sign out", buttons);
        Assert.Contains("Use this PC's library", buttons);
        Assert.Contains("Change password", buttons);
        Assert.DoesNotContain("Connect to a server", buttons);
    }

    [AvaloniaFact]
    public void Settings_ThisPc_OffersAServer_AndNoAccount()
    {
        var settings = NewSettings(connectToServer: () => Task.FromResult<string?>(null));

        var window = Show(new SettingsView { DataContext = settings });
        var view = (Control)window.Content!;
        var shown = TextShown(view);
        var buttons = ButtonsShown(view);
        window.Close();

        Assert.True(settings.ShowTmdbKey);
        Assert.Null(settings.Account);
        Assert.Contains("TMDB key", shown);
        Assert.Contains("Connect to a server", buttons);
        Assert.DoesNotContain("Sign out", buttons);
    }

    // ---- Signing out and switching ----

    [AvaloniaFact]
    public async Task SignOut_ClearsConnection_ReturnsToConnect()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        var account = viewer.Services.GetRequiredService<SettingsViewModel>().Account!;
        var settings = viewer.Services.GetRequiredService<SettingsStore>();

        await account.SignOutCommand.ExecuteAsync(null);

        Assert.Equal(1, viewer.Restarts);
        var stored = settings.Load().Connection!;
        Assert.Null(stored.ProtectedToken);
        Assert.Equal(viewer.Connection.Endpoint.Fingerprint, stored.Endpoint.Fingerprint);
        Assert.Equal(viewer.Connection.Endpoint.Addresses, stored.Endpoint.Addresses);
        Assert.Equal("Sam", stored.AccountName);
        Assert.Equal(StartupChoice.ShowConnect, ModeGate.Decide(settings.TryLoad(), databaseExists: false));
        Assert.Null(world.Services.GetRequiredService<DeviceStore>().ByToken(viewer.Token));
    }

    [AvaloniaFact]
    public async Task SignOut_ServerGone_StillSignsOutHere()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        var account = viewer.Services.GetRequiredService<SettingsViewModel>().Account!;
        await world.Sharing.SetOnAsync(false);
        await Ui.WaitUntil(() => viewer.Monitor.State == ConnectionState.Unreachable);

        await account.SignOutCommand.ExecuteAsync(null);

        Assert.Equal(1, viewer.Restarts);
        Assert.Null(viewer.Services.GetRequiredService<SettingsStore>().Load().Connection!.ProtectedToken);
    }

    [AvaloniaFact]
    public async Task UseThisPc_SetsMode_Restarts()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var account = viewer.Services.GetRequiredService<SettingsViewModel>().Account!;
        var settings = viewer.Services.GetRequiredService<SettingsStore>();
        var outbox = viewer.Paths.Outbox(viewer.Connection.Endpoint.Fingerprint, "Sam");
        await File.WriteAllTextAsync(viewer.Paths.Database, "kept", Ct);
        await File.WriteAllTextAsync(outbox, "[]", Ct);

        await account.UseThisPcCommand.ExecuteAsync(null);

        Assert.Equal(1, viewer.Restarts);
        var saved = settings.Load();
        Assert.Equal(AppMode.ThisPc, saved.Mode);
        Assert.Equal(viewer.Connection.ProtectedToken, saved.Connection!.ProtectedToken);
        Assert.Equal(viewer.Connection.Endpoint.Fingerprint, saved.Connection.Endpoint.Fingerprint);
        Assert.True(File.Exists(viewer.Paths.Database));
        Assert.True(File.Exists(outbox));
        Assert.Equal(StartupChoice.RunLocal, ModeGate.Decide(settings.TryLoad(), databaseExists: true));
    }

    [Fact]
    public void UseThisPc_RestartRefused_LeavesThisRunGoing()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { Mode = AppMode.Server });
        var change = new ModeSwitch(store, new AppRestart(null, (_, _) => { }, () => { }), _hub);

        Assert.Equal("Restart MediaBoard to open this PC's library.", change.UseThisPc());
        Assert.Equal(AppMode.ThisPc, store.Load().Mode);
    }

    [Fact]
    public void UseThisPc_SaveRefused_SaysThat_AndDoesNotRestart()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { Mode = AppMode.Server });
        var restarts = new List<string>();
        var change = new ModeSwitch(store, new AppRestart("MediaBoard.exe", (_, _) => restarts.Add("restart"), () => { }), _hub);

        string? said;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Delete)) said = change.UseThisPc();

        Assert.Equal("Couldn't save this change. See the activity panel.", said);
        Assert.Empty(restarts);
    }

    [AvaloniaFact]
    public async Task SignOut_MidPlay_TheServerHasThePosition()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var account = viewer.Services.GetRequiredService<SettingsViewModel>().Account!;
        var file = await PlayAsync(viewer);

        await account.SignOutCommand.ExecuteAsync(null);

        // Nothing has run the exit path, and the token is dead, so only a send before the sign-out can have put it there.
        Assert.True(viewer.Player.Closed.Task.IsCompleted);
        var sam = world.Services.GetRequiredService<AccountStore>().All().Single(a => a.Name == "Sam");
        Assert.Equal(115, world.Services.GetRequiredService<ProgressStore>().Get(sam.Id, file)?.PositionS);
        Assert.Null(world.Services.GetRequiredService<DeviceStore>().ByToken(viewer.Token));
    }

    [AvaloniaFact]
    public async Task SignOut_WhileStillConnecting_TellsTheServer()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var account = viewer.Services.GetRequiredService<SettingsViewModel>().Account!;
        Assert.Equal(ConnectionState.Connecting, viewer.Monitor.State);

        await account.SignOutCommand.ExecuteAsync(null);

        Assert.Null(world.Services.GetRequiredService<DeviceStore>().ByToken(viewer.Token));
    }

    [AvaloniaFact]
    public async Task SignOut_RestartRefused_TheTokenInMemoryStopsWorking()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world, restartWorks: false);
        var account = viewer.Services.GetRequiredService<SettingsViewModel>().Account!;
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        await world.Sharing.SetOnAsync(false);
        await Ui.WaitUntil(() => viewer.Monitor.State == ConnectionState.Unreachable);

        await account.SignOutCommand.ExecuteAsync(null);
        await world.Sharing.SetOnAsync(true);
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct); // longer than the monitor's wait from its first failure

        Assert.Contains("Restart", account.LeaveMessage, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.SignedOut, viewer.Monitor.State);
        Assert.NotNull(world.Services.GetRequiredService<DeviceStore>().ByToken(viewer.Token)); // the server never heard
        var refused = await Assert.ThrowsAsync<ApiException>(() => viewer.Services.GetRequiredService<ApiClient>().GetAsync<MeReply>("me", Ct));
        Assert.Equal(ApiErrorCode.Unauthorized, refused.Code);
    }

    private ModeSwitch Switch(SettingsStore store, FakeRunKey key, Func<ConnectViewModel, Task>? show = null, List<string>? restarts = null, Func<string, Task<bool>>? confirm = null)
    {
        var restart = new AppRestart("MediaBoard.exe", (_, _) => restarts?.Add("restart"), () => { });
        var startWithWindows = new StartWithWindows(store, key, "MediaBoard.exe", _hub);
        return new ModeSwitch(store, restart, _hub, startWithWindows, show, "PC", confirm);
    }

    private static StoredConnection Saved(string? token) => new(Endpoint, "Sam", "PC", token);

    [AvaloniaFact]
    public async Task ConnectToServer_FromThisPc_OpensConnect_AndChangesNothingYet()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { Mode = AppMode.ThisPc, Sharing = new SharingSettings { StartWithWindows = true } });
        var key = new FakeRunKey();
        key.Set("on");
        ConnectViewModel? shown = null;
        var restarts = new List<string>();

        var said = await Switch(store, key, connect =>
        {
            shown = connect;
            return Task.CompletedTask;
        }, restarts).ConnectToServerAsync();

        Assert.Null(said);
        Assert.NotNull(shown);
        Assert.Equal(ConnectStep.Invite, shown.Step);
        Assert.Equal(AppMode.ThisPc, store.Load().Mode);
        Assert.Equal("on", key.Command);
        Assert.Empty(restarts);
    }

    [AvaloniaFact]
    public async Task ConnectToServer_SignedOutDevice_OffersItsServer()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { Mode = AppMode.ThisPc, Connection = Saved(null) });
        ConnectViewModel? shown = null;

        await Switch(store, new FakeRunKey(), connect =>
        {
            shown = connect;
            return Task.CompletedTask;
        }).ConnectToServerAsync();

        Assert.Equal(ConnectStep.SignIn, shown!.Step);
        Assert.Equal("Sam", shown.Name);
    }

    [AvaloniaFact]
    public async Task ConnectToServer_ClosedAsTheAnswerArrived_SaysToRestart()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { Mode = AppMode.ThisPc });

        // Connect saves and leaves the restart to a window that has gone.
        var said = await Switch(store, new FakeRunKey(), _ =>
        {
            store.Update(s => s with { Mode = AppMode.Server });
            return Task.CompletedTask;
        }).ConnectToServerAsync();

        Assert.Equal("Restart MediaBoard to connect.", said);
    }

    [AvaloniaFact]
    public async Task ConnectToServer_TokenStillHeld_GoesBackWithoutAsking()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { Mode = AppMode.ThisPc, Connection = Saved("protected") });
        var restarts = new List<string>();

        var said = await Switch(store, new FakeRunKey(), _ => throw new InvalidOperationException("A held token needs no Connect screen."), restarts).ConnectToServerAsync();

        Assert.Null(said);
        Assert.Equal(AppMode.Server, store.Load().Mode);
        Assert.Equal("protected", store.Load().Connection!.ProtectedToken);
        Assert.Equal(["restart"], restarts);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SwitchingToAServer_TurnsStartWithWindowsOff_OnlyWhenItIsOn(bool on)
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { Mode = AppMode.ThisPc, Connection = Saved("protected"), Sharing = new SharingSettings { StartWithWindows = on } });
        var key = new FakeRunKey();
        if (on) key.Set("on");
        else key.Refuses = true; // a write it didn't need would log a failure here

        await Switch(store, key).ConnectToServerAsync();

        Assert.Null(key.Command);
        Assert.False(store.Load().Sharing.StartWithWindows);
        Assert.Empty(_hub.Recent);
    }

    [AvaloniaFact]
    public async Task ConnectToServer_SharingOn_AsksFirst_AndNoLeavesEverythingAlone()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { Mode = AppMode.ThisPc, Connection = Saved("protected") });
        _hub.SetSharing("Sharing: 2 watching");
        var asked = new List<string>();
        var restarts = new List<string>();

        await Switch(store, new FakeRunKey(), null, restarts, question =>
        {
            asked.Add(question);
            return Task.FromResult(false);
        }).ConnectToServerAsync();

        Assert.Contains("stops sharing", Assert.Single(asked), StringComparison.Ordinal);
        Assert.Equal(AppMode.ThisPc, store.Load().Mode);
        Assert.Empty(restarts);
    }

    // ---- What each state says ----

    public static TheoryData<string, string> Rows => new()
    {
        { "unreachable", "Can't reach Anna's library. Their PC may be off or asleep, or sharing is off." },
        { "signed out", "You've been signed out of this library." },
        { "identity", "This server's identity changed. Ask for a new invite." },
        { "this PC behind", "Update MediaBoard on this PC." },
        { "server behind", "Update MediaBoard on the owner's PC." },
        { "invite dead", "This invite no longer works. Ask for a new one." },
        { "locked", "Too many tries. Wait 15 minutes." },
        { "file missing", "File missing" },
        { "nothing shared", "Nothing is shared with you yet." },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void FailureText_EveryRow(string situation, string expected)
    {
        var said = situation switch
        {
            "unreachable" => FailureText.For(ConnectionState.Unreachable, Server, null),
            "signed out" => FailureText.For(ConnectionState.SignedOut, Server, null),
            "identity" => FailureText.For(ConnectionState.IdentityChanged, Server, null),
            "this PC behind" => FailureText.For(ConnectionState.VersionMismatch, Server, ApiVersion.Current + 1),
            "server behind" => FailureText.For(ConnectionState.VersionMismatch, Server, ApiVersion.Current - 1),
            "invite dead" => FailureText.InviteDead,
            "locked" => FailureText.Locked,
            "file missing" => FailureText.FileMissing,
            "nothing shared" => FailureText.NothingShared,
            _ => throw new ArgumentOutOfRangeException(nameof(situation)),
        };

        Assert.Equal(expected, said);
    }

    private async Task<(MainWindowViewModel Shell, ConnectionPanelViewModel Panel, ConnectionMonitor Monitor)> ShellWithNothingLoadedAsync()
    {
        var remote = RemoteShell();
        var failures = 0;
        _library.OnQuery = _ =>
        {
            Interlocked.Increment(ref failures);
            throw new ServerUnreachableException();
        };
        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => Volatile.Read(ref failures) > 0);
        return remote;
    }

    private async Task<(MainWindowViewModel Shell, ConnectionPanelViewModel Panel, ConnectionMonitor Monitor)> ShellWithDataAsync()
    {
        var remote = RemoteShell();
        _library.Recent.Add(FakeLibrary.Card(1, "Heat"));
        remote.Monitor.Report(ConnectionEvent.Listening);
        Ui.Flush();
        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => remote.Shell.CurrentPage is HomeViewModel { IsLoaded: true, NothingAdded: false });
        return remote;
    }

    [AvaloniaFact]
    public async Task Unreachable_NothingLoaded_ShowsFullPanel_WithRetry()
    {
        var (shell, panel, monitor) = await ShellWithNothingLoadedAsync();

        monitor.Report(ConnectionEvent.Unreachable);
        Ui.Flush();

        Assert.True(shell.IsPageCovered);
        Assert.Equal(FailureText.For(ConnectionState.Unreachable, Server, null), panel.Message);
        Assert.Equal("Retry", panel.ActionLabel);

        panel.ActCommand.Execute(null);
        Ui.Flush();

        Assert.Equal(ConnectionState.Connecting, monitor.State);
        Assert.True(shell.IsPageCovered);
        Assert.Equal($"Connecting to {Server}…", panel.Message);
    }

    // A timeout, an error with no code and unreadable JSON all throw this without telling the monitor.
    [AvaloniaFact]
    public async Task LoadFails_WhileConnected_SaysSo_AndRetryReloadsThePage()
    {
        var (shell, panel, monitor) = RemoteShell();
        monitor.Report(ConnectionEvent.Listening);
        Ui.Flush();
        var failing = true;
        _library.OnQuery = _ =>
        {
            if (Volatile.Read(ref failing)) throw new ServerUnreachableException();
        };

        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => shell.IsPageCovered);

        Assert.Equal(FailureText.LoadFailed, panel.Message);
        Assert.Equal("Retry", panel.ActionLabel);
        Assert.Equal(AppCapabilities.RemoteFailureHint, _hub.Recent[^1].Hint);
        Assert.Equal(ConnectionState.Connected, monitor.State);

        _library.Recent.Add(FakeLibrary.Card(1, "Heat"));
        Volatile.Write(ref failing, false);
        panel.ActCommand.Execute(null);

        await Ui.WaitUntil(() => !shell.IsPageCovered);
        Assert.True(((HomeViewModel)shell.CurrentPage).IsLoaded);
    }

    [AvaloniaFact]
    public async Task LoadFails_WhileConnected_RetryThatFails_StaysCovered()
    {
        var (shell, panel, monitor) = RemoteShell();
        monitor.Report(ConnectionEvent.Listening);
        Ui.Flush();
        var tries = 0;
        _library.OnQuery = _ =>
        {
            Interlocked.Increment(ref tries);
            throw new ServerUnreachableException();
        };
        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => shell.IsPageCovered);
        var before = Volatile.Read(ref tries);

        panel.ActCommand.Execute(null);
        await Ui.WaitUntil(() => Volatile.Read(ref tries) > before);
        await Task.Delay(50, Ct);
        Ui.Flush();

        Assert.True(shell.IsPageCovered);
        Assert.Equal(FailureText.LoadFailed, panel.Message);
    }

    [AvaloniaFact]
    public async Task Unreachable_WithData_ShowsStatusLineOnly()
    {
        var (shell, _, monitor) = await ShellWithDataAsync();

        monitor.Report(ConnectionEvent.Unreachable);
        Ui.Flush();

        Assert.False(shell.IsPageCovered);
        Assert.Equal("Can't reach the server", shell.StatusBar.Connection);
    }

    [AvaloniaFact]
    public void Starting_NothingLoaded_SaysConnecting_WithNothingToPress()
    {
        var (shell, panel, _) = RemoteShell();

        Assert.True(shell.IsPageCovered);
        Assert.Equal($"Connecting to {Server}…", panel.Message);
        Assert.Null(panel.ActionLabel);
        Assert.False(panel.HasAction);
    }

    [AvaloniaFact]
    public async Task IdentityChanged_RetryReachesTheMonitor()
    {
        var (shell, panel, monitor) = await ShellWithDataAsync();

        monitor.Report(ConnectionEvent.IdentityChanged);
        Ui.Flush();

        Assert.True(shell.IsPageCovered); // nothing on the page can be refreshed, and only the panel says what to do
        Assert.Equal(FailureText.IdentityChanged, panel.Message);
        Assert.Equal("Retry", panel.ActionLabel);

        panel.ActCommand.Execute(null);
        Ui.Flush();

        Assert.Equal(ConnectionState.Connecting, monitor.State);
    }

    [AvaloniaFact]
    public async Task IdentityChanged_RetryRunsAnotherTry_AgainstARealServer()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        viewer.Services.GetRequiredService<MainWindowViewModel>();
        var panel = viewer.Services.GetRequiredService<ConnectionPanelViewModel>();
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        viewer.Monitor.Report(ConnectionEvent.IdentityChanged);
        await Ui.WaitUntil(() => panel.ActionLabel == "Retry");

        panel.ActCommand.Execute(null);

        await viewer.ConnectedAsync();
    }

    [AvaloniaFact]
    public async Task VersionMismatch_ShowsThePanel_WithRetry_NotOffline()
    {
        var certificate = new CertificateStore(Path.Combine(_dir, "certificate.bin")).LoadOrCreate();
        var port = SharingWorld.FreePort();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRoutingCore();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port, listen => listen.UseHttps(certificate)));
        await using var older = builder.Build();
        older.Run(context => Results.Json(new PingReply(ApiVersion.Current - 1, "Older"), ApiJson.Options).ExecuteAsync(context));
        await older.StartAsync(Ct);

        ConnectionMonitor? monitor = null;
        using var client = new ApiClient(
            new ServerEndpoint(Server, [$"127.0.0.1:{port}"], CertificateStore.Fingerprint(certificate)), () => null, happened => monitor?.Report(happened));
        monitor = new ConnectionMonitor(client);
        var panel = new ConnectionPanelViewModel(monitor, Server, () => true);
        var capabilities = AppCapabilities.Remote(monitor);
        var shell = new MainWindowViewModel(
            _library, new StatusBarViewModel(_hub), new ActivityPanelViewModel(_hub), _hub, null,
            NewSettings(capabilities), Posters.None(), _titles.Services() with { Capabilities = capabilities },
            capabilities: capabilities, connection: panel);
        using var stop = new CancellationTokenSource();

        var running = Task.Run(() => monitor.RunAsync(stop.Token), Ct);
        await Ui.WaitUntil(() => panel.Message == "Update MediaBoard on the owner's PC.");

        Assert.True(shell.IsPageCovered);
        Assert.Equal("Retry", panel.ActionLabel);
        Assert.Equal("Update MediaBoard", shell.StatusBar.Connection);
        await stop.CancelAsync();
        await running;
    }

    [AvaloniaFact]
    public void StatusBar_Remote_NamesEachState()
    {
        var (shell, _, monitor) = RemoteShell();
        Assert.Equal("Connecting…", shell.StatusBar.Connection);
        Assert.Null(shell.StatusBar.Progress); // "Up to date" says nothing about a server

        monitor.Report(ConnectionEvent.Listening);
        Ui.Flush();
        Assert.Equal($"Connected to {Server}", shell.StatusBar.Connection);

        monitor.Report(ConnectionEvent.IdentityChanged);
        Ui.Flush();
        Assert.Equal("Server identity changed", shell.StatusBar.Connection);
    }

    [AvaloniaFact]
    public void StatusBar_SignedOut_SaysSo()
    {
        var (shell, _, monitor) = RemoteShell();

        monitor.Report(ConnectionEvent.SignedOut);
        Ui.Flush();

        Assert.Equal("Signed out", shell.StatusBar.Connection);
    }

    [AvaloniaFact]
    public async Task Panel_HearsAChangeFromAnotherThread_OnTheUiThread()
    {
        var (shell, panel, monitor) = RemoteShell();
        var heardOnTheUiThread = new List<bool>();
        panel.PropertyChanged += (_, _) => heardOnTheUiThread.Add(Dispatcher.UIThread.CheckAccess());

        await Task.Run(() => monitor.Report(ConnectionEvent.Unreachable), Ct);
        Ui.Flush();

        Assert.Equal(FailureText.For(ConnectionState.Unreachable, Server, null), panel.Message);
        Assert.Equal("Can't reach the server", shell.StatusBar.Connection);
        Assert.NotEmpty(heardOnTheUiThread);
        Assert.All(heardOnTheUiThread, Assert.True);
    }

    [AvaloniaFact]
    public async Task Settings_StaysOpenInEveryState_SoAViewerCanLeave()
    {
        var (shell, _, monitor) = await ShellWithNothingLoadedAsync();
        monitor.Report(ConnectionEvent.SignedOut);
        Ui.Flush();
        Assert.True(shell.IsPageCovered);

        shell.NavigateCommand.Execute(MainWindowViewModel.SettingsPage);
        Ui.Flush();

        Assert.False(shell.IsPageCovered);
    }

    [AvaloniaFact]
    public async Task Reconnect_ReloadsCurrentPage()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var shell = viewer.Services.GetRequiredService<MainWindowViewModel>();
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        shell.NavigateCommand.Execute(MainWindowViewModel.MoviesPage);
        var grid = (TitleGridViewModel)shell.CurrentPage;
        await Ui.WaitUntil(() => grid.IsLoaded);
        Assert.Empty(grid.Items);

        await world.Sharing.SetOnAsync(false);
        await Ui.WaitUntil(() => viewer.Monitor.State == ConnectionState.Unreachable);
        await world.AddLongMovieAsync();
        await world.Sharing.SetOnAsync(true);

        await Ui.WaitUntil(() => grid.Items.Count == 1);
        Assert.Equal("Movie", grid.Items[0].Name);
    }

    [AvaloniaFact]
    public async Task SignedOut_ReturnsToConnect_WithReason()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var shell = viewer.Services.GetRequiredService<MainWindowViewModel>();
        var panel = viewer.Services.GetRequiredService<ConnectionPanelViewModel>();
        var settings = viewer.Services.GetRequiredService<SettingsStore>();
        await world.AddLongMovieAsync();
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        var home = (HomeViewModel)shell.CurrentPage;
        await Ui.WaitUntil(() => home is { IsLoaded: true, NothingAdded: false });

        var device = world.Services.GetRequiredService<DeviceStore>().ByToken(viewer.Token)!;
        world.Accounts.SignOutDevice(device.Id);

        await Ui.WaitUntil(() => viewer.Monitor.State == ConnectionState.SignedOut && panel.Message == FailureText.SignedOut);
        Assert.True(shell.IsPageCovered);
        Assert.Equal("Sign in", panel.ActionLabel);
        await Ui.WaitUntil(() => settings.Load().Connection!.ProtectedToken is null);

        var stored = settings.Load().Connection!;
        Assert.Equal(viewer.Connection.Endpoint.Fingerprint, stored.Endpoint.Fingerprint);
        Assert.Equal(viewer.Connection.Endpoint.Addresses, stored.Endpoint.Addresses);
        Assert.Equal(StartupChoice.ShowConnect, ModeGate.Decide(settings.TryLoad(), databaseExists: false));

        panel.ActCommand.Execute(null);
        Assert.Equal(1, viewer.Restarts);

        // What the next process opens with.
        using var connect = new ConnectViewModel(settings, "PC", () => true, stored);
        Assert.Equal(ConnectStep.SignIn, connect.Step);
        Assert.Equal("Sam", connect.Name);
        Assert.Contains(viewer.Connection.Endpoint.ServerName, connect.Note, StringComparison.Ordinal);
        Assert.Equal(FailureText.SignedOut, connect.Message);
    }

    [AvaloniaFact]
    public async Task UndecryptableToken_ClearsIt_SoTheNextStartIsConnect()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world, signIn: false);
        viewer.Services.GetRequiredService<MainWindowViewModel>();
        var settings = viewer.Services.GetRequiredService<SettingsStore>();

        viewer.Host.Start();

        await Ui.WaitUntil(() => settings.Load().Connection!.ProtectedToken is null);
        Assert.Equal(StartupChoice.ShowConnect, ModeGate.Decide(settings.TryLoad(), databaseExists: false));
    }

    [AvaloniaFact]
    public async Task NothingShared_EmptyState()
    {
        var (shell, panel, monitor) = RemoteShell();
        monitor.Report(ConnectionEvent.Listening);
        Ui.Flush();
        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => shell.IsPageCovered);

        Assert.Equal(FailureText.NothingShared, panel.Message);
        Assert.Null(panel.ActionLabel);

        // Every library page has the one answer, with no change per page.
        shell.NavigateCommand.Execute(MainWindowViewModel.MoviesPage);
        Ui.Flush();
        Assert.True(shell.IsPageCovered);

        _library.Recent.Add(FakeLibrary.Card(1, "Heat"));
        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => !shell.IsPageCovered);
    }

    [AvaloniaFact]
    public async Task NothingShared_LeavesAnOnlineTitleAlone()
    {
        var (shell, _, monitor) = RemoteShell();
        monitor.Report(ConnectionEvent.Listening);
        Ui.Flush();
        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => shell.IsPageCovered);

        shell.OpenOnlineTitle(603, TitleKind.Movie);
        Ui.Flush();

        Assert.False(shell.IsPageCovered);
    }

    [AvaloniaFact]
    public async Task Hint_Remote_NotRestart()
    {
        var (_, _, _) = RemoteShell();
        _library.OnQuery = _ => throw new ApiException(ApiErrorCode.BadRequest);

        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => _hub.Recent.Count > 0);

        var entry = _hub.Recent[^1];
        Assert.Equal(AppCapabilities.RemoteFailureHint, entry.Hint);
        Assert.DoesNotContain("Restart", entry.Hint!, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(ApiErrorCode.BadRequest), entry.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Hint_Remote_CoversTheTitleAndListPages()
    {
        var capabilities = AppCapabilities.Remote(Monitor());
        _library.OnQuery = _ => throw new ApiException(ApiErrorCode.BadRequest);
        var page = new TitleViewModel(1, _titles.Services() with { Capabilities = capabilities }, _titles.Navigation());
        var list = new ListViewModel(ListKind.RecentlyAdded, _library, _hub, Posters.None(), new PageActions(_ => { }, _ => { }, _ => { }), capabilities);

        await page.RefreshAsync();
        await list.RefreshAsync();

        Assert.Equal(2, _hub.Recent.Count);
        Assert.All(_hub.Recent, entry => Assert.Equal(AppCapabilities.RemoteFailureHint, entry.Hint));
    }

    // ---- The keyboard ----

    private static void Settle(Window window)
    {
        Ui.Flush();
        window.UpdateLayout();
        Ui.Flush();
    }

    private static object? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    private async Task<(MainWindow Window, TextBox Filter, MainWindowViewModel Shell, ConnectionPanelViewModel Panel, ConnectionMonitor Monitor)> GridInAWindowAsync()
    {
        var (shell, panel, monitor) = await ShellWithDataAsync();
        _library.TitleCards.Add(FakeLibrary.Card(1, "Heat"));
        shell.NavigateCommand.Execute(MainWindowViewModel.MoviesPage);
        var grid = (TitleGridViewModel)shell.CurrentPage;
        await Ui.WaitUntil(() => grid.IsLoaded);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Settle(window);
        var filter = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "FilterBox");
        filter.Focus();
        Settle(window);
        Assert.Same(filter, Focused(window));
        return (window, filter, shell, panel, monitor);
    }

    [AvaloniaFact]
    public async Task Panel_OverAFocusedField_TakesTheKeyboard_ThenLeavesTheFocusOnSomethingVisible()
    {
        var (window, filter, shell, _, monitor) = await GridInAWindowAsync();
        var grid = (TitleGridViewModel)shell.CurrentPage;

        monitor.Report(ConnectionEvent.IdentityChanged);
        Settle(window);

        var retry = Assert.IsType<Button>(Focused(window));
        Assert.Equal("Retry", retry.Content);
        window.KeyTextInput("x");
        Settle(window);
        Assert.Equal("", grid.FilterText);

        monitor.Retry();
        monitor.Report(ConnectionEvent.Listening);
        Settle(window);

        Assert.False(shell.IsPageCovered);
        Assert.True(filter.IsEffectivelyVisible);
        Assert.True(((InputElement)Focused(window)!).IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Retry_OnACoveredPageWithData_StaysCovered_AndKeepsTheFocus()
    {
        var (window, _, shell, panel, monitor) = await GridInAWindowAsync();
        monitor.Report(ConnectionEvent.IdentityChanged);
        Settle(window);
        var retry = Assert.IsType<Button>(Focused(window));

        panel.ActCommand.Execute(null);
        Settle(window);

        Assert.Equal(ConnectionState.Connecting, monitor.State);
        Assert.True(shell.IsPageCovered);
        Assert.Same(retry, Focused(window));

        monitor.Report(ConnectionEvent.IdentityChanged);
        Settle(window);

        Assert.True(shell.IsPageCovered);
        Assert.Same(retry, Focused(window));

        panel.ActCommand.Execute(null);
        monitor.Report(ConnectionEvent.Listening);
        Settle(window);

        Assert.False(shell.IsPageCovered);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Panel_UnderAModalDialog_LeavesTheDialogItsKeyboard()
    {
        var (window, _, shell, _, monitor) = await GridInAWindowAsync();
        var pressed = 0;
        var ok = new Button { Content = "OK" };
        ok.Click += (_, _) => pressed++;
        var dialog = new Window { Width = 300, Height = 200, Content = ok };
        var showing = dialog.ShowDialog(window);
        Settle(dialog);
        ok.Focus();
        Settle(dialog);

        monitor.Report(ConnectionEvent.IdentityChanged);
        Settle(window);
        Settle(dialog);

        Assert.True(shell.IsPageCovered);
        Assert.True(dialog.IsVisible);
        Assert.Same(ok, Focused(dialog));

        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(dialog);

        Assert.Equal(1, pressed);
        Assert.Equal(ConnectionState.IdentityChanged, monitor.State); // Retry behind the dialog heard nothing
        dialog.Close();
        await showing;
        window.Close();
    }

    [AvaloniaFact]
    public async Task Panel_NoButton_LeavesTheFocusOnTheWindow_SoCtrlFStillWorks()
    {
        var (window, _, shell, _, monitor) = await GridInAWindowAsync();
        _library.Recent.Clear();
        _library.RaiseChanged();
        Ui.Flush();

        await Ui.WaitUntil(() => shell.IsPageCovered);
        Settle(window);

        Assert.True(((InputElement)Focused(window)!).IsEffectivelyVisible);
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        Settle(window);
        Assert.True(shell.IsSearchOpen);
        monitor.Report(ConnectionEvent.Unreachable);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Panel_RetryThatConnectsToNothing_DropsTheButton_AndTheFocusWithIt()
    {
        var (shell, panel, monitor) = await ShellWithNothingLoadedAsync();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        monitor.Report(ConnectionEvent.Unreachable);
        Settle(window);
        Assert.IsType<Button>(Focused(window));

        _library.OnQuery = null;
        monitor.Retry();
        monitor.Report(ConnectionEvent.Listening);
        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => panel.Message == FailureText.NothingShared);
        Settle(window);

        Assert.True(shell.IsPageCovered);
        Assert.True(((InputElement)Focused(window)!).IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PasswordForm_OpensOnItsFirstField_AndClosesOnItsButton()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var settings = viewer.Services.GetRequiredService<SettingsViewModel>();
        var window = Show(new SettingsView { DataContext = settings });
        var view = (Control)window.Content!;
        var change = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ChangePasswordButton");
        var account = settings.Account!;

        account.BeginChangePasswordCommand.Execute(null);
        Settle(window);
        var box = view.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "CurrentPasswordBox");
        Assert.Same(box, Focused(window));
        Assert.False(change.IsVisible);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(window);

        Assert.False(account.IsChangingPassword);
        Assert.Same(change, Focused(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task PasswordForm_EnterSaves()
    {
        await using var world = new SharingWorld();
        var (viewer, account) = await JoinWithPasswordAsync(world);
        await using var _ = viewer;
        var window = Show(new SettingsView { DataContext = viewer.Services.GetRequiredService<SettingsViewModel>() });
        account.BeginChangePasswordCommand.Execute(null);
        Settle(window);
        account.CurrentPassword = "not the password";
        account.NewPassword = "another good password";
        account.RepeatPassword = "another good password";

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        await Ui.WaitUntil(() => account.PasswordMessage == "Wrong password. Try again.");
        Settle(window);
        Assert.True(account.IsChangingPassword);
        var box = ((Control)window.Content!).GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "CurrentPasswordBox");
        Assert.Same(box, Focused(window));
        window.Close();
    }

    [AvaloniaFact]
    public void ConnectDialog_Back_Closes_AndTheFieldHasTheFocus()
    {
        using var connect = new ConnectViewModel(new SettingsStore(SettingsPath), "PC", () => true);
        var owner = new Window();
        owner.Show();
        var dialog = new ConnectDialog(connect);
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        _ = dialog.ShowDialog(owner);
        Settle(dialog);
        Assert.Equal("InviteBox", Assert.IsType<TextBox>(Focused(dialog)).Name);

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(dialog);

        Assert.True(closed);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task ConnectToServer_Dialogs_NoWindow_ChangesNothing()
    {
        var dialogs = new Dialogs();
        using var connect = new ConnectViewModel(new SettingsStore(SettingsPath), "PC", () => true);

        await dialogs.ShowConnectAsync(connect);

        Assert.Equal(ConnectStep.Invite, connect.Step);
    }

    public static TheoryData<Exception> ConnectionFailures => [new ServerUnreachableException(), new IdentityChangedException(), new ApiException(ApiErrorCode.Unauthorized)];

    // The panel and the status bar say this already. A red line in the strip as well would be the third telling.
    [AvaloniaTheory]
    [MemberData(nameof(ConnectionFailures))]
    public async Task LoadFailure_AboutTheConnection_IsLeftToTheShell(Exception failure)
    {
        var (shell, _, _) = RemoteShell();
        var failed = 0;
        _library.OnQuery = _ =>
        {
            Interlocked.Increment(ref failed);
            throw failure;
        };

        _library.RaiseChanged();
        Ui.Flush();
        await Ui.WaitUntil(() => Volatile.Read(ref failed) > 0);
        await Task.Delay(50, Ct);
        Ui.Flush();

        Assert.Empty(_hub.Recent);
        Assert.Null(shell.StatusBar.Problem);
    }

    // ---- The palette ----

    private static readonly SearchCandidate Severance = new(95396, TitleKind.Show, "Severance", null, 2022, 40, 8.4, null, null);

    private readonly List<int> _opened = [];

    private SearchPaletteViewModel Palette(FakeSearch search, ManualClock clock) => new(
        search, _library, new PaletteActions(_opened.Add, (_, _) => { }, () => { }, () => Task.CompletedTask, () => { }), _hub, clock, () => { },
        AppCapabilities.Remote(Monitor()));

    private static string[] Texts(SearchPaletteViewModel palette) => [.. palette.Rows.Select(r => r.Text)];

    [AvaloniaFact]
    public async Task Palette_Remote_TwoRequestsPerSettledText_NonePerKeystroke()
    {
        var hit = FakeLibrary.Card(1, "The Matrix");
        var search = new FakeSearch();
        var asked = new List<string>();
        _library.LocalSearch = query =>
        {
            lock (asked) asked.Add(query);
            return [hit];
        };
        var clock = new ManualClock();
        var palette = Palette(search, clock);

        palette.Query = "m";
        palette.Query = "ma";
        palette.Query = "mat";
        await Task.Delay(50, Ct);
        Ui.Flush();
        Assert.Empty(asked);
        Assert.Empty(search.Queries);

        clock.Advance(SearchPaletteViewModel.OnlineDelay);
        await Ui.WaitUntil(() => Texts(palette).Contains("The Matrix") && search.Queries.Count == 1);
        await palette.Settled;

        Assert.Equal(["mat"], asked);
        Assert.Equal(["mat"], search.Queries);
        Assert.Empty(_hub.Recent);
    }

    [AvaloniaFact]
    public async Task Palette_Remote_LastTextsHits_GoWithIt_SoEnterOpensNothingOld()
    {
        _library.LocalSearch = query => query == "heat" ? [FakeLibrary.Card(1, "Heat")] : [FakeLibrary.Card(2, "The Matrix")];
        var late = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new FakeSearch { OnSearch = (query, _) => query == "heat" ? Task.FromResult(new SearchResults([], [], OnlineState.Ok)) : late.Task };
        var clock = new ManualClock();
        var palette = Palette(search, clock);
        palette.Query = "heat";
        clock.Advance(SearchPaletteViewModel.OnlineDelay);
        await Ui.WaitUntil(() => Texts(palette).Contains("Heat"));

        palette.Query = "matrix";

        Assert.DoesNotContain("Heat", Texts(palette));
        Assert.Equal([SearchPaletteViewModel.SearchingNote], Texts(palette));
        palette.OpenSelectedCommand.Execute(null);
        Assert.Empty(_opened);
        late.SetResult(new SearchResults([], [], OnlineState.Ok));
    }

    [AvaloniaFact]
    public void Palette_Remote_SaysSearching_FromTheFirstKey()
    {
        var palette = Palette(new FakeSearch(), new ManualClock());

        palette.Query = "m";

        Assert.Equal([SearchPaletteViewModel.SearchingNote], Texts(palette));
        Assert.Null(palette.Selected);
    }

    [AvaloniaFact]
    public async Task Palette_Remote_NoHitsAnywhere_EndsOnNoMatches()
    {
        _library.LocalSearch = _ => [];
        var clock = new ManualClock();
        var palette = Palette(new FakeSearch(), clock);

        palette.Query = "nothing";
        clock.Advance(SearchPaletteViewModel.OnlineDelay);

        await Ui.WaitUntil(() => palette.EmptyText == SearchPaletteViewModel.NoMatches);
        Assert.Empty(Texts(palette));
    }

    [AvaloniaFact]
    public async Task Palette_Remote_LibraryHits_DoNotWaitForTmdb()
    {
        _library.LocalSearch = _ => [FakeLibrary.Card(1, "The Matrix")];
        var never = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualClock();
        var palette = Palette(new FakeSearch { OnSearch = (_, _) => never.Task }, clock);

        palette.Query = "matrix";
        clock.Advance(SearchPaletteViewModel.OnlineDelay);

        await Ui.WaitUntil(() => Texts(palette).Contains("The Matrix"));
        Assert.Equal(1, palette.Selected?.Text == "The Matrix" ? 1 : 0);
        never.SetResult(new SearchResults([], [], OnlineState.Ok));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Palette_Remote_EitherAnswerFirst_LibraryStaysOnTop(bool libraryFirst)
    {
        using var local = new ManualResetEventSlim();
        var online = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new SearchResults([], [new OnlineResult(Severance, null)], OnlineState.Ok);
        _library.LocalSearch = _ =>
        {
            Assert.True(local.Wait(TimeSpan.FromSeconds(15), Ct));
            return [FakeLibrary.Card(1, "The Matrix")];
        };
        var search = new FakeSearch { OnSearch = (_, _) => online.Task };
        var clock = new ManualClock();
        var palette = Palette(search, clock);

        palette.Query = "matrix";
        clock.Advance(SearchPaletteViewModel.OnlineDelay);
        await Ui.WaitUntil(() => search.Queries.Count == 1);

        if (libraryFirst)
        {
            local.Set();
            await Ui.WaitUntil(() => Texts(palette).Contains("The Matrix"));
            online.SetResult(answer);
        }
        else
        {
            online.SetResult(answer);
            await Ui.WaitUntil(() => Texts(palette).Contains("Severance"));
            local.Set();
        }

        await Ui.WaitUntil(() => Texts(palette).Length == 4);
        Assert.Equal([SearchPaletteViewModel.LibraryHeader, "The Matrix", SearchPaletteViewModel.OnlineHeader, "Severance"], Texts(palette));
    }

    [AvaloniaFact]
    public async Task Palette_Remote_SearchLegsOwnLibraryHits_LeaveTheLocalOnesInPlace()
    {
        _library.LocalSearch = _ => [FakeLibrary.Card(1, "The Matrix")];
        var search = new FakeSearch { OnSearch = (_, _) => Task.FromResult(new SearchResults([FakeLibrary.Card(9, "Something Else")], [], OnlineState.Ok)) };
        var clock = new ManualClock();
        var palette = Palette(search, clock);

        palette.Query = "matrix";
        clock.Advance(SearchPaletteViewModel.OnlineDelay);
        await Ui.WaitUntil(() => Texts(palette).Contains("The Matrix") && search.Queries.Count == 1);
        await palette.Settled;
        Ui.Flush();

        Assert.Equal([SearchPaletteViewModel.LibraryHeader, "The Matrix"], Texts(palette));
    }

    [AvaloniaFact]
    public async Task Palette_Remote_StaleAnswers_FromEitherRequest_AreDropped()
    {
        using var heatLocal = new ManualResetEventSlim();
        var heatOnline = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        _library.LocalSearch = query =>
        {
            Interlocked.Increment(ref asked);
            if (query != "heat") return [FakeLibrary.Card(2, "The Matrix")];

            Assert.True(heatLocal.Wait(TimeSpan.FromSeconds(15), Ct));
            return [FakeLibrary.Card(1, "Heat")];
        };
        var search = new FakeSearch
        {
            OnSearch = (query, _) => query == "heat" ? heatOnline.Task : Task.FromResult(new SearchResults([], [], OnlineState.Ok)),
        };
        var clock = new ManualClock();
        var palette = Palette(search, clock);

        palette.Query = "heat";
        clock.Advance(SearchPaletteViewModel.OnlineDelay);
        await Ui.WaitUntil(() => Volatile.Read(ref asked) == 1 && search.Queries.Count == 1);
        palette.Query = "matrix";
        clock.Advance(SearchPaletteViewModel.OnlineDelay);
        await Ui.WaitUntil(() => Texts(palette).Contains("The Matrix"));

        heatLocal.Set();
        heatOnline.SetResult(new SearchResults([], [new OnlineResult(Severance, null)], OnlineState.Ok));
        await Task.Delay(100, Ct);
        Ui.Flush();

        Assert.Equal([SearchPaletteViewModel.LibraryHeader, "The Matrix"], Texts(palette));
    }

    [AvaloniaFact]
    public async Task Palette_Remote_SearchFails_KeepsTheLibraryHits()
    {
        _library.LocalSearch = _ => [FakeLibrary.Card(1, "The Matrix")];
        var search = new FakeSearch { OnSearch = (_, _) => Task.FromException<SearchResults>(new ServerUnreachableException()) };
        var clock = new ManualClock();
        var palette = Palette(search, clock);

        palette.Query = "matrix";
        clock.Advance(SearchPaletteViewModel.OnlineDelay);
        await Ui.WaitUntil(() => Texts(palette).Contains("The Matrix") && Texts(palette).Contains(SearchPaletteViewModel.OfflineNote));
    }

    [AvaloniaFact]
    public async Task Palette_Remote_ServerGone_SaysOffline_WithoutLogging()
    {
        _library.LocalSearch = _ => throw new ServerUnreachableException();
        var search = new FakeSearch { OnSearch = (_, _) => Task.FromException<SearchResults>(new ServerUnreachableException()) };
        var clock = new ManualClock();
        var palette = Palette(search, clock);

        palette.Query = "matrix";
        clock.Advance(SearchPaletteViewModel.OnlineDelay);

        await Ui.WaitUntil(() => Texts(palette).Contains(SearchPaletteViewModel.OfflineNote));
        Assert.DoesNotContain(SearchPaletteViewModel.SearchingNote, Texts(palette));
        Assert.Empty(_hub.Recent);
    }

    [AvaloniaFact]
    public async Task Palette_Remote_NoTmdbKey_IsNothingAViewerCanFix()
    {
        var search = new FakeSearch { State = OnlineState.NoKey };
        var clock = new ManualClock();
        var palette = Palette(search, clock);

        palette.Query = "matrix";
        clock.Advance(SearchPaletteViewModel.OnlineDelay);

        await Ui.WaitUntil(() => Texts(palette).Contains(SearchPaletteViewModel.ServerNoKeyNote));
        Assert.DoesNotContain(SearchPaletteViewModel.NoKeyChoice, Texts(palette));
    }

    // ---- Closing ----

    private sealed class CloseRig
    {
        public CloseRig(ITrayReason[] reasons, Core.Playback.IPlaybackService playback, PlayerExitPrompt? prompt)
        {
            Window = new Window();
            Window.Closed += (_, _) => Closed = true;
            Tray = new TrayController(Window, reasons, question =>
            {
                Asked.Add(question);
                return Task.FromResult(true);
            });
            Window.Closing += new ShellClosing(Tray, prompt, playback).OnClosing;
            Window.Show();
        }

        public Window Window { get; }

        public TrayController Tray { get; }

        public bool Closed { get; private set; }

        public List<string> Asked { get; } = [];
    }

    private static CloseRig RemoteRig(Viewer viewer) => new(
        [.. viewer.Services.GetServices<ITrayReason>()], viewer.Playback, viewer.Services.GetService<PlayerExitPrompt>());

    private static async Task<int> PlayAsync(Viewer viewer)
    {
        var file = await viewer.World.AddLongMovieAsync();
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        await viewer.Playback.PlayAsync(file, fromStart: true, Ct);
        viewer.Player.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1000)));
        viewer.Player.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(115)));
        Ui.Flush();
        return file;
    }

    [AvaloniaFact]
    public async Task Close_WhilePlaying_Remote_HidesToTray_NoPrompt()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var rig = RemoteRig(viewer);
        await PlayAsync(viewer);
        await Ui.WaitUntil(() => rig.Tray.Icon.IsVisible);

        rig.Window.Close();
        Ui.Flush();

        Assert.False(rig.Window.IsVisible);
        Assert.False(rig.Closed);
        Assert.Empty(rig.Asked);
        Assert.Contains(viewer.Playback.CurrentPlayer!, rig.Tray.Icon.ToolTipText, StringComparison.Ordinal);
        Assert.False(viewer.Player.Closed.Task.IsCompleted);
        rig.Window.Show();
        await rig.Tray.QuitAsync();
    }

    [AvaloniaFact]
    public async Task PlayerCloses_ThenAppExits()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var rig = RemoteRig(viewer);
        await PlayAsync(viewer);
        await Ui.WaitUntil(() => rig.Tray.Icon.IsVisible);
        rig.Window.Close();
        Ui.Flush();

        viewer.Player.Raise(new PlaybackEvent.Closed());

        await Ui.WaitUntil(() => rig.Closed);
    }

    [AvaloniaFact]
    public async Task PlayerCloses_WithTheWindowOpen_TheAppStays()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var rig = RemoteRig(viewer);
        await PlayAsync(viewer);
        await Ui.WaitUntil(() => rig.Tray.Icon.IsVisible);

        viewer.Player.Raise(new PlaybackEvent.Closed());
        await Ui.WaitUntil(() => !rig.Tray.Icon.IsVisible);

        Assert.False(rig.Closed);
        Assert.True(rig.Window.IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task Close_WhileIdle_Remote_Exits()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var rig = RemoteRig(viewer);

        rig.Window.Close();
        Ui.Flush();

        Assert.True(rig.Closed);
        Assert.Empty(rig.Asked);
    }

    [AvaloniaFact]
    public async Task TrayQuit_SavesPosition()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var rig = RemoteRig(viewer);
        var file = await PlayAsync(viewer);
        await Ui.WaitUntil(() => rig.Tray.Icon.IsVisible);
        rig.Window.Close();
        Ui.Flush();

        await rig.Tray.QuitAsync();
        Assert.True(rig.Closed);
        Assert.Empty(rig.Asked);

        // What App does on exit.
        await viewer.StopAsync();
        await viewer.Services.GetRequiredService<Core.Remote.ProgressOutbox>().DisposeAsync();

        var sam = world.Services.GetRequiredService<AccountStore>().All().Single(a => a.Name == "Sam");
        Assert.Equal(115, world.Services.GetRequiredService<ProgressStore>().Get(sam.Id, file)?.PositionS);
    }

    // The app can't say why the player went if it's gone too, so the window comes back with the reason on it.
    [AvaloniaFact]
    public async Task StreamCut_WhileHidden_BringsTheWindowBack()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var rig = RemoteRig(viewer);
        await PlayAsync(viewer);
        await Ui.WaitUntil(() => rig.Tray.Icon.IsVisible);
        rig.Window.Close();
        Ui.Flush();

        await viewer.Playback.AbandonCurrentAsync("Anna stopped this stream");

        await Ui.WaitUntil(() => rig.Window.IsVisible);
        Assert.False(rig.Closed);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task LaunchOnlyPlayerCloses_WhileHidden_StaysForTheQuestion()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        viewer.Player.TracksProgress = false;
        var rig = RemoteRig(viewer);
        await PlayAsync(viewer);
        await Ui.WaitUntil(() => rig.Tray.Icon.IsVisible);
        rig.Window.Close();
        Ui.Flush();

        viewer.Player.Raise(new PlaybackEvent.Closed());

        await Ui.WaitUntil(() => rig.Window.IsVisible);
        Assert.False(rig.Closed);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task Close_WhilePlaying_Local_StillPrompts()
    {
        var playback = new FakePlayback { CurrentPlayer = "MPC-HC" };
        var asked = new List<string>();
        var prompt = new PlayerExitPrompt(playback, player =>
        {
            asked.Add(player);
            return Task.FromResult(true);
        });
        var rig = new CloseRig([new FakeTrayReason()], playback, prompt);

        rig.Window.Close();

        await Ui.WaitUntil(() => rig.Closed);
        Assert.Equal(["MPC-HC"], asked);
        Assert.Equal(["save", "stop:close"], playback.Calls);
    }

    [AvaloniaFact]
    public void Close_ByTheAppItself_SkipsThePrompt()
    {
        var playback = new FakePlayback { CurrentPlayer = "MPC-HC" };
        var asked = 0;
        var prompt = new PlayerExitPrompt(playback, _ =>
        {
            asked++;
            return Task.FromResult(true);
        });
        var closing = new ShellClosing(new TrayController(new Window(), [], _ => Task.FromResult(true)), prompt, playback);

        // The app's own shutdown comes from the lifetime, which the headless app hasn't got.
        var shutdown = (WindowClosingEventArgs)Activator.CreateInstance(
            typeof(WindowClosingEventArgs), BindingFlags.NonPublic | BindingFlags.Instance, null, [WindowCloseReason.ApplicationShutdown, true], null)!;
        closing.OnClosing(new Window(), shutdown);

        Assert.Equal(0, asked);
    }

    [AvaloniaTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Tray_TheReasonThatHeldLastDecides(bool playingEndsLast, bool exits)
    {
        var sharing = new FakeTrayReason();
        var playing = new FakeTrayReason { ExitsWhenOver = true };
        var rig = new CloseRig([sharing, playing], new FakePlayback(), null);
        sharing.Set(true);
        playing.Set(true);
        Ui.Flush();
        rig.Window.Close();
        Ui.Flush();

        (playingEndsLast ? sharing : playing).Set(false);
        Ui.Flush();
        Assert.False(rig.Window.IsVisible);
        (playingEndsLast ? playing : sharing).Set(false);
        Ui.Flush();

        Assert.Equal(exits, rig.Closed);
        Assert.Equal(!exits, rig.Window.IsVisible);
        if (!exits) rig.Window.Close();
    }

    // ---- The account ----

    private static async Task<(Viewer Viewer, AccountViewModel Account)> JoinWithPasswordAsync(SharingWorld world)
    {
        var viewer = await Viewer.JoinAsync(world);
        var sam = world.Services.GetRequiredService<AccountStore>().All().Single(a => a.Name == "Sam");
        Assert.True(InviteString.TryDecode(world.Sharing.CreateInvite(sam.Id), out var invite));
        Assert.NotNull(world.Accounts.Redeem(invite.Code, SamPassword, "Tablet", "198.51.100.2").Issued);
        return (viewer, viewer.Services.GetRequiredService<SettingsViewModel>().Account!);
    }

    [AvaloniaFact]
    public async Task ChangePassword_WrongCurrent_SaysSo_AndClearsTheFields()
    {
        await using var world = new SharingWorld();
        var (viewer, account) = await JoinWithPasswordAsync(world);
        await using var _ = viewer;
        account.BeginChangePasswordCommand.Execute(null);
        account.CurrentPassword = "not the password";
        account.NewPassword = "another good password";
        account.RepeatPassword = "another good password";

        await account.SavePasswordCommand.ExecuteAsync(null);

        Assert.Equal("Wrong password. Try again.", account.PasswordMessage);
        Assert.Equal("", account.CurrentPassword);
        Assert.Equal("", account.NewPassword);
        Assert.Equal("", account.RepeatPassword);
        Assert.True(account.IsChangingPassword);
    }

    [AvaloniaFact]
    public async Task ChangePassword_Works_AndClosesTheForm()
    {
        await using var world = new SharingWorld();
        var (viewer, account) = await JoinWithPasswordAsync(world);
        await using var _ = viewer;
        account.BeginChangePasswordCommand.Execute(null);
        account.CurrentPassword = SamPassword;
        account.NewPassword = "another good password";
        account.RepeatPassword = "another good password";

        await account.SavePasswordCommand.ExecuteAsync(null);

        Assert.False(account.IsChangingPassword);
        Assert.Equal("Password changed.", account.PasswordMessage);
        Assert.Equal("", account.NewPassword);
        Assert.NotNull(world.Accounts.SignIn("Sam", "another good password", "Desk", "198.51.100.3").Issued);
    }

    [AvaloniaTheory]
    [InlineData("short", "short", "Use at least 10 characters.")]
    [InlineData("long enough one", "long enough two", "The passwords don't match.")]
    public async Task ChangePassword_ChecksHere_BeforeAskingTheServer(string next, string repeat, string says)
    {
        await using var world = new SharingWorld();
        var (viewer, account) = await JoinWithPasswordAsync(world);
        await using var _ = viewer;
        account.BeginChangePasswordCommand.Execute(null);
        account.CurrentPassword = SamPassword;
        account.NewPassword = next;
        account.RepeatPassword = repeat;

        await account.SavePasswordCommand.ExecuteAsync(null);

        Assert.Equal(says, account.PasswordMessage);
        Assert.NotNull(world.Accounts.SignIn("Sam", SamPassword, "Desk", "198.51.100.3").Issued); // unchanged
    }

    [AvaloniaFact]
    public async Task RemoteComposition_HasTheRestart_AndCapabilities()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);

        Assert.NotNull(viewer.Services.GetRequiredService<AppRestart>());
        var capabilities = viewer.Services.GetRequiredService<AppCapabilities>();
        Assert.False(capabilities.CanEditLibrary);
        Assert.False(capabilities.ManagesFolders);
        Assert.False(capabilities.Shares);
        Assert.Same(viewer.Monitor, capabilities.Connection);
        Assert.Null(viewer.Services.GetService<FoldersViewModel>());

        Assert.Equal(AppCapabilities.Local, world.Services.GetRequiredService<AppCapabilities>());
    }
}
