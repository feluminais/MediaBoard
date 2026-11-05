using System.Net;
using System.Net.Http.Json;
using System.Text;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace MediaBoard.Server.Tests;

/// <summary>What a signed-in device can do about its own session.</summary>
public sealed class SessionEndpointTests
{
    private const string NewPassword = "a different long one";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World : IAsyncDisposable
    {
        private World(ServerFixture fixture) => Fixture = fixture;

        public ServerFixture Fixture { get; }

        public Account Sam { get; private set; } = null!;

        public IssuedDevice Phone { get; private set; } = null!;

        public IssuedDevice Laptop { get; private set; } = null!;

        public static async Task<World> StartAsync(TimeProvider? time = null)
        {
            var fixture = await ServerFixture.StartTestHostAsync(time);
            var world = new World(fixture) { Sam = fixture.NewPerson("Sam") };
            world.Laptop = fixture.Join(world.Sam);
            world.Phone = fixture.Devices.Issue(world.Sam.Id, "Phone", "198.51.100.1");
            return world;
        }

        public async Task<HttpResponseMessage> GetMeAsync(string token) =>
            await Fixture.Client.SendAsync(Fixture.Request("GET", "/api/v1/me", token), Ct);

        public async Task<HttpResponseMessage> ChangePasswordAsync(string json)
        {
            using var request = Fixture.Request("PUT", "/api/v1/me/password", Phone.Token);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return await Fixture.Client.SendAsync(request, Ct);
        }

        public Task<HttpResponseMessage> ChangePasswordAsync(string current, string next) =>
            ChangePasswordAsync($$"""{"current":"{{current}}","next":"{{next}}"}""");

        public bool SignsInWith(string password) =>
            Fixture.Service.SignIn(Sam.Name, password, "Tablet", "198.51.100.1").Issued is not null;

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    [Fact]
    public async Task Me_NamesTheAccountAndTheOwner()
    {
        await using var world = await World.StartAsync();

        var reply = await (await world.GetMeAsync(world.Phone.Token)).Content.ReadFromJsonAsync<MeReply>(ApiJson.Options, Ct);

        Assert.Equal(new MeReply("Sam", world.Fixture.Accounts.Get(LibraryDatabase.OwnerId)!.Name), reply);
    }

    [Fact]
    public async Task SignOut_RevokesThisDevice()
    {
        await using var world = await World.StartAsync();

        var response = await world.Fixture.Client.SendAsync(world.Fixture.Request("POST", "/api/v1/sign-out", world.Phone.Token), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.GetMeAsync(world.Phone.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await world.GetMeAsync(world.Laptop.Token)).StatusCode);
    }

    [Fact]
    public async Task SignOut_EndsThisDevicesStreams()
    {
        await using var world = await World.StartAsync();
        var folder = world.Fixture.AddFolder("open");
        var file = await world.Fixture.AddFileAsync(folder, "Heat (1995).mkv", world.Fixture.Movie("Heat", 101));
        await (await world.Fixture.Client.SendAsync(
            world.Fixture.Request("GET", $"/api/v1/files/{file}/stream", world.Phone.Token), Ct)).Content.ReadAsByteArrayAsync(Ct);
        Assert.Single(world.Fixture.Streams.Current);

        await world.Fixture.Client.SendAsync(world.Fixture.Request("POST", "/api/v1/sign-out", world.Phone.Token), Ct);

        Assert.Empty(world.Fixture.Streams.Current);
    }

    [Fact]
    public async Task ChangePassword_NeedsCurrent()
    {
        await using var world = await World.StartAsync();

        var response = await world.ChangePasswordAsync("not the password", NewPassword);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCode.WrongPassword, await ServerFixture.CodeOf(response));
        Assert.True(world.SignsInWith(ServerFixture.Password));
    }

    [Fact]
    public async Task ChangePassword_ChangesIt()
    {
        await using var world = await World.StartAsync();

        var response = await world.ChangePasswordAsync(ServerFixture.Password, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(world.SignsInWith(NewPassword));
        Assert.False(world.SignsInWith(ServerFixture.Password));
    }

    [Fact]
    public async Task ChangePassword_KeepsOtherDevices()
    {
        await using var world = await World.StartAsync();

        await world.ChangePasswordAsync(ServerFixture.Password, NewPassword);

        Assert.Equal(HttpStatusCode.OK, (await world.GetMeAsync(world.Phone.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await world.GetMeAsync(world.Laptop.Token)).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_TooShort()
    {
        await using var world = await World.StartAsync();

        var response = await world.ChangePasswordAsync(ServerFixture.Password, "short");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCode.PasswordTooShort, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task ChangePassword_AfterFiveWrongGuesses_Locked()
    {
        await using var world = await World.StartAsync();
        for (var i = 0; i < 5; i++) await world.ChangePasswordAsync("not the password", NewPassword);

        var response = await world.ChangePasswordAsync(ServerFixture.Password, NewPassword);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(ApiErrorCode.Locked, await ServerFixture.CodeOf(response));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"current\":\"correct horse battery\"}")]
    [InlineData("{\"next\":\"a different long one\"}")]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task ChangePassword_BadBody_400(string json)
    {
        await using var world = await World.StartAsync();

        var response = await world.ChangePasswordAsync(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCode.BadRequest, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task Request_NotesLastSeen_AtMostOnceAMinute()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        await using var world = await World.StartAsync(clock);
        var issuedAt = clock.GetUtcNow().UtcDateTime;
        world.Fixture.RemoteAddress = IPAddress.Parse("203.0.113.9");

        clock.Advance(TimeSpan.FromSeconds(59));
        await world.GetMeAsync(world.Phone.Token);

        Assert.Equal(issuedAt, world.Fixture.Devices.Get(world.Phone.Device.Id)!.LastSeen);
        Assert.Equal("198.51.100.1", world.Fixture.Devices.Get(world.Phone.Device.Id)!.LastAddress);
        Assert.Null(world.Fixture.Accounts.Get(world.Sam.Id)!.LastSeen);

        clock.Advance(TimeSpan.FromSeconds(1));
        await world.GetMeAsync(world.Phone.Token);

        var seenAt = clock.GetUtcNow().UtcDateTime;
        Assert.Equal(seenAt, world.Fixture.Devices.Get(world.Phone.Device.Id)!.LastSeen);
        Assert.Equal("203.0.113.9", world.Fixture.Devices.Get(world.Phone.Device.Id)!.LastAddress);
        Assert.Equal(seenAt, world.Fixture.Accounts.Get(world.Sam.Id)!.LastSeen);
        // The other device wasn't heard from.
        Assert.Equal(issuedAt, world.Fixture.Devices.Get(world.Laptop.Device.Id)!.LastSeen);
    }

    [Fact]
    public async Task Request_WithADeadToken_NotesNothing()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        await using var world = await World.StartAsync(clock);
        clock.Advance(TimeSpan.FromHours(1));

        await world.GetMeAsync("no-such-token");

        Assert.Null(world.Fixture.Accounts.Get(world.Sam.Id)!.LastSeen);
    }
}
