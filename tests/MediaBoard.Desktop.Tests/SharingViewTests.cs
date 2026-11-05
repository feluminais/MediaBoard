using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Players;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;
using MediaBoard.Server;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop.Tests;

/// <summary>The page as a window shows it: how much it builds, where the focus goes, and how the shell reaches it.</summary>
public sealed class SharingViewTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static async Task<Window> Show(SharingViewModel page)
    {
        await Ui.WaitUntil(() => page.IsLoaded);
        var window = new Window { Width = 1000, Height = 720, Content = new SharingView { DataContext = page } };
        window.Show();
        Settle(window);
        return window;
    }

    private static List<Button> Buttons(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();

    private static Button Button(Window window, string label) =>
        Buttons(window).First(b => label.Equals(b.Content as string, StringComparison.Ordinal));

    private static List<string?> Actions(Window window, PersonRow row)
    {
        var view = window.GetVisualDescendants().OfType<ContentControl>().Single(c => ReferenceEquals(c.Content, row.Detail));
        return view.GetVisualDescendants().OfType<Button>().Where(b => b is not CheckBox && b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
    }

    private static void ScrollTo(Window window, double offset)
    {
        window.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = new Avalonia.Vector(0, offset);
        Settle(window);
    }

    private static async Task<PersonRow> OpenRow(SharingViewModel page, Window window, string name)
    {
        await page.RefreshAsync();
        var row = page.People.Single(r => r.Name == name);
        await page.ToggleRowCommand.ExecuteAsync(row);
        Settle(window);
        return row;
    }

    [AvaloniaFact]
    public async Task HundredAccounts_BuildOnlyTheRowsThatShow()
    {
        await using var world = new SharingWorld();
        for (var i = 0; i < 100; i++) world.Accounts.AddPerson($"Person {i:D3}", null);
        var page = SharingViewModelTests.Build(world);
        page.Opened();

        var window = await Show(page);

        Assert.Equal(101, page.People.Count);
        var headers = window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("disclosure"));
        Assert.InRange(headers, 1, 30);
        window.Close();
    }

    [AvaloniaFact]
    public async Task HundredAccounts_TabReachesEveryRow()
    {
        await using var world = new SharingWorld();
        for (var i = 0; i < 100; i++) world.Accounts.AddPerson($"Person {i:D3}", null);
        var page = SharingViewModelTests.Build(world);
        page.Opened();
        var window = await Show(page);
        Button(window, "Add person").Focus();

        var reached = new HashSet<PersonRow>();
        for (var i = 0; i < 110; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Settle(window);
            if (window.FocusManager!.GetFocusedElement() is Button { DataContext: PersonRow row }) reached.Add(row);
        }

        Assert.Equal(page.People.Count, reached.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PanelHeldOpen_StillClosesWithItsOwner()
    {
        var running = new TaskCompletionSource();
        var panel = new SharingPanel();
        var step = new AddPersonStep([], _ => running.Task) { Name = "Anna" };
        panel.Step = step;
        var owner = new Window();
        owner.Show();
        var dialog = new SharingDialog(panel);
        var shown = dialog.ShowDialog(owner);
        Settle(dialog);
        var create = step.CreateCommand.ExecuteAsync(null);

        dialog.Close();
        Settle(dialog);
        Assert.False(shown.IsCompleted); // the owner's close waits for the step

        owner.Close();
        await Ui.WaitUntil(() => shown.IsCompleted); // the app closing doesn't

        running.SetResult();
        await create;
    }

    [AvaloniaFact]
    public async Task FullLog_BuildsOnlyTheLinesThatShow()
    {
        var rows = Enumerable.Range(0, 5000).Select(i => new SharingActivityRow(i, "10:00", $"Entry {i}")).ToList();
        var owner = new Window();
        owner.Show();
        var dialog = new SharingDialog(new SharingPanel { Step = new ActivityStep(rows) });
        var shown = dialog.ShowDialog(owner);
        Settle(dialog);

        var built = dialog.GetVisualDescendants().OfType<SelectableTextBlock>().Count();
        Assert.InRange(built, 1, 60);

        dialog.Close();
        await shown;
        owner.Close();
    }

    [AvaloniaFact]
    public async Task EightStreams_AllShow_WithStopButtons()
    {
        await using var world = new SharingWorld();
        var device = world.NewDevice().Device;
        var accounts = world.Services.GetRequiredService<AccountStore>();
        var account = accounts.Get(device.UserId)!;
        var who = new RequestViewer(accounts.ViewerOf(account), account, device);
        for (var i = 1; i <= 8; i++)
            world.Streams.Open(who, new ReachableFile(i, 1, "unused", "Movie.mkv", $"Movie {i}", null, null, false), CancellationToken.None, out _);
        var page = SharingViewModelTests.Build(world);
        page.Opened();

        var window = await Show(page);

        Assert.Equal(8, Buttons(window).Count(b => "Stop".Equals(b.Content as string, StringComparison.Ordinal)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task OwnerRow_OffersItsDevices_NoFoldersOrViewerActions()
    {
        await using var world = new SharingWorld();
        var page = SharingViewModelTests.Build(world);
        page.Opened();
        var window = await Show(page);

        await page.ToggleRowCommand.ExecuteAsync(page.People[0]);
        Settle(window);

        Assert.Equal(["New invite", "Set password"], Actions(window, page.People[0]));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<CheckBox>(), box => box.IsEffectivelyVisible);

        world.Services.GetRequiredService<DeviceStore>().Issue(LibraryDatabase.OwnerId, "Laptop", "198.51.100.2");
        world.Services.GetRequiredService<InviteStore>().Create(LibraryDatabase.OwnerId);
        await page.RefreshAsync();
        Settle(window);

        Assert.Equal(["Sign out", "New invite", "Cancel invites", "Set password"], Actions(window, page.People[0]));
        window.Close();
    }

    [AvaloniaFact]
    public async Task PersonRow_OffersEveryAction()
    {
        await using var world = new SharingWorld();
        var device = world.NewDevice();
        var page = SharingViewModelTests.Build(world);
        page.Opened();
        var window = await Show(page);

        var row = await OpenRow(page, window, "Anna");
        await Ui.WaitUntil(() => row.Detail!.Devices.Count == 1);
        Settle(window);

        Assert.Equal(["Sign out", "New invite", "Reset password", "Disable", "Remove"], Actions(window, row));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Removing_TheFocusedRow_PutsTheFocusOnTheNextRow()
    {
        await using var world = new SharingWorld();
        world.Accounts.AddPerson("Anna", null);
        world.Accounts.AddPerson("Boris", null);
        var page = SharingViewModelTests.Build(world);
        page.Opened();
        var window = await Show(page);
        var row = await OpenRow(page, window, "Anna");
        var remove = Button(window, "Remove");
        remove.Focus();

        await page.RemoveCommand.ExecuteAsync(row);
        Settle(window);

        var focused = Assert.IsType<Button>(window.FocusManager!.GetFocusedElement());
        Assert.Same(page.People.Single(r => r.Name == "Boris"), focused.DataContext);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SigningOut_TheFocusedDevice_PutsTheFocusOnTheRow()
    {
        await using var world = new SharingWorld();
        world.NewDevice();
        var page = SharingViewModelTests.Build(world);
        page.Opened();
        var window = await Show(page);
        var row = await OpenRow(page, window, "Anna");
        await Ui.WaitUntil(() => row.Detail!.Devices.Count == 1);
        Settle(window);
        var signOut = Button(window, "Sign out");
        signOut.Focus();

        await page.SignOutCommand.ExecuteAsync(row.Detail!.Devices[0]);
        Settle(window);

        var focused = Assert.IsType<Button>(window.FocusManager!.GetFocusedElement());
        Assert.Same(row, focused.DataContext);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Stopping_TheFocusedStream_PutsTheFocusOnTheSwitch_WhenNoneIsLeft()
    {
        await using var world = new SharingWorld();
        world.OpenStream();
        var page = SharingViewModelTests.Build(world);
        page.Opened();
        var window = await Show(page);
        Button(window, "Stop").Focus();

        await page.StopCommand.ExecuteAsync(page.Watching[0]);
        Settle(window);

        Assert.IsType<ToggleSwitch>(window.FocusManager!.GetFocusedElement());
        window.Close();
    }

    [AvaloniaFact]
    public async Task CancellingInvites_PutsTheFocusOnTheRow()
    {
        await using var world = new SharingWorld();
        var anna = world.Accounts.AddPerson("Anna", null);
        world.Services.GetRequiredService<InviteStore>().Create(anna.Id);
        var page = SharingViewModelTests.Build(world);
        page.Opened();
        var window = await Show(page);
        var row = await OpenRow(page, window, "Anna");
        Button(window, "Cancel invites").Focus();

        await page.CancelInvitesCommand.ExecuteAsync(row);
        Settle(window);

        var focused = Assert.IsType<Button>(window.FocusManager!.GetFocusedElement());
        Assert.Same(row, focused.DataContext);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ARefresh_LeavesTheFocusWhereItWas()
    {
        await using var world = new SharingWorld();
        world.Accounts.AddPerson("Anna", null);
        var page = SharingViewModelTests.Build(world);
        page.Opened();
        var window = await Show(page);
        await OpenRow(page, window, "Anna");
        var remove = Button(window, "Remove");
        remove.Focus();

        await page.RefreshAsync();
        Settle(window);

        Assert.Same(remove, window.FocusManager!.GetFocusedElement());
        window.Close();
    }

    [AvaloniaFact]
    public async Task NoAddress_PutsTheFocusInThePublicAddress()
    {
        await using var world = new SharingWorld();
        var page = SharingViewModelTests.Build(world);
        page.Opened();
        var window = await Show(page);

        await page.AddPersonCommand.ExecuteAsync(null);
        Settle(window);

        var box = Assert.IsType<TextBox>(window.FocusManager!.GetFocusedElement());
        Assert.Equal("PublicBox", box.Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PanelOverAPage_FocusStartsInName_ThenMovesToCopy_AndEscapeCloses()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = "media.example.org" });
        var page = SharingViewModelTests.Build(world);
        page.Opened();
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var panel = new SharingPanel();
        var dialog = new SharingDialog(panel);
        panel.Step = new AddPersonStep([], _ =>
        {
            panel.Step = new InviteStep("Invite for Anna", "Send this to Anna.", "mb1-test", _ => Task.CompletedTask);
            return Task.CompletedTask;
        });

        var shown = dialog.ShowDialog(owner);
        Settle(dialog);

        var name = Assert.IsType<TextBox>(dialog.FocusManager!.GetFocusedElement());
        name.Text = "Anna";
        Settle(dialog);
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r"); // the default button
        Settle(dialog);

        var copy = Assert.IsType<Button>(dialog.FocusManager!.GetFocusedElement());
        Assert.Equal("Copy", copy.Content);

        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(dialog);
        Assert.False(shown.IsCompleted); // not copied, so the first Escape warns

        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Ui.WaitUntil(() => shown.IsCompleted);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task PanelText_GivesUpTheFocus_OnAPressOnEmptySpace()
    {
        var owner = new Window();
        owner.Show();
        var dialog = new SharingDialog(new SharingPanel { Step = new SetPasswordStep(_ => Task.CompletedTask) });
        var shown = dialog.ShowDialog(owner);
        Settle(dialog);
        Assert.IsType<TextBox>(dialog.FocusManager!.GetFocusedElement());

        dialog.MouseDown(new Avalonia.Point(8, 200), MouseButton.Left);
        dialog.MouseUp(new Avalonia.Point(8, 200), MouseButton.Left);
        Settle(dialog);

        Assert.IsNotType<TextBox>(dialog.FocusManager!.GetFocusedElement());
        Assert.NotNull(dialog.FocusManager!.GetFocusedElement()); // Escape still has somewhere to go

        dialog.Close();
        await shown;
        owner.Close();
    }

    // ---- The shell ----

    private static MainWindowViewModel Shell(SharingWorld world, SharingViewModel? page, ActivityHub hub)
    {
        var library = new FakeLibrary();
        return new MainWindowViewModel(
            library,
            new StatusBarViewModel(hub),
            new ActivityPanelViewModel(hub),
            hub,
            new FoldersViewModel(library, world.Services.GetRequiredService<IFolderActions>(), hub, () => Task.FromResult<string?>(null), _ => Task.FromResult(true)),
            new SettingsViewModel(world.Settings, new PlayerDetector(_ => false, () => [], hub), hub, () => Task.FromResult<string?>(null), _ => { }),
            Posters.None(),
            new Titles(library, hub).Services(),
            sharing: page);
    }

    [AvaloniaFact]
    public async Task Sidebar_ListsSharing_RightAfterFolders()
    {
        await using var world = new SharingWorld();
        var page = SharingViewModelTests.Build(world);

        var shell = Shell(world, page, world.Hub);

        Assert.Equal(["Home", "Movies", "Shows", "Folders", "Sharing", "Settings"], shell.NavItems.Select(i => i.Key));
        shell.NavigateCommand.Execute(MainWindowViewModel.SharingPage);
        Assert.Same(page, shell.CurrentPage);
        await Ui.WaitUntil(() => page.IsLoaded);
    }

    [AvaloniaFact]
    public async Task Sidebar_WithoutThePage_HasNoEntry()
    {
        await using var world = new SharingWorld();

        var shell = Shell(world, null, world.Hub);

        Assert.DoesNotContain(shell.NavItems, i => i.Key == MainWindowViewModel.SharingPage);
    }

    [AvaloniaFact]
    public async Task ThroughTheShell_NoAddress_PutsTheFocusInThePublicAddress()
    {
        await using var world = new SharingWorld();
        var page = SharingViewModelTests.Build(world);
        var shell = Shell(world, page, world.Hub);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        shell.NavigateCommand.Execute(MainWindowViewModel.SharingPage);
        await Ui.WaitUntil(() => page.IsLoaded);
        Settle(window);

        await page.AddPersonCommand.ExecuteAsync(null);
        Settle(window);

        Assert.Equal("PublicBox", Assert.IsType<TextBox>(window.FocusManager!.GetFocusedElement()).Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task StatusBarSegment_OpensThePage_NotTheActivityPanel()
    {
        await using var world = new SharingWorld();
        var page = SharingViewModelTests.Build(world);
        var shell = Shell(world, page, world.Hub);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await world.Sharing.SetOnAsync(true);
        await Ui.WaitUntil(() => shell.StatusBar.Sharing is not null);
        Settle(window);

        var segment = window.FindControl<Button>("SharingSegment")!;
        Assert.True(segment.IsEffectivelyVisible);
        Assert.Equal("Sharing on", segment.Content);
        segment.Command!.Execute(null);

        Assert.Same(page, shell.CurrentPage);
        Assert.False(shell.IsActivityOpen);
        window.Close();
    }
}
