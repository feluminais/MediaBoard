using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Storage;

namespace MediaBoard.Server.Tests;

/// <summary>The library, search and image routes over a library with one folder a viewer may reach and one they may not.</summary>
public sealed class LibraryEndpointTests
{
    private const string ForbiddenName = "Ronin";
    private const int ForbiddenTmdbId = 301;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World : IAsyncDisposable
    {
        private World(ServerFixture fixture) => Fixture = fixture;

        public ServerFixture Fixture { get; }

        public LibraryFolder Open { get; private set; } = null!;

        public LibraryFolder Closed { get; private set; } = null!;

        public int Heat { get; private set; }

        public int HeatFile { get; private set; }

        public int Ronin { get; private set; }

        /// <summary>A person who may reach only the open folder.</summary>
        public Account Sam { get; private set; } = null!;

        public string SamToken { get; private set; } = null!;

        public string OwnerToken { get; private set; } = null!;

        public static async Task<World> CreateAsync()
        {
            var fixture = await ServerFixture.StartTestHostAsync();
            var world = new World(fixture)
            {
                Open = fixture.AddFolder("open"),
                Closed = fixture.AddFolder("closed"),
            };
            world.Heat = fixture.Movie("Heat", 101);
            world.Ronin = fixture.Movie(ForbiddenName, ForbiddenTmdbId);
            world.HeatFile = await fixture.AddFileAsync(world.Open, @"Sub\Heat (1995).mkv", world.Heat);
            await fixture.AddFileAsync(world.Closed, "Ronin (1998).mkv", world.Ronin);
            world.Sam = fixture.NewPerson("Sam", [world.Open.Id]);
            world.SamToken = fixture.Devices.Issue(world.Sam.Id, "Phone", "198.51.100.1").Token;
            world.OwnerToken = fixture.Devices.Issue(LibraryDatabase.OwnerId, "PC", "198.51.100.1").Token;
            return world;
        }

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();

        public async Task<(HttpStatusCode Status, string Body)> SendAsync(string method, string route, string? token = null)
        {
            var response = await Fixture.Client.SendAsync(Fixture.Request(method, route, token ?? SamToken), Ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        }

        public async Task<T> GetAsync<T>(string route, string? token = null)
        {
            var (status, body) = await SendAsync("GET", route, token);
            Assert.Equal(HttpStatusCode.OK, status);
            return JsonSerializer.Deserialize<T>(body, ApiJson.Options)!;
        }
    }

    [Fact]
    public async Task Titles_FollowViewerFolders()
    {
        await using var world = await World.CreateAsync();

        var viewer = await world.GetAsync<List<TitleCard>>("/api/v1/titles?kind=movie");
        var owner = await world.GetAsync<List<TitleCard>>("/api/v1/titles?kind=movie", world.OwnerToken);

        Assert.Equal(["Heat"], viewer.Select(t => t.Name));
        Assert.Equal(["Heat", ForbiddenName], owner.Select(t => t.Name));
    }

    [Fact]
    public async Task Titles_NeverIncludeHidden()
    {
        await using var world = await World.CreateAsync();
        world.Fixture.Owner.SetHidden(TargetKind.Title, world.Heat, hidden: true);

        // The owner asking for hidden titles gets none either: the remote UI has no way to show them.
        var owner = await world.GetAsync<List<TitleCard>>("/api/v1/titles?kind=movie&includeHidden=true", world.OwnerToken);
        var viewer = await world.GetAsync<List<TitleCard>>("/api/v1/titles?kind=movie&includeHidden=true");

        Assert.Equal([ForbiddenName], owner.Select(t => t.Name));
        Assert.Empty(viewer);
    }

    [Fact]
    public async Task Search_PastItsShareOfTmdb_SaysBusy()
    {
        await using var world = await World.CreateAsync();

        for (var i = 0; i < 120; i++) Assert.Equal(HttpStatusCode.OK, (await world.SendAsync("GET", "/api/v1/search?q=heat")).Status);

        var (status, body) = await world.SendAsync("GET", "/api/v1/search?q=heat");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(ApiErrorCode.Busy, JsonSerializer.Deserialize<ApiErrorBody>(body, ApiJson.Options)!.Code);
    }

    [Fact]
    public async Task OwnerDevice_CannotReachAHiddenTitle_ByItsId()
    {
        await using var world = await World.CreateAsync();
        world.Fixture.Owner.SetHidden(TargetKind.Title, world.Heat, hidden: true);

        var (status, _) = await world.SendAsync("GET", $"/api/v1/titles/{world.Heat}", world.OwnerToken);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Theory]
    [InlineData("?kind=anime")]
    [InlineData("")]
    [InlineData("?kind=movie&sort=random")]
    [InlineData("?kind=7")]
    public async Task Titles_BadQuery_IsBadRequest(string query)
    {
        await using var world = await World.CreateAsync();

        var (status, body) = await world.SendAsync("GET", "/api/v1/titles" + query);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("badRequest", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Title_Forbidden_Is404_SameBodyAsUnknown()
    {
        await using var world = await World.CreateAsync();

        var forbidden = await world.SendAsync("GET", $"/api/v1/titles/{world.Ronin}");
        var unknown = await world.SendAsync("GET", "/api/v1/titles/99999");
        var forbiddenNext = await world.SendAsync("GET", $"/api/v1/titles/{world.Ronin}/next");
        var unknownNext = await world.SendAsync("GET", "/api/v1/titles/99999/next");

        Assert.Equal(HttpStatusCode.NotFound, forbidden.Status);
        Assert.Equal(unknown, forbidden);
        Assert.Equal(HttpStatusCode.NotFound, forbiddenNext.Status);
        Assert.Equal(unknownNext, forbiddenNext);
        Assert.Equal(ApiErrorCode.NotFound, JsonSerializer.Deserialize<ApiErrorBody>(forbidden.Body, ApiJson.Options)!.Code);
    }

    [Fact]
    public async Task Title_NoRelPathDirectories()
    {
        await using var world = await World.CreateAsync();

        var (status, body) = await world.SendAsync("GET", $"/api/v1/titles/{world.Heat}");

        Assert.Equal(HttpStatusCode.OK, status);
        var title = JsonSerializer.Deserialize<TitleDetailsView>(body, ApiJson.Options)!;
        var file = Assert.Single(title.Files);
        Assert.Equal("Heat (1995).mkv", file.RelPath);
        Assert.DoesNotContain('\\', file.RelPath);
        Assert.DoesNotContain('/', file.RelPath);
        // Neither the subfolder nor where the library sits on the owner's disk.
        Assert.DoesNotContain("Sub", body, StringComparison.Ordinal);
        Assert.DoesNotContain("mediaboard-test-", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recent_FollowsViewerFolders()
    {
        await using var world = await World.CreateAsync();

        var viewer = await world.GetAsync<List<TitleCard>>("/api/v1/recent?limit=10");
        var owner = await world.GetAsync<List<TitleCard>>("/api/v1/recent", world.OwnerToken);

        Assert.Equal(["Heat"], viewer.Select(t => t.Name));
        Assert.Equal(2, owner.Count);
    }

    [Fact]
    public async Task Continue_IsPerAccount()
    {
        await using var world = await World.CreateAsync();
        var anna = world.Fixture.NewPerson("Anna");
        var annaToken = world.Fixture.Devices.Issue(anna.Id, "Phone", "198.51.100.1").Token;
        world.Fixture.Progress.Save(world.Sam.Id, world.HeatFile, 60, 2700);

        var sams = await world.GetAsync<List<ContinueItem>>("/api/v1/continue");
        var annas = await world.GetAsync<List<ContinueItem>>("/api/v1/continue", annaToken);

        var item = Assert.Single(sams);
        Assert.Equal(world.Heat, item.TitleId);
        Assert.Empty(annas);
    }

    [Fact]
    public async Task Dismiss_IsPerAccount()
    {
        await using var world = await World.CreateAsync();
        var anna = world.Fixture.NewPerson("Anna");
        var annaToken = world.Fixture.Devices.Issue(anna.Id, "Phone", "198.51.100.1").Token;
        world.Fixture.Progress.Save(world.Sam.Id, world.HeatFile, 60, 2700);
        world.Fixture.Progress.Save(anna.Id, world.HeatFile, 90, 2700);

        var (status, _) = await world.SendAsync("POST", $"/api/v1/continue/{world.Heat}/dismiss");

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Empty(await world.GetAsync<List<ContinueItem>>("/api/v1/continue"));
        Assert.Single(await world.GetAsync<List<ContinueItem>>("/api/v1/continue", annaToken));
    }

    [Fact]
    public async Task Dismiss_ForbiddenTitle_Is404_SameBodyAsUnknown_AndChangesNothing()
    {
        await using var world = await World.CreateAsync();
        var roninFile = new FileStore(world.Fixture.Db).ByFolder(world.Closed.Id).Single().Id;
        world.Fixture.Progress.Save(LibraryDatabase.OwnerId, roninFile, 60, 2700);
        world.Fixture.Progress.Save(world.Sam.Id, roninFile, 60, 2700);

        var forbidden = await world.SendAsync("POST", $"/api/v1/continue/{world.Ronin}/dismiss");
        var unknown = await world.SendAsync("POST", "/api/v1/continue/99999/dismiss");

        Assert.Equal(HttpStatusCode.NotFound, forbidden.Status);
        Assert.Equal(unknown, forbidden);
        // The dismissal would have been recorded for the viewer if the handler had gone ahead.
        Assert.Single(await world.GetAsync<List<ContinueItem>>("/api/v1/continue", world.OwnerToken));
        using var connection = world.Fixture.Db.Open();
        Assert.Equal(0, Dapper.SqlMapper.ExecuteScalar<int>(connection, "SELECT COUNT(*) FROM continue_dismissed"));
    }

    [Fact]
    public async Task Next_NamesTheFileToPlay()
    {
        await using var world = await World.CreateAsync();

        var next = await world.GetAsync<NextReply>($"/api/v1/titles/{world.Heat}/next");

        Assert.Equal(world.HeatFile, next.FileId);
    }

    [Fact]
    public async Task Search_UsesOwnersKey_NotExposed()
    {
        await using var world = await World.CreateAsync();
        world.Fixture.Provider.Hits.Add(new SearchCandidate(101, TitleKind.Movie, "Heat", null, 1995, 50, 8.2, "/heat.jpg", "A thief.", 10));

        var (status, body) = await world.SendAsync("GET", "/api/v1/search?q=heat"); 

        // The viewer has no TMDB key of their own: the owner's provider answers, and nothing of it is in the reply.
        Assert.Equal(HttpStatusCode.OK, status);
        var results = JsonSerializer.Deserialize<SearchResults>(body, ApiJson.Options)!;
        Assert.Equal(OnlineState.Ok, results.State);
        Assert.Equal("Heat", Assert.Single(results.Online).Candidate.Name);
        Assert.DoesNotContain("api_key", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apikey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mediaboard-test-", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_ForbiddenTitle_HasNoLibraryTitleId()
    {
        await using var world = await World.CreateAsync();
        world.Fixture.Provider.Hits.Add(new SearchCandidate(
            ForbiddenTmdbId, TitleKind.Movie, ForbiddenName, null, 1998, 40, 7.5, "/ronin.jpg", "A job.", 10));

        var viewer = await world.GetAsync<SearchResults>($"/api/v1/search?q={ForbiddenName}");
        var owner = await world.GetAsync<SearchResults>($"/api/v1/search?q={ForbiddenName}", world.OwnerToken);
        var local = await world.GetAsync<List<TitleCard>>($"/api/v1/search/local?q={ForbiddenName}");

        Assert.Null(Assert.Single(viewer.Online).LibraryTitleId);
        Assert.Empty(viewer.Library);
        Assert.Equal(world.Ronin, Assert.Single(owner.Online).LibraryTitleId);
        Assert.Empty(local);
    }

    [Fact]
    public async Task SearchLocal_FindsTitlesTheViewerMayReach()
    {
        await using var world = await World.CreateAsync();

        var found = await world.GetAsync<List<TitleCard>>("/api/v1/search/local?q=heat&limit=5");

        Assert.Equal(["Heat"], found.Select(t => t.Name));
    }

    [Fact]
    public async Task Online_Offline_AnswersSeeState()
    {
        await using var world = await World.CreateAsync();
        world.Fixture.Connectivity.ReportOffline();

        var results = await world.GetAsync<SearchResults>("/api/v1/search?q=heat");

        Assert.Equal(OnlineState.Offline, results.State);
        Assert.Empty(results.Online);
        Assert.Equal(["Heat"], results.Library.Select(t => t.Name));
    }

    [Fact]
    public async Task Online_TitleAndSeason_AreForwarded()
    {
        await using var world = await World.CreateAsync();
        world.Fixture.Provider.Titles[(55, TitleKind.Show)] = new TitleDetails(55, TitleKind.Show, "Skippy", 2015, null, "About.", 8.0, "/s.jpg", 45, [1]);
        world.Fixture.Provider.Seasons[(55, 1)] = new SeasonDetails(1, [new EpisodeDetails(1, 1, "Pilot", 45, null)]);

        var title = await world.GetAsync<TitleDetails>("/api/v1/online/tv/55");
        var season = await world.GetAsync<SeasonDetails>("/api/v1/online/tv/55/seasons/1");
        var missing = await world.SendAsync("GET", "/api/v1/online/movie/55");
        var badKind = await world.SendAsync("GET", "/api/v1/online/anime/55");

        Assert.Equal("Skippy", title.Name);
        Assert.Equal("Pilot", Assert.Single(season.Episodes).Name);
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Equal(HttpStatusCode.NotFound, badKind.Status);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("%2e%2e")]
    [InlineData("..%2f..%2fwindows%2fwin.ini")]
    [InlineData("%2e%2e%5cwin.ini")]
    [InlineData("%2fabc.jpg")]
    [InlineData("C%3A%5Cwindows%5Cwin.ini")]
    [InlineData("abc.gif")]
    [InlineData("abc")]
    [InlineData("a%20b.jpg")]
    [InlineData("abc.jpg%0A")]
    public async Task Images_RejectsTraversal(string path)
    {
        await using var world = await World.CreateAsync();
        Directory.CreateDirectory(world.Fixture.ImagesDir);
        await File.WriteAllBytesAsync(Path.Combine(world.Fixture.ImagesDir, "w342abc.jpg"), [1, 2, 3], Ct);

        var (status, body) = await world.SendAsync("GET", "/api/v1/images/342/" + path);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("notFound", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(-342)]
    public async Task Images_OnlyTheCachedWidths(int width)
    {
        await using var world = await World.CreateAsync();

        var (status, _) = await world.SendAsync("GET", $"/api/v1/images/{width}/abc.jpg");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Theory]
    [InlineData(342, "abc.jpg", "image/jpeg")]
    [InlineData(780, "Abc123.png", "image/png")]
    [InlineData(300, "still.jpg", "image/jpeg")]
    public async Task Images_ServesCachedFile(int width, string name, string contentType)
    {
        await using var world = await World.CreateAsync();
        Directory.CreateDirectory(world.Fixture.ImagesDir);
        byte[] bytes = [9, 8, 7, 6];
        await File.WriteAllBytesAsync(Path.Combine(world.Fixture.ImagesDir, $"w{width}{name}"), bytes, Ct);

        var response = await world.Fixture.Client.SendAsync(world.Fixture.Request("GET", $"/api/v1/images/{width}/{name}", world.SamToken), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Images_NotCachedAndNotFetchable_Is404()
    {
        await using var world = await World.CreateAsync();

        var (status, _) = await world.SendAsync("GET", "/api/v1/images/342/never.jpg");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task Images_NeedAToken()
    {
        await using var world = await World.CreateAsync();
        Directory.CreateDirectory(world.Fixture.ImagesDir);
        await File.WriteAllBytesAsync(Path.Combine(world.Fixture.ImagesDir, "w342poster.jpg"), [1], Ct);

        var response = await world.Fixture.Client.GetAsync("/api/v1/images/342/poster.jpg", Ct);

        // No token, no image.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCode.Unauthorized, await ServerFixture.CodeOf(response));
    }
}
