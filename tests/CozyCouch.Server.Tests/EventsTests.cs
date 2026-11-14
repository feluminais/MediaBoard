using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using CozyCouch.Core.Accounts;
using CozyCouch.Core.Remote;
using Microsoft.Extensions.Time.Testing;

namespace CozyCouch.Server.Tests;

/// <summary>
/// The live events. A clock the test moves makes the timing exact, and a device that should get nothing is shown to
/// have got nothing by the next thing it reads being the keep-alive that comes after.
/// </summary>
public sealed class EventsTests
{
    private const string KeepAlive = ": keep-alive";
    private const string LibraryChanged = "event: library-changed";
    private const string StreamStopped = "event: stream-stopped";
    private const string SignedOut = "event: signed-out";

    private static readonly TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReadWithin = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World : IAsyncDisposable
    {
        private World(ServerFixture fixture, FakeTimeProvider clock)
        {
            Fixture = fixture;
            Clock = clock;
        }

        public ServerFixture Fixture { get; }

        public FakeTimeProvider Clock { get; }

        public Account Sam { get; private set; } = null!;

        public IssuedDevice Phone { get; private set; } = null!;

        public IssuedDevice Laptop { get; private set; } = null!;

        public static async Task<World> StartAsync()
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
            var fixture = await ServerFixture.StartTestHostAsync(clock);
            var world = new World(fixture, clock) { Sam = fixture.NewPerson("Sam") };
            world.Phone = fixture.Devices.Issue(world.Sam.Id, "Phone", "198.51.100.1");
            world.Laptop = fixture.Devices.Issue(world.Sam.Id, "Laptop", "198.51.100.1");
            return world;
        }

        public Task<StreamReader> ListenAsync(string token) => EventsTests.ListenAsync(Fixture, Fixture.Client, "", token);

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    private static async Task<StreamReader> ListenAsync(ServerFixture fixture, HttpClient client, string host, string token)
    {
        using var request = fixture.Request("GET", $"{host}/api/v1/events", token);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
    }

    /// <summary>The next event or comment line, or null at the end of the stream. Its data line is empty and skipped.</summary>
    private static async Task<string?> NextAsync(StreamReader events)
    {
        while (true)
        {
            var line = await events.ReadLineAsync(Ct).AsTask().WaitAsync(ReadWithin, Ct);
            if (line is null || (line.Length > 0 && !line.StartsWith("data:", StringComparison.Ordinal))) return line;
        }
    }

    private static async Task AssertEndedAsync(StreamReader events) => Assert.Null(await NextAsync(events));

    [Fact]
    public async Task Events_FrameIsAWholeServerSentEvent()
    {
        await using var world = await World.StartAsync();
        var events = await world.ListenAsync(world.Phone.Token);

        world.Fixture.Service.SignOutDevice(world.Phone.Device.Id);

        Assert.Equal("event: signed-out\ndata: \n\n", await events.ReadToEndAsync(Ct).WaitAsync(ReadWithin, Ct));
    }

    [Fact]
    public async Task Events_LibraryChanged_Coalesced5s()
    {
        await using var world = await World.StartAsync();
        var events = await world.ListenAsync(world.Phone.Token);

        world.Fixture.Owner.NotifyChanged();
        world.Fixture.Owner.NotifyChanged();
        world.Clock.Advance(TimeSpan.FromSeconds(4));
        world.Fixture.Owner.NotifyChanged();
        world.Clock.Advance(TimeSpan.FromSeconds(1));

        // One for the three, and nothing more by the keep-alive at 20 s.
        Assert.Equal(LibraryChanged, await NextAsync(events));
        world.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(KeepAlive, await NextAsync(events));

        world.Fixture.Owner.NotifyChanged();
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(LibraryChanged, await NextAsync(events));
    }

    [Fact]
    public async Task Events_OneAccountsProgress_ReachesOnlyThatAccount()
    {
        await using var world = await World.StartAsync();
        var other = world.Fixture.NewPerson("Ann");
        var theirs = await world.ListenAsync(world.Fixture.Devices.Issue(other.Id, "Tablet", "198.51.100.2").Token);
        var own = await world.ListenAsync(world.Phone.Token);

        world.Fixture.Owner.ForViewer(world.Fixture.Accounts.ViewerOf(world.Sam)).NotifyProgressChanged();
        world.Clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(LibraryChanged, await NextAsync(own));

        // The next frame the other account gets is the keep-alive, so the change never reached it.
        world.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(KeepAlive, await NextAsync(theirs));
    }

    [Fact]
    public async Task Events_LibraryChanged_NotBefore5s()
    {
        await using var world = await World.StartAsync();
        var events = await world.ListenAsync(world.Phone.Token);

        world.Clock.Advance(TimeSpan.FromSeconds(4.9));
        world.Fixture.Owner.NotifyChanged();
        world.Clock.Advance(KeepAliveEvery - TimeSpan.FromSeconds(4.9));

        // The change came 15.1 s before the keep-alive, so it should be out first, and only 5 s after it was raised.
        Assert.Equal(LibraryChanged, await NextAsync(events));
        Assert.Equal(KeepAlive, await NextAsync(events));
    }

    [Fact]
    public async Task Events_LibraryChanged_GoesToEveryDevice()
    {
        await using var world = await World.StartAsync();
        var phone = await world.ListenAsync(world.Phone.Token);
        var laptop = await world.ListenAsync(world.Laptop.Token);

        world.Fixture.Owner.NotifyChanged();
        world.Clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(LibraryChanged, await NextAsync(phone));
        Assert.Equal(LibraryChanged, await NextAsync(laptop));
    }

    [Fact]
    public async Task Events_WatchedPut_ReachesTheOtherDevices()
    {
        await using var world = await World.StartAsync();
        var folder = world.Fixture.AddFolder("open");
        var file = await world.Fixture.AddFileAsync(folder, "Heat (1995).mkv", world.Fixture.Movie("Heat", 101));
        var laptop = await world.ListenAsync(world.Laptop.Token);
        using var request = world.Fixture.Request("PUT", $"/api/v1/files/{file}/watched", world.Phone.Token);
        request.Content = JsonContent.Create(new WatchedRequest(true), options: ApiJson.Options);

        await world.Fixture.Client.SendAsync(request, Ct);
        world.Clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(LibraryChanged, await NextAsync(laptop));
    }

    [Fact]
    public async Task Events_FolderChange_ReachesThatAccountOnly_Coalesced()
    {
        await using var world = await World.StartAsync();
        var anna = world.Fixture.Devices.Issue(world.Fixture.NewPerson("Anna").Id, "Phone", "198.51.100.2");
        var annas = await world.ListenAsync(anna.Token);
        var sams = await world.ListenAsync(world.Phone.Token);

        world.Fixture.Service.SetFolders(world.Sam.Id, []);
        world.Clock.Advance(TimeSpan.FromSeconds(4));
        world.Fixture.Service.SetFolders(world.Sam.Id, null);
        world.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(LibraryChanged, await NextAsync(sams));
        world.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(KeepAlive, await NextAsync(sams));
        Assert.Equal(KeepAlive, await NextAsync(annas));
    }

    [Fact]
    public async Task Events_FolderChange_AndLibraryChange_ShareOneFrame()
    {
        await using var world = await World.StartAsync();
        var anna = world.Fixture.Devices.Issue(world.Fixture.NewPerson("Anna").Id, "Phone", "198.51.100.2");
        var annas = await world.ListenAsync(anna.Token);
        var sams = await world.ListenAsync(world.Phone.Token);

        world.Fixture.Service.SetFolders(world.Sam.Id, []);
        world.Fixture.Owner.NotifyChanged();
        world.Clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(LibraryChanged, await NextAsync(sams));
        Assert.Equal(LibraryChanged, await NextAsync(annas));
        world.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(KeepAlive, await NextAsync(sams));
        Assert.Equal(KeepAlive, await NextAsync(annas));
    }

    [Fact]
    public async Task Events_FolderChange_DoesNotCarryOverToTheNextChange()
    {
        await using var world = await World.StartAsync();
        var anna = world.Fixture.NewPerson("Anna");
        var annas = await world.ListenAsync(world.Fixture.Devices.Issue(anna.Id, "Phone", "198.51.100.2").Token);
        var sams = await world.ListenAsync(world.Phone.Token);

        world.Fixture.Service.SetFolders(world.Sam.Id, []);
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(LibraryChanged, await NextAsync(sams));

        world.Fixture.Service.SetFolders(anna.Id, []);
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(LibraryChanged, await NextAsync(annas));

        world.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(KeepAlive, await NextAsync(sams));
    }

    [Fact]
    public async Task Events_NewConnection_EndsTheDevicesOlderOne()
    {
        await using var world = await World.StartAsync();
        var laptop = await world.ListenAsync(world.Laptop.Token);
        var first = await world.ListenAsync(world.Phone.Token);
        var second = await world.ListenAsync(world.Phone.Token);
        var third = await world.ListenAsync(world.Phone.Token);

        await AssertEndedAsync(first);
        await AssertEndedAsync(second);

        world.Clock.Advance(KeepAliveEvery);
        Assert.Equal(KeepAlive, await NextAsync(third));
        Assert.Equal(KeepAlive, await NextAsync(laptop));
    }

    [Fact]
    public async Task Events_NewConnection_StillGetsWhatTheDeviceIsOwed()
    {
        await using var world = await World.StartAsync();
        await world.ListenAsync(world.Phone.Token);
        var second = await world.ListenAsync(world.Phone.Token);

        world.Fixture.Service.SignOutDevice(world.Phone.Device.Id);

        Assert.Equal(SignedOut, await NextAsync(second));
        await AssertEndedAsync(second);
    }

    [Fact]
    public async Task Events_SignedOut_OnDisable()
    {
        await using var world = await World.StartAsync();
        var phone = await world.ListenAsync(world.Phone.Token);
        var laptop = await world.ListenAsync(world.Laptop.Token);

        world.Fixture.Service.Disable(world.Sam.Id, disabled: true);

        Assert.Equal(SignedOut, await NextAsync(phone));
        Assert.Equal(SignedOut, await NextAsync(laptop));
        await AssertEndedAsync(phone);
        await AssertEndedAsync(laptop);
    }

    [Fact]
    public async Task Events_SignedOut_OnRemoveAndOnReset_ToThatAccountOnly()
    {
        await using var world = await World.StartAsync();
        var anna = world.Fixture.NewPerson("Anna");
        var annasPhone = world.Fixture.Devices.Issue(anna.Id, "Phone", "198.51.100.2");
        var bystander = await world.ListenAsync(world.Laptop.Token);
        var annas = await world.ListenAsync(annasPhone.Token);
        var sams = await world.ListenAsync(world.Phone.Token);

        world.Fixture.Service.Remove(anna.Id);
        Assert.Equal(SignedOut, await NextAsync(annas));

        world.Clock.Advance(KeepAliveEvery);
        Assert.Equal(KeepAlive, await NextAsync(sams));
        Assert.Equal(KeepAlive, await NextAsync(bystander));

        world.Fixture.Service.ResetPassword(world.Sam.Id);
        Assert.Equal(SignedOut, await NextAsync(sams));
        Assert.Equal(SignedOut, await NextAsync(bystander));
    }

    [Fact]
    public async Task Events_SignedOut_OnDeviceSignOut_ToThatDeviceOnly()
    {
        await using var world = await World.StartAsync();
        var phone = await world.ListenAsync(world.Phone.Token);
        var laptop = await world.ListenAsync(world.Laptop.Token);

        world.Fixture.Service.SignOutDevice(world.Phone.Device.Id);

        Assert.Equal(SignedOut, await NextAsync(phone));
        await AssertEndedAsync(phone);
        world.Clock.Advance(KeepAliveEvery);
        Assert.Equal(KeepAlive, await NextAsync(laptop));
    }

    [Fact]
    public async Task Events_StreamStopped_ToThatDeviceOnly()
    {
        await using var world = await World.StartAsync();
        var folder = world.Fixture.AddFolder("open");
        var file = await world.Fixture.AddFileAsync(folder, "Heat (1995).mkv", world.Fixture.Movie("Heat", 101));
        var phone = await world.ListenAsync(world.Phone.Token);
        var laptop = await world.ListenAsync(world.Laptop.Token);
        await (await world.Fixture.Client.SendAsync(
            world.Fixture.Request("GET", $"/api/v1/files/{file}/stream", world.Phone.Token), Ct)).Content.ReadAsByteArrayAsync(Ct);

        world.Fixture.Streams.Stop(Assert.Single(world.Fixture.Streams.Current).Id);

        Assert.Equal(StreamStopped, await NextAsync(phone));
        world.Clock.Advance(KeepAliveEvery);
        Assert.Equal(KeepAlive, await NextAsync(phone));
        Assert.Equal(KeepAlive, await NextAsync(laptop));
    }

    [Fact]
    public async Task Events_StreamClosedForAccess_IsNotAStop()
    {
        await using var world = await World.StartAsync();
        var folder = world.Fixture.AddFolder("open");
        var file = await world.Fixture.AddFileAsync(folder, "Heat (1995).mkv", world.Fixture.Movie("Heat", 101));
        var phone = await world.ListenAsync(world.Phone.Token);
        await (await world.Fixture.Client.SendAsync(
            world.Fixture.Request("GET", $"/api/v1/files/{file}/stream", world.Phone.Token), Ct)).Content.ReadAsByteArrayAsync(Ct);

        world.Fixture.Streams.Close(Assert.Single(world.Fixture.Streams.Current).Id);

        world.Clock.Advance(KeepAliveEvery);
        Assert.Equal(KeepAlive, await NextAsync(phone));
    }

    [Fact]
    public async Task Events_KeepAlive()
    {
        await using var world = await World.StartAsync();
        var events = await world.ListenAsync(world.Phone.Token);

        world.Clock.Advance(KeepAliveEvery);
        Assert.Equal(KeepAlive, await NextAsync(events));

        world.Clock.Advance(KeepAliveEvery);
        Assert.Equal(KeepAlive, await NextAsync(events));
    }

    // A connection that gets past DeviceAuth just before the revoke, then registers just after its event went out,
    // would listen for a session that no longer exists.
    [Fact]
    public async Task Events_TokenDiesAfterTheCheck_401()
    {
        await using var world = await World.StartAsync();
        world.Clock.Advance(TimeSpan.FromHours(1));
        using (var connection = world.Fixture.Db.Open())
        {
            // The last thing DeviceAuth does is this touch, so the device goes after every check has passed.
            connection.Execute("CREATE TRIGGER vanish AFTER UPDATE OF last_seen ON users BEGIN DELETE FROM devices WHERE user_id = NEW.id; END");
        }

        // Headers only: if the connection stayed open, its body would never end.
        var response = await world.Fixture.Client.SendAsync(
            world.Fixture.Request("GET", "/api/v1/events", world.Phone.Token), HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCode.Unauthorized, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task Events_ConnectionClosesOnStop()
    {
        var logs = new HostTests.LogCapture();
        await using var fixture = new ServerFixture(logs);
        var port = ServerFixture.FreePort();
        await fixture.Server.StartAsync(new SharingOptions(port, fixture.NewCertificate()), Ct);
        var phone = fixture.Devices.Issue(fixture.NewPerson().Id, "Phone", "198.51.100.1");
        var events = await ListenAsync(fixture, fixture.TlsClient(), $"https://127.0.0.1:{port}", phone.Token);

        var clock = Stopwatch.StartNew();
        await fixture.Server.StopAsync();

        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<IOException>(() => events.ReadToEndAsync(Ct).WaitAsync(ReadWithin, Ct));
        Assert.Empty(logs.Lines);
    }

    [Fact]
    public async Task Events_AfterARestart_StillArrive_AndOnlyOnce()
    {
        await using var fixture = new ServerFixture();
        var certificate = fixture.NewCertificate();
        await fixture.Server.StartAsync(new SharingOptions(ServerFixture.FreePort(), certificate), Ct);
        await fixture.Server.StopAsync();
        var port = ServerFixture.FreePort();
        await fixture.Server.StartAsync(new SharingOptions(port, certificate), Ct);
        var phone = fixture.Devices.Issue(fixture.NewPerson().Id, "Phone", "198.51.100.1");
        var events = await ListenAsync(fixture, fixture.TlsClient(), $"https://127.0.0.1:{port}", phone.Token);

        fixture.Service.SignOutDevice(phone.Device.Id);

        Assert.Equal(SignedOut, await NextAsync(events));
        await AssertEndedAsync(events);
    }

    [Fact]
    public async Task Events_ClientLeaves_TheRestCarryOn()
    {
        await using var world = await World.StartAsync();
        using var leaving = new CancellationTokenSource();
        using var request = world.Fixture.Request("GET", "/api/v1/events", world.Phone.Token);
        using var gone = await world.Fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, leaving.Token);
        var staying = await world.ListenAsync(world.Laptop.Token);

        await leaving.CancelAsync();
        gone.Dispose();

        world.Clock.Advance(KeepAliveEvery);
        Assert.Equal(KeepAlive, await NextAsync(staying));
    }
}
