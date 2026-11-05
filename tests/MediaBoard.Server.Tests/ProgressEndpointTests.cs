using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace MediaBoard.Server.Tests;

/// <summary>Saving where a viewer got to, on a clock the test moves by hand.</summary>
public sealed class ProgressEndpointTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

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

        public int Movie { get; private set; }

        public int File { get; private set; }

        public int ForbiddenFile { get; private set; }

        public Account Sam { get; private set; } = null!;

        public IssuedDevice Phone { get; private set; } = null!;

        public IssuedDevice Laptop { get; private set; } = null!;

        public Progress? Saved => Fixture.Progress.Get(Sam.Id, File);

        public static async Task<World> StartAsync()
        {
            var clock = new FakeTimeProvider(Start);
            var fixture = await ServerFixture.StartTestHostAsync(clock);
            var world = new World(fixture, clock);
            var open = fixture.AddFolder("open");
            var closed = fixture.AddFolder("closed");
            world.Movie = fixture.Movie("Heat", 101);
            world.File = await fixture.AddFileAsync(open, "Heat (1995).mkv", world.Movie);
            world.ForbiddenFile = await fixture.AddFileAsync(closed, "Ronin (1998).mkv", fixture.Movie("Ronin", 102));
            world.Sam = fixture.NewPerson("Sam", [open.Id]);
            world.Phone = fixture.Devices.Issue(world.Sam.Id, "Phone", "198.51.100.1");
            world.Laptop = fixture.Devices.Issue(world.Sam.Id, "Laptop", "198.51.100.1");
            return world;
        }

        public Task<HttpResponseMessage> PutProgressAsync(ProgressEntry entry, string? token = null, int? fileId = null) =>
            PutAsync($"/api/v1/files/{fileId ?? File}/progress", JsonContent.Create(entry, options: ApiJson.Options), token);

        public Task<HttpResponseMessage> PutRawProgressAsync(string json) =>
            PutAsync($"/api/v1/files/{File}/progress", new StringContent(json, Encoding.UTF8, "application/json"), null);

        public Task<HttpResponseMessage> PutWatchedAsync(string json, int? fileId = null) =>
            PutAsync($"/api/v1/files/{fileId ?? File}/watched", new StringContent(json, Encoding.UTF8, "application/json"), null);

        /// <summary>The file's row goes in the moment a write reaches it, after the access check passed.</summary>
        public void DeleteTheFileAsTheWriteLands()
        {
            using var connection = Fixture.Db.Open();
            connection.Execute("CREATE TRIGGER vanish BEFORE INSERT ON progress BEGIN DELETE FROM files WHERE id = NEW.file_id; END");
        }

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();

        private async Task<HttpResponseMessage> PutAsync(string route, HttpContent body, string? token)
        {
            using var request = Fixture.Request("PUT", route, token ?? Phone.Token);
            request.Content = body;
            return await Fixture.Client.SendAsync(request, Ct);
        }
    }

    private static ProgressEntry Entry(double positionS, DateTimeOffset at, bool? watched = null) => new(positionS, 3000, watched, at);

    [Fact]
    public async Task Progress_IsPerAccount()
    {
        await using var world = await World.StartAsync();
        var anna = world.Fixture.NewPerson("Anna");
        var annasPhone = world.Fixture.Devices.Issue(anna.Id, "Phone", "198.51.100.2");

        Assert.Equal(HttpStatusCode.NoContent, (await world.PutProgressAsync(Entry(100, Start))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await world.PutProgressAsync(Entry(200, Start), annasPhone.Token)).StatusCode);

        Assert.Equal(100, world.Saved!.PositionS);
        Assert.Equal(200, world.Fixture.Progress.Get(anna.Id, world.File)!.PositionS);
        Assert.Null(world.Fixture.Progress.Get(LibraryDatabase.OwnerId, world.File));
    }

    [Fact]
    public async Task Progress_IsReadBackFromTheFile()
    {
        await using var world = await World.StartAsync();

        await world.PutProgressAsync(Entry(100, Start));

        var response = await world.Fixture.Client.SendAsync(world.Fixture.Request("GET", $"/api/v1/files/{world.File}", world.Phone.Token), Ct);
        var reply = await response.Content.ReadFromJsonAsync<FileReply>(ApiJson.Options, Ct);
        Assert.Equal(100, reply!.Saved!.PositionS);
        Assert.Equal(3000, reply.Saved.DurationS);
    }

    [Fact]
    public async Task Progress_OlderEntry_Ignored()
    {
        await using var world = await World.StartAsync();
        await world.PutProgressAsync(Entry(200, Start));

        var response = await world.PutProgressAsync(Entry(100, Start.AddMinutes(-1)), world.Laptop.Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(200, world.Saved!.PositionS);
        Assert.Equal(Start.UtcDateTime, world.Saved.LastPlayed);
    }

    [Fact]
    public async Task Progress_TwoDevices_NewestWins_InEitherArrivalOrder()
    {
        await using var world = await World.StartAsync();
        world.Clock.Advance(TimeSpan.FromMinutes(5));

        await world.PutProgressAsync(Entry(300, Start.AddMinutes(2)));
        await world.PutProgressAsync(Entry(100, Start.AddMinutes(1)), world.Laptop.Token);
        Assert.Equal(300, world.Saved!.PositionS);

        await world.PutProgressAsync(Entry(400, Start.AddMinutes(3)), world.Laptop.Token);
        Assert.Equal(400, world.Saved!.PositionS);
    }

    // A phone an hour ahead: taken at its word it would beat every honest entry for the next hour.
    [Fact]
    public async Task Progress_FutureAt_ClampedToServerNow()
    {
        await using var world = await World.StartAsync();

        await world.PutProgressAsync(Entry(1000, Start.AddHours(1)));

        Assert.Equal(1000, world.Saved!.PositionS);
        Assert.Equal(Start.UtcDateTime, world.Saved.LastPlayed);

        world.Clock.Advance(TimeSpan.FromSeconds(10));
        await world.PutProgressAsync(Entry(50, world.Clock.GetUtcNow()), world.Laptop.Token);

        Assert.Equal(50, world.Saved!.PositionS);
        Assert.Equal(Start.AddSeconds(10).UtcDateTime, world.Saved.LastPlayed);
    }

    [Fact]
    public async Task Progress_ClockAhead_StillMovesForward_OnItsOwn()
    {
        await using var world = await World.StartAsync();
        await world.PutProgressAsync(Entry(1000, Start.AddHours(1)));

        world.Clock.Advance(TimeSpan.FromSeconds(10));
        await world.PutProgressAsync(Entry(1010, Start.AddHours(1).AddSeconds(10)));

        Assert.Equal(1010, world.Saved!.PositionS);
        Assert.Equal(Start.AddSeconds(10).UtcDateTime, world.Saved.LastPlayed);
    }

    [Theory]
    [InlineData("progress")]
    [InlineData("watched")]
    public async Task Progress_OnlyForReachableFile(string route)
    {
        await using var world = await World.StartAsync();
        var hiddenTitle = world.Fixture.Movie("Alien", 103);
        var open = world.Fixture.Folders.All().Single(f => f.Path.EndsWith("open", StringComparison.Ordinal));
        var hidden = await world.Fixture.AddFileAsync(open, "Alien (1979).mkv", hiddenTitle);
        world.Fixture.Owner.SetHidden(TargetKind.Title, hiddenTitle, hidden: true);
        var notMedia = await world.Fixture.AddFileAsync(open, "Trailer (2001).mkv", world.Fixture.Movie("Trailer", 104));
        world.Fixture.Owner.MarkNotMedia(notMedia);

        foreach (var id in new[] { world.ForbiddenFile, hidden, notMedia, 99999 })
        {
            var response = route == "progress"
                ? await world.PutProgressAsync(Entry(100, Start), fileId: id)
                : await world.PutWatchedAsync("{\"watched\":true}", id);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ApiErrorCode.NotFound, await ServerFixture.CodeOf(response));
            Assert.Null(world.Fixture.Progress.Get(world.Sam.Id, id));
        }
    }

    [Theory]
    [InlineData("progress")]
    [InlineData("watched")]
    public async Task Progress_FileDeletedAfterTheCheck_404(string route)
    {
        await using var world = await World.StartAsync();
        world.DeleteTheFileAsTheWriteLands();

        var response = route == "progress"
            ? await world.PutProgressAsync(Entry(100, Start))
            : await world.PutWatchedAsync("{\"watched\":true}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCode.NotFound, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task Watched_CarriedInEntry()
    {
        await using var world = await World.StartAsync();

        await world.PutProgressAsync(Entry(2900, Start, watched: true));
        Assert.True(world.Saved!.Watched);

        // No word on watched in this one, so it stays as it is.
        await world.PutProgressAsync(Entry(2950, Start));
        Assert.True(world.Saved!.Watched);
        Assert.Equal(2950, world.Saved.PositionS);
    }

    [Fact]
    public async Task Watched_InAnOlderEntry_Ignored()
    {
        await using var world = await World.StartAsync();
        await world.PutProgressAsync(Entry(100, Start));

        await world.PutProgressAsync(Entry(2900, Start.AddMinutes(-1), watched: true), world.Laptop.Token);

        Assert.False(world.Saved!.Watched);
    }

    [Fact]
    public async Task Watched_Put_SetsTheFlag_AndClearingForgetsThePosition()
    {
        await using var world = await World.StartAsync();
        await world.PutProgressAsync(Entry(100, Start));

        Assert.Equal(HttpStatusCode.NoContent, (await world.PutWatchedAsync("{\"watched\":true}")).StatusCode);
        Assert.True(world.Saved!.Watched);
        Assert.Equal(100, world.Saved.PositionS);

        Assert.Equal(HttpStatusCode.NoContent, (await world.PutWatchedAsync("{\"watched\":false}")).StatusCode);
        Assert.False(world.Saved!.Watched);
        Assert.Equal(0, world.Saved.PositionS);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"watched\":\"maybe\"}")]
    public async Task Watched_BadBody_400(string json)
    {
        await using var world = await World.StartAsync();

        var response = await world.PutWatchedAsync(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCode.BadRequest, await ServerFixture.CodeOf(response));
        Assert.Null(world.Saved);
    }

    [Fact]
    public async Task Progress_FeedsHeartbeat()
    {
        await using var world = await World.StartAsync();
        var stream = world.Fixture.Request("GET", $"/api/v1/files/{world.File}/stream", world.Phone.Token);
        await (await world.Fixture.Client.SendAsync(stream, Ct)).Content.ReadAsByteArrayAsync(Ct);

        // Past the 30 s quiet limit twice over, on nothing but reports. A paused player sends no bytes.
        world.Clock.Advance(TimeSpan.FromSeconds(25));
        await world.PutProgressAsync(Entry(100, world.Clock.GetUtcNow()));
        world.Clock.Advance(TimeSpan.FromSeconds(25));
        await world.PutProgressAsync(Entry(100, world.Clock.GetUtcNow()));
        world.Clock.Advance(TimeSpan.FromSeconds(25));

        var session = Assert.Single(world.Fixture.Streams.Current);
        Assert.Equal(100, session.PositionS);
        Assert.Equal(3000, session.DurationS);

        world.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(world.Fixture.Streams.Current);
    }

    // A slow clock makes every report older than what is saved. The player is still there.
    [Fact]
    public async Task Progress_OlderEntry_StillFeedsHeartbeat()
    {
        await using var world = await World.StartAsync();
        await world.PutProgressAsync(Entry(500, Start));
        var stream = world.Fixture.Request("GET", $"/api/v1/files/{world.File}/stream", world.Phone.Token);
        await (await world.Fixture.Client.SendAsync(stream, Ct)).Content.ReadAsByteArrayAsync(Ct);

        world.Clock.Advance(TimeSpan.FromSeconds(25));
        await world.PutProgressAsync(Entry(100, Start.AddHours(-1)));
        world.Clock.Advance(TimeSpan.FromSeconds(25));

        Assert.Single(world.Fixture.Streams.Current);
        Assert.Equal(500, world.Saved!.PositionS);
    }

    [Theory]
    [InlineData("{\"positionS\":-1,\"durationS\":3000,\"at\":\"2026-11-05T12:00:00Z\"}")]
    [InlineData("{\"positionS\":1,\"durationS\":-1,\"at\":\"2026-11-05T12:00:00Z\"}")]
    [InlineData("{\"positionS\":\"NaN\",\"durationS\":3000,\"at\":\"2026-11-05T12:00:00Z\"}")]
    [InlineData("{\"positionS\":1,\"durationS\":\"NaN\",\"at\":\"2026-11-05T12:00:00Z\"}")]
    [InlineData("{\"positionS\":\"Infinity\",\"durationS\":3000,\"at\":\"2026-11-05T12:00:00Z\"}")]
    [InlineData("{\"positionS\":1e999,\"durationS\":3000,\"at\":\"2026-11-05T12:00:00Z\"}")]
    [InlineData("{\"positionS\":1,\"durationS\":1e999,\"at\":\"2026-11-05T12:00:00Z\"}")]
    [InlineData("{\"positionS\":1,\"durationS\":3000}")]
    [InlineData("{\"durationS\":3000,\"at\":\"2026-11-05T12:00:00Z\"}")]
    [InlineData("{\"positionS\":1,\"at\":\"2026-11-05T12:00:00Z\"}")]
    [InlineData("{\"at\":\"2026-11-05T12:00:00Z\"}")]
    [InlineData("{\"positionS\":1,\"durationS\":3000,\"at\":\"yesterday\"}")]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task Progress_NaNOrNegative_Rejected(string json)
    {
        await using var world = await World.StartAsync();

        var response = await world.PutRawProgressAsync(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCode.BadRequest, await ServerFixture.CodeOf(response));
        Assert.Null(world.Saved);
    }

    [Fact]
    public async Task Progress_NoPosition_KeepsTheSavedOne()
    {
        await using var world = await World.StartAsync();
        await world.PutProgressAsync(Entry(500, Start));

        await world.PutRawProgressAsync("{\"durationS\":3000,\"at\":\"2026-11-05T12:00:00Z\"}");

        Assert.Equal(500, world.Saved!.PositionS);
    }

    [Fact]
    public async Task Progress_NoWordOnWatched_IsFine()
    {
        await using var world = await World.StartAsync();

        var response = await world.PutRawProgressAsync("{\"positionS\":1,\"durationS\":3000,\"at\":\"2026-11-05T12:00:00Z\"}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Progress_BadBody_FeedsNoHeartbeat()
    {
        await using var world = await World.StartAsync();
        var stream = world.Fixture.Request("GET", $"/api/v1/files/{world.File}/stream", world.Phone.Token);
        await (await world.Fixture.Client.SendAsync(stream, Ct)).Content.ReadAsByteArrayAsync(Ct);

        await world.PutRawProgressAsync("{\"positionS\":1e999,\"durationS\":3000,\"at\":\"2026-11-05T12:00:00Z\"}");

        Assert.Equal(0, Assert.Single(world.Fixture.Streams.Current).PositionS);
    }
}
