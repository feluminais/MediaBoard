using System.Diagnostics;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Storage;
using MediaBoard.Desktop.Sharing;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop.Tests;

public sealed class SharingControllerTests
{
    private static readonly TimeSpan CutWithin = TimeSpan.FromSeconds(3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- On and off ----

    [Fact]
    public async Task Off_NothingListens()
    {
        await using var world = new SharingWorld();

        Assert.Equal(SharingStatus.Off, world.Sharing.Status);
        Assert.Null(world.Sharing.Problem);
        await world.AssertRefusedAsync();
    }

    [Fact]
    public async Task SetOn_Listens_SavesSetting()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        var seen = new List<SharingStatus>();
        world.Sharing.Changed += () => seen.Add(world.Sharing.Status);

        await world.Sharing.SetOnAsync(true);

        Assert.Equal(SharingStatus.On, world.Sharing.Status);
        Assert.Equal([SharingStatus.Starting, SharingStatus.On], seen);
        Assert.True(world.Settings.Load().Sharing.On);
        Assert.Equal($"{Environment.UserName}'s library", (await world.PingAsync()).Name);
    }

    [Fact]
    public async Task SetOn_UsesTheChosenServerName()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.SetSharing(s => s with { ServerName = "The den" });

        await world.Sharing.SetOnAsync(true);

        Assert.Equal("The den", (await world.PingAsync()).Name);
    }

    [Fact]
    public async Task SetOn_PortInUse_StatusFailed_SaysSo()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();

        var taken = new TcpListener(System.Net.IPAddress.Loopback, world.Port);
        taken.Start();
        try
        {
            await world.Sharing.SetOnAsync(true);
        }
        finally
        {
            taken.Stop();
        }

        Assert.Equal(SharingStatus.Failed, world.Sharing.Status);
        Assert.Contains(world.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), world.Sharing.Problem);
        Assert.Contains("Another program", world.Sharing.Problem);
        Assert.False(world.Settings.Load().Sharing.On); // it never ran, so a restart must not bring it up unannounced
        Assert.Contains(world.Hub.Recent, e => e.Level == ActivityLevel.Error && e.Message == world.Sharing.Problem);

        await world.Sharing.SetOnAsync(true);

        Assert.Equal(SharingStatus.On, world.Sharing.Status);
        Assert.Null(world.Sharing.Problem);
    }

    [Fact]
    public async Task SetOn_BadPort_StatusFailed_InsteadOfThrowing()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.SetSharing(s => s with { Port = 70000 });

        await world.Sharing.SetOnAsync(true);

        Assert.Equal(SharingStatus.Failed, world.Sharing.Status);
        Assert.Contains("70000", world.Sharing.Problem);
    }

    [Fact]
    public async Task SetOn_UnreadableCertificate_StatusFailed_LeavesTheFileAlone()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        byte[] junk = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(world.Paths.Certificate, junk, Ct);

        await world.Sharing.SetOnAsync(true);

        Assert.Equal(SharingStatus.Failed, world.Sharing.Status);
        Assert.Contains("certificate", world.Sharing.Problem);
        Assert.Equal(junk, await File.ReadAllBytesAsync(world.Paths.Certificate, Ct));
        await world.AssertRefusedAsync();
    }

    [Fact]
    public async Task SetOff_AfterFailure_ClearsTheProblem()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        await File.WriteAllBytesAsync(world.Paths.Certificate, [1, 2, 3, 4], Ct);
        await world.Sharing.SetOnAsync(true);

        await world.Sharing.SetOnAsync(false);

        Assert.Equal(SharingStatus.Off, world.Sharing.Status);
        Assert.Null(world.Sharing.Problem);
    }

    [Fact]
    public async Task SetOff_ClosesStreams_AndListener()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        var fileId = await world.AddLongMovieAsync();
        var token = world.NewDevice().Token;
        await world.Sharing.SetOnAsync(true);
        var stream = await world.OpenBodyAsync($"/api/v1/files/{fileId}/stream", token);
        var events = await world.OpenBodyAsync("/api/v1/events", token);
        Assert.Single(world.Streams.Current);

        var clock = Stopwatch.StartNew();
        await world.Sharing.SetOnAsync(false);

        Assert.InRange(clock.Elapsed, TimeSpan.Zero, CutWithin);
        Assert.Equal(SharingStatus.Off, world.Sharing.Status);
        Assert.False(world.Settings.Load().Sharing.On);
        Assert.Empty(world.Streams.Current);
        await Assert.ThrowsAnyAsync<IOException>(() => stream.CopyToAsync(Stream.Null, Ct).WaitAsync(CutWithin, Ct));
        await Assert.ThrowsAnyAsync<IOException>(() => events.CopyToAsync(Stream.Null, Ct).WaitAsync(CutWithin, Ct));
        await world.AssertRefusedAsync();
    }

    [Fact]
    public async Task SetOn_AgainAfterOff_Listens()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        await world.Sharing.SetOnAsync(true);
        await world.Sharing.SetOnAsync(false);

        await world.Sharing.SetOnAsync(true);

        Assert.Equal(SharingStatus.On, world.Sharing.Status);
        await world.PingAsync();
    }

    [Fact]
    public async Task StopForExit_StopsListening_ButKeepsTheSetting()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        await world.Sharing.SetOnAsync(true);

        await world.Sharing.StopForExitAsync();

        Assert.Equal(SharingStatus.Off, world.Sharing.Status);
        Assert.True(world.Settings.Load().Sharing.On);
        await world.AssertRefusedAsync();
    }

    // ---- Startup ----

    [Fact]
    public async Task RememberedOn_ResumesAfterStartup()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { On = true });
        var host = world.Services.GetRequiredService<AppHost>();

        host.Start();
        await host.SharingSettled.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(SharingStatus.On, world.Sharing.Status);
        await world.PingAsync();

        await host.Startup.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await host.StopAsync();

        await world.AssertRefusedAsync();
        Assert.True(world.Settings.Load().Sharing.On); // quitting isn't turning it off
    }

    [Fact]
    public async Task RememberedOn_ResumeFails_KeepsTheSetting()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { On = true });
        await File.WriteAllBytesAsync(world.Paths.Certificate, [1, 2, 3, 4], Ct);
        var host = world.Services.GetRequiredService<AppHost>();

        host.Start();
        await host.SharingSettled.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await host.Startup.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await host.StopAsync();

        Assert.True(world.Settings.Load().Sharing.On);
    }

    [Fact]
    public async Task Single_User_Startup_BuildsNoServer()
    {
        await using var world = new SharingWorld();
        var host = world.Services.GetRequiredService<AppHost>();

        host.Start();
        await host.Startup.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(host.SharingSettled.IsCompleted);
        Assert.Equal(0, world.ServersBuilt);
        Assert.Equal(SharingStatus.Off, world.Sharing.Status);
        await world.AssertRefusedAsync();
        await host.StopAsync();
        Assert.Equal(0, world.ServersBuilt);
    }

    [Fact]
    public async Task SharingSettled_Completes_WhenTheDatabaseCantOpen()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { On = true });
        Directory.CreateDirectory(world.Paths.Database);
        var host = world.Services.GetRequiredService<AppHost>();

        host.Start();
        await host.SharingSettled.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await host.StopAsync();

        Assert.Equal(0, world.ServersBuilt);
    }

    // ---- Invites ----

    [Fact]
    public async Task Invite_CarriesBothAddressesAndFingerprint()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.SetSharing(s => s with { PublicAddress = "media.example.org", HomeAddress = "192.168.1.20", ServerName = "The den" });
        await world.Sharing.SetOnAsync(true);
        await world.PingAsync();

        var invite = world.Sharing.CreateInvite(LibraryDatabase.OwnerId);

        Assert.True(InviteString.TryDecode(invite, out var payload));
        Assert.Equal("The den", payload.ServerName);
        Assert.Equal(2, payload.Addresses.Count);
        Assert.Equal(world.ServedFingerprint, payload.Fingerprint);
        Assert.Equal(LibraryDatabase.OwnerId, world.Services.GetRequiredService<InviteStore>().Peek(payload.Code));
    }

    [Fact]
    public async Task HomeAddressComesFirst()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.SetSharing(s => s with { PublicAddress = "media.example.org", HomeAddress = "192.168.1.20" });

        var invite = world.Sharing.CreateInvite(LibraryDatabase.OwnerId);

        Assert.True(InviteString.TryDecode(invite, out var payload));
        Assert.Equal([$"192.168.1.20:{world.Port}", $"media.example.org:{world.Port}"], payload.Addresses);
    }

    [Theory]
    [InlineData("media.example.org:8443", "media.example.org:8443")] // a router can forward a different outside port
    [InlineData("  media.example.org  ", "media.example.org:{port}")]
    [InlineData("https://media.example.org/", "media.example.org:{port}")]
    [InlineData("[2001:db8::1]", "[2001:db8::1]:{port}")]
    public async Task Invite_NormalisesWhatTheOwnerTyped(string typed, string expected)
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.SetSharing(s => s with { PublicAddress = typed });

        var invite = world.Sharing.CreateInvite(LibraryDatabase.OwnerId);

        Assert.True(InviteString.TryDecode(invite, out var payload));
        Assert.Equal([expected.Replace("{port}", world.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)], payload.Addresses);
    }

    [Fact]
    public async Task Invite_WhileOff_LeavesItOff_WithTheFingerprintItWillServe()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.SetSharing(s => s with { PublicAddress = "media.example.org" });

        var invite = world.Sharing.CreateInvite(LibraryDatabase.OwnerId);

        Assert.Equal(SharingStatus.Off, world.Sharing.Status);
        await world.AssertRefusedAsync();
        await world.Sharing.SetOnAsync(true);
        await world.PingAsync();
        Assert.True(InviteString.TryDecode(invite, out var payload));
        Assert.Equal(world.ServedFingerprint, payload.Fingerprint);
    }

    [Fact]
    public async Task Invite_NoAddress_Throws_AndCreatesNoInvite()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();

        Assert.Throws<InvalidOperationException>(() => world.Sharing.CreateInvite(LibraryDatabase.OwnerId));

        Assert.Empty(world.Services.GetRequiredService<InviteStore>().Pending(LibraryDatabase.OwnerId));
    }

    // ---- Check connection ----

    [Fact]
    public async Task Check_NoPublicAddress_SaysSo()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.SetSharing(s => s with { HomeAddress = "192.168.1.20" });
        await world.Sharing.SetOnAsync(true);

        Assert.Equal(CheckResult.NoAddress, await world.Sharing.CheckConnectionAsync(Ct));
    }

    [Fact]
    public async Task Check_PublicAddressAnswers_IsReachable()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.SetSharing(s => s with { PublicAddress = "127.0.0.1" });
        await world.Sharing.SetOnAsync(true);

        Assert.Equal(CheckResult.Reachable, await world.Sharing.CheckConnectionAsync(Ct));
    }

    [Fact]
    public async Task Check_WhileOff_GoesNowhere()
    {
        await using var world = new SharingWorld();
        world.SetSharing(s => s with { PublicAddress = "127.0.0.1" });

        Assert.Equal(CheckResult.NotConclusive, await world.Sharing.CheckConnectionAsync(Ct));
        Assert.False(File.Exists(world.Paths.Certificate)); // it didn't even load a certificate to pin
    }

    [Fact]
    public async Task Check_AnotherServersCertificate_IsNotConclusive()
    {
        await using var world = new SharingWorld();
        await using var other = new SharingWorld();
        world.OpenDatabase();
        other.OpenDatabase();
        await other.Sharing.SetOnAsync(true);
        world.SetSharing(s => s with { PublicAddress = $"127.0.0.1:{other.Port}" });
        await world.Sharing.SetOnAsync(true);

        Assert.Equal(CheckResult.NotConclusive, await world.Sharing.CheckConnectionAsync(Ct));
    }

    [Fact]
    public async Task Check_NothingListensThere_IsNotConclusive()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.SetSharing(s => s with { PublicAddress = $"127.0.0.1:{SharingWorld.FreePort()}" });
        await world.Sharing.SetOnAsync(true);

        Assert.Equal(CheckResult.NotConclusive, await world.Sharing.CheckConnectionAsync(Ct));
    }

    [Fact]
    public async Task Check_Cancelled_Throws()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.SetSharing(s => s with { PublicAddress = "127.0.0.1" });
        await world.Sharing.SetOnAsync(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Sharing.CheckConnectionAsync(new CancellationToken(canceled: true)));
    }

    // ---- Certificate ----

    [Fact]
    public async Task ReplaceCertificate_WhileOn_ServesTheNewOne()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.SetSharing(s => s with { PublicAddress = "127.0.0.1" });
        await world.Sharing.SetOnAsync(true);
        await world.PingAsync();
        var before = world.ServedFingerprint;

        await world.Sharing.ReplaceCertificateAsync();

        Assert.Equal(SharingStatus.On, world.Sharing.Status);
        await world.PingAsync();
        Assert.NotEqual(before, world.ServedFingerprint);
        Assert.True(InviteString.TryDecode(world.Sharing.CreateInvite(LibraryDatabase.OwnerId), out var payload));
        Assert.Equal(world.ServedFingerprint, payload.Fingerprint);
        Assert.Equal(CheckResult.Reachable, await world.Sharing.CheckConnectionAsync(Ct));
    }

    [Fact]
    public async Task ReplaceCertificate_WhileOff_StaysOff()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        world.SetSharing(s => s with { PublicAddress = "media.example.org" });
        Assert.True(InviteString.TryDecode(world.Sharing.CreateInvite(LibraryDatabase.OwnerId), out var before));

        await world.Sharing.ReplaceCertificateAsync();

        Assert.Equal(SharingStatus.Off, world.Sharing.Status);
        await world.AssertRefusedAsync();
        Assert.True(InviteString.TryDecode(world.Sharing.CreateInvite(LibraryDatabase.OwnerId), out var after));
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public async Task ReplaceCertificate_AfterAnUnreadableOne_StartsSharing()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        await File.WriteAllBytesAsync(world.Paths.Certificate, [1, 2, 3, 4], Ct);
        await world.Sharing.SetOnAsync(true);

        await world.Sharing.ReplaceCertificateAsync();

        Assert.Equal(SharingStatus.On, world.Sharing.Status);
        Assert.Null(world.Sharing.Problem);
    }

    // ---- Warning and status ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublicNetwork_Warns(bool onPublicNetwork)
    {
        await using var world = new SharingWorld { OnPublicNetwork = onPublicNetwork };
        world.OpenDatabase();

        await world.Sharing.SetOnAsync(true);

        Assert.Equal(SharingStatus.On, world.Sharing.Status);
        Assert.Equal(onPublicNetwork, world.Sharing.Warning?.Contains("Public", StringComparison.Ordinal) ?? false);

        await world.Sharing.SetOnAsync(false);

        Assert.Null(world.Sharing.Warning);
    }

    [AvaloniaFact]
    public async Task StatusBar_ShowsSharingSegment()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        var bar = new StatusBarViewModel(world.Hub);
        Assert.Null(bar.Sharing);

        await world.Sharing.SetOnAsync(true);
        await Ui.WaitUntil(() => bar.Sharing == "Sharing on");

        using (world.OpenStream()) await Ui.WaitUntil(() => bar.Sharing == "Sharing: 1 watching");

        world.Streams.CloseAll();
        await Ui.WaitUntil(() => bar.Sharing == "Sharing on");

        await world.Sharing.SetOnAsync(false);
        await Ui.WaitUntil(() => bar.Sharing is null);
    }

    // ---- Search for viewers ----

    [Fact]
    public void Searches_KeepsOnePerViewer()
    {
        var built = new List<Viewer>();
        var searches = new ViewerSearches(viewer => { built.Add(viewer); return new FakeSearch(); });

        var first = searches.For(new Viewer(2, new HashSet<int> { 1, 2 }));
        var again = searches.For(new Viewer(2, new HashSet<int> { 2, 1 }));
        var other = searches.For(new Viewer(3, null));

        Assert.Same(first, again);
        Assert.NotSame(first, other);
        Assert.Equal(2, built.Count);
    }

    [Theory]
    [InlineData(new[] { 1 }, new[] { 1, 2 })]
    [InlineData(new[] { 1 }, null)]
    [InlineData(null, new[] { 1 })]
    public void Searches_ChangedFolders_BuildAgain(int[]? before, int[]? after)
    {
        var searches = new ViewerSearches(_ => new FakeSearch());

        var first = searches.For(new Viewer(2, before?.ToHashSet()));
        var second = searches.For(new Viewer(2, after?.ToHashSet()));

        Assert.NotSame(first, second); // a kept search would answer from the folders the person had before
    }

    // ---- Close to tray ----

    private static (Window Window, TrayController Tray, List<string> Log) Shell(ITrayReason reason, bool answer = true)
    {
        var log = new List<string>();
        var window = new Window();
        window.Closed += (_, _) => log.Add("closed");
        var tray = new TrayController(window, [reason], question =>
        {
            log.Add($"asked:{window.IsVisible}:{question}");
            return Task.FromResult(answer);
        });
        window.Closing += (_, e) => tray.HideInstead(e);
        return (window, tray, log);
    }

    [AvaloniaFact]
    public void Close_WhileOn_HidesInsteadOfExiting()
    {
        var reason = new FakeTrayReason();
        var (window, _, log) = Shell(reason);
        window.Show();
        reason.Set(true);
        Ui.Flush();

        window.Close();
        Ui.Flush();

        Assert.False(window.IsVisible);
        Assert.Empty(log);
        reason.Set(false);
        Ui.Flush();
        window.Close();
    }

    [AvaloniaFact]
    public void Close_WhileOff_ExitsAsBefore()
    {
        var (window, _, log) = Shell(new FakeTrayReason());
        window.Show();

        window.Close();

        Assert.Equal(["closed"], log);
    }

    [AvaloniaFact]
    public void Tray_ShowsOnlyWhileAReasonHolds_AndSaysWhy()
    {
        var reason = new FakeTrayReason { Status = "Sharing: 2 watching" };
        var (window, tray, _) = Shell(reason);
        window.Show();
        Assert.False(tray.Icon.IsVisible);

        reason.Set(true);
        Ui.Flush();

        Assert.True(tray.Icon.IsVisible);
        Assert.Equal("Sharing: 2 watching", tray.Icon.ToolTipText);

        reason.Set(false);
        Ui.Flush();

        Assert.False(tray.Icon.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Tray_ReasonsAddUp()
    {
        var first = new FakeTrayReason { Status = "Sharing on" };
        var second = new FakeTrayReason { Status = "Playing" };
        var window = new Window();
        var tray = new TrayController(window, [first, second], _ => Task.FromResult(true));
        window.Show();

        first.Set(true);
        second.Set(true);
        Ui.Flush();

        Assert.Equal("Sharing on\nPlaying", tray.Icon.ToolTipText);

        first.Set(false);
        Ui.Flush();

        Assert.True(tray.Icon.IsVisible);
        Assert.Equal("Playing", tray.Icon.ToolTipText);
        second.Set(false);
        Ui.Flush();
        window.Close();
    }

    [AvaloniaFact]
    public void Tray_LastReasonEnds_WhileHidden_BringsTheWindowBack()
    {
        var reason = new FakeTrayReason();
        var (window, _, _) = Shell(reason);
        window.Show();
        reason.Set(true);
        Ui.Flush();
        window.Close();
        Assert.False(window.IsVisible);

        reason.Set(false);
        Ui.Flush();

        Assert.True(window.IsVisible); // nothing else would show that it's still running
        window.Close();
    }

    [AvaloniaFact]
    public void Tray_ReasonEnds_AfterTheWindowClosed_LeavesItClosed()
    {
        var reason = new FakeTrayReason();
        var (window, _, log) = Shell(reason);
        window.Show();
        window.Close();
        Assert.Equal(["closed"], log);

        reason.Set(false); // sharing stopping as the app exits

        Ui.Flush(); // would throw: a closed window can't be shown again
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Tray_Open_ShowsTheWindow()
    {
        var reason = new FakeTrayReason();
        var (window, tray, _) = Shell(reason);
        window.Show();
        reason.Set(true);
        Ui.Flush();
        window.Close();

        tray.ShowWindow();

        Assert.True(window.IsVisible);
        reason.Set(false);
        Ui.Flush();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Quit_WhileSomeoneWatches_Confirms()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        await world.Sharing.SetOnAsync(true);
        var (window, tray, log) = Shell(new SharingTrayReason(world.Sharing));
        world.OpenStream();
        window.Show();
        Ui.Flush();
        window.Close();
        Assert.False(window.IsVisible);

        await tray.QuitAsync();

        var asked = Assert.Single(log, line => line.StartsWith("asked:", StringComparison.Ordinal));
        Assert.StartsWith("asked:True:", asked, StringComparison.Ordinal); // a dialog needs its owner on screen
        Assert.Equal("closed", log[^1]);
    }

    [AvaloniaFact]
    public async Task Quit_WhileSomeoneWatches_Refused_KeepsRunning()
    {
        var reason = new FakeTrayReason { QuitQuestion = "Quit?" };
        var (window, tray, log) = Shell(reason, answer: false);
        window.Show();
        reason.Set(true);
        Ui.Flush();

        await tray.QuitAsync();

        Assert.DoesNotContain("closed", log);
        Assert.True(window.IsVisible);
        reason.Set(false);
        Ui.Flush();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Quit_NobodyWatching_ClosesWithoutAsking()
    {
        var reason = new FakeTrayReason();
        var (window, tray, log) = Shell(reason);
        window.Show();
        reason.Set(true);
        Ui.Flush();
        window.Close();

        await tray.QuitAsync();

        Assert.False(window.IsVisible); // no flash of the window on the way out
        Assert.Equal(["closed"], log);
    }

    [AvaloniaFact]
    public async Task SharingReason_FollowsTheController()
    {
        await using var world = new SharingWorld();
        world.OpenDatabase();
        var reason = new SharingTrayReason(world.Sharing);
        Assert.False(reason.Applies);

        await world.Sharing.SetOnAsync(true);

        Assert.True(reason.Applies);
        Assert.Equal("Sharing on", reason.Status);
        Assert.Null(reason.QuitQuestion);

        world.OpenStream();

        Assert.Equal("Sharing: 1 watching", reason.Status);
        Assert.NotNull(reason.QuitQuestion);
    }

    // ---- Starting in the tray ----

    [AvaloniaFact]
    public async Task StartHidden_WithAReason_StaysHidden()
    {
        var reason = new FakeTrayReason();
        var (window, tray, _) = Shell(reason);
        var settled = new TaskCompletionSource();

        var started = tray.StartHiddenAsync(settled.Task);
        reason.Set(true);
        Ui.Flush();
        settled.SetResult();
        await started;

        Assert.False(window.IsVisible);
        Assert.True(tray.Icon.IsVisible);
        reason.Set(false);
        Ui.Flush();
        window.Close();
    }

    [AvaloniaFact]
    public async Task StartHidden_NoReasonOnceSettled_ShowsTheWindow()
    {
        var (window, tray, _) = Shell(new FakeTrayReason());

        await tray.StartHiddenAsync(Task.CompletedTask);

        Assert.True(window.IsVisible); // an app with neither window nor tray icon can't be reached at all
        window.Close();
    }

    [AvaloniaFact]
    public void StartHidden_AReasonStillComing_DoesNotShowTheWindowYet()
    {
        var reason = new FakeTrayReason();
        var (window, tray, _) = Shell(reason);
        var settled = new TaskCompletionSource();

        _ = tray.StartHiddenAsync(settled.Task);
        reason.Set(false); // Starting raises a change while nothing applies yet
        Ui.Flush();

        Assert.False(window.IsVisible);
        settled.SetResult();
        Ui.Flush();
        window.Close();
    }

    // ---- Start with Windows ----

    [Fact]
    public async Task StartWithWindows_WritesTheProgramWithTheTrayArgument()
    {
        await using var world = new SharingWorld();
        var start = new StartWithWindows(world.Settings, world.RunKey, @"C:\Apps\MediaBoard.exe", world.Hub);

        Assert.True(start.Set(true));

        Assert.Equal("\"C:\\Apps\\MediaBoard.exe\" --tray", world.RunKey.Command);
        Assert.True(world.Settings.Load().Sharing.StartWithWindows);

        Assert.True(start.Set(false));

        Assert.Null(world.RunKey.Command);
        Assert.False(world.Settings.Load().Sharing.StartWithWindows);
    }

    [Fact]
    public async Task StartWithWindows_RegistryRefuses_KeepsTheSetting_AndLogsIt()
    {
        await using var world = new SharingWorld();
        world.RunKey.Refuses = true;
        var start = new StartWithWindows(world.Settings, world.RunKey, @"C:\Apps\MediaBoard.exe", world.Hub);

        Assert.False(start.Set(true));

        Assert.False(world.Settings.Load().Sharing.StartWithWindows);
        Assert.Contains(world.Hub.Recent, e => e.Level == ActivityLevel.Error);
    }

    [Theory]
    [InlineData(new[] { "--tray" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--other" }, false)]
    public void IsTrayStart_ReadsTheArgument(string[] args, bool expected) =>
        Assert.Equal(expected, StartWithWindows.IsTrayStart(args));
}
