using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Metadata.Tmdb;
using MediaBoard.Core.Tests.Support;

namespace MediaBoard.Core.Tests.Metadata;

public class TmdbClientTests
{
    private const string V3Key = "0123456789abcdef0123456789abcdef";
    private const string BearerKey = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiJ0ZXN0In0.c2lnbmF0dXJl";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed record Rig(TmdbClient Client, ActivityHub Hub, FakeClock Clock);

    private static Rig Build(HttpMessageHandler handler, string? key = V3Key)
    {
        var hub = new ActivityHub();
        var clock = new FakeClock();
        var client = new TmdbClient(new HttpClient(handler), () => key, hub, clock);
        return new Rig(client, hub, clock);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<MetadataUnavailableException> Unavailable(Func<Task> call) =>
        await Assert.ThrowsAsync<MetadataUnavailableException>(call);

    // --- mapping ---------------------------------------------------------------------------------

    [Fact]
    public async Task Search_MapsCandidates()
    {
        var rig = Build(new FixtureHandler());

        var results = await rig.Client.SearchAsync("Dune", 2021, TitleKind.Movie, Ct);

        Assert.Equal(2, results.Count);
        Assert.Equal(
            new SearchCandidate(438631, TitleKind.Movie, "Dune", "Dune", 2021, 87.123, 7.8,
                "/d5NXSklXo0qyIYkgV94XAgMIckC.jpg",
                "Paul Atreides, a brilliant and gifted young man born into a great destiny, must travel to the most dangerous planet in the universe."),
            results[0]);

        // Empty release date, no poster, no votes: all become null rather than "" or 0.
        var sparse = results[1];
        Assert.Null(sparse.Year);
        Assert.Null(sparse.Rating);
        Assert.Null(sparse.PosterPath);
        Assert.Null(sparse.Overview);
    }

    [Fact]
    public async Task Search_Show_UsesTvEndpointAndFirstAirDateYear()
    {
        var handler = new FixtureHandler();
        var rig = Build(handler);

        var results = await rig.Client.SearchAsync("Game of Thrones", 2011, TitleKind.Show, Ct);

        var show = Assert.Single(results);
        Assert.Equal((1399, TitleKind.Show, "Game of Thrones", 2011), (show.TmdbId, show.Kind, show.Name, show.Year));
        Assert.Equal("/3/search/tv", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task SearchAny_UsesMultiEndpoint_AndDropsPeople()
    {
        var handler = new FixtureHandler();
        var rig = Build(handler);

        var results = await rig.Client.SearchAnyAsync("ford", Ct);

        Assert.Equal([(603, TitleKind.Movie, "Ford v Ferrari"), (1399, TitleKind.Show, "Fordland")],
            results.Select(r => (r.TmdbId, r.Kind, r.Name)));
        Assert.Equal("Le Mans '66", results[0].OriginalName);
        Assert.Equal("/3/search/multi", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task FindByImdbId_ReturnsMovie_OrNullWhenNothingMatches()
    {
        var rig = Build(new FixtureHandler());

        Assert.Equal((603, TitleKind.Movie), await rig.Client.FindByImdbIdAsync("tt0133093", Ct));
        Assert.Null(await rig.Client.FindByImdbIdAsync("tt0000000", Ct));
    }

    [Fact]
    public async Task GetTitle_Movie_MapsRuntimeAndYear()
    {
        var rig = Build(new FixtureHandler());

        var title = await rig.Client.GetTitleAsync(438631, TitleKind.Movie, Ct);

        Assert.Equal(TitleKind.Movie, title.Kind);
        Assert.Equal("Dune", title.Name);
        Assert.Equal(2021, title.Year);
        Assert.Null(title.EndYear);
        Assert.Equal(155, title.RuntimeMin);
        Assert.Equal(7.8, title.Rating);
        Assert.Empty(title.SeasonNumbers);
    }

    [Fact]
    public async Task GetTitle_EndedShow_HasEndYearFirstRuntimeAndSeasons()
    {
        var rig = Build(new FixtureHandler());

        var title = await rig.Client.GetTitleAsync(1399, TitleKind.Show, Ct);

        Assert.Equal(TitleKind.Show, title.Kind);
        Assert.Equal((2011, 2019), (title.Year, title.EndYear));
        Assert.Equal(60, title.RuntimeMin);
        Assert.Equal([0, 1, 2], title.SeasonNumbers);
    }

    [Fact]
    public async Task GetTitle_ReturningShow_HasNoEndYearRuntimeOrRating()
    {
        var rig = Build(new FixtureHandler());

        var title = await rig.Client.GetTitleAsync(95396, TitleKind.Show, Ct);

        Assert.Equal(2022, title.Year);
        Assert.Null(title.EndYear);
        Assert.Null(title.RuntimeMin);
        Assert.Null(title.Rating);
    }

    [Fact]
    public async Task GetSeason_MapsEpisodes()
    {
        var rig = Build(new FixtureHandler());

        var season = await rig.Client.GetSeasonAsync(1399, 1, Ct);

        Assert.Equal(1, season.Season);
        Assert.Equal(
        [
            new EpisodeDetails(1, 1, "Winter Is Coming", 62, new DateOnly(2011, 4, 17)),
            new EpisodeDetails(1, 2, "The Kingsroad", 56, new DateOnly(2011, 4, 24)),
            new EpisodeDetails(1, 3, "Lord Snow", null, null),
        ], season.Episodes);
    }

    // --- request shape ---------------------------------------------------------------------------

    [Fact]
    public async Task BearerVsApiKey_ChosenByShape()
    {
        var plain = new FixtureHandler();
        await Build(plain, V3Key).Client.SearchAsync("Dune", 2021, TitleKind.Movie, Ct);

        var request = Assert.Single(plain.Requests);
        Assert.Null(request.Authorization);
        Assert.Contains($"api_key={V3Key}", request.Uri.Query);

        var jwt = new FixtureHandler();
        await Build(jwt, BearerKey).Client.SearchAsync("Dune", 2021, TitleKind.Movie, Ct);

        request = Assert.Single(jwt.Requests);
        Assert.Equal($"Bearer {BearerKey}", request.Authorization);
        Assert.DoesNotContain("api_key", request.Uri.Query);
        Assert.DoesNotContain(BearerKey, request.Uri.ToString());
    }

    [Fact]
    public async Task EveryRequest_AsksForEnglish()
    {
        var handler = new FixtureHandler();
        var rig = Build(handler);

        await rig.Client.SearchAsync("Dune", 2021, TitleKind.Movie, Ct);
        await rig.Client.SearchAnyAsync("ford", Ct);
        await rig.Client.FindByImdbIdAsync("tt0133093", Ct);
        await rig.Client.GetTitleAsync(438631, TitleKind.Movie, Ct);
        await rig.Client.GetTitleAsync(1399, TitleKind.Show, Ct);
        await rig.Client.GetSeasonAsync(1399, 1, Ct);

        Assert.Equal(6, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Contains("language=en-US", r.Uri.Query));
    }

    [Fact]
    public void FixtureHandler_NamesFilesByPathAndSortedQuery_WithoutTheKey()
    {
        var uri = new Uri("https://api.themoviedb.org/3/search/movie?query=Dune%20Part&year=2021&language=en-US&api_key=SECRET");

        Assert.Equal("3_search_movie__language=en-US&query=Dune Part&year=2021.json", FixtureHandler.FileNameFor(uri));
    }

    [Fact]
    public async Task MissingFixture_FailsWithTheExpectedFileName()
    {
        var rig = Build(new FixtureHandler());

        var ex = await Assert.ThrowsAsync<FixtureHandler.MissingFixtureException>(
            () => rig.Client.GetTitleAsync(7, TitleKind.Movie, Ct));

        Assert.Contains("3_movie_7__language=en-US.json", ex.Message);
        Assert.DoesNotContain(V3Key, ex.Message);
    }

    // --- failures --------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoKey_ThrowsNoKey_WithoutHttpCall(string? key)
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);
        var rig = Build(handler, key);
        rig.Hub.SetKeyState(KeyState.Ok);

        var ex = await Unavailable(() => rig.Client.SearchAsync("Dune", null, TitleKind.Movie, Ct));

        Assert.Equal(UnavailableReason.NoKey, ex.Reason);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(KeyState.Missing, rig.Hub.Current.KeyState);
    }

    [Fact]
    public async Task Http401_ThrowsKeyRejected_AndSetsHubKeyState()
    {
        var handler = StubHandler.Status(HttpStatusCode.Unauthorized);
        var rig = Build(handler);

        var ex = await Unavailable(() => rig.Client.GetTitleAsync(603, TitleKind.Movie, Ct));

        Assert.Equal(UnavailableReason.KeyRejected, ex.Reason);
        Assert.Equal(KeyState.Rejected, rig.Hub.Current.KeyState);
        Assert.Equal(1, handler.Calls); // no retry
        Assert.Empty(rig.Clock.Delays);
        Assert.True(rig.Hub.Current.Online); // TMDB answered, so we are online
    }

    [Fact]
    public async Task Http429_RetriesAfterDelay()
    {
        var handler = new StubHandler((call, _) => call <= 2
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)) },
            }
            : Json("""{"results": [{"id": 1, "title": "Heat", "release_date": "1995-12-15"}]}"""));
        var rig = Build(handler);

        var results = await rig.Client.SearchAsync("Heat", null, TitleKind.Movie, Ct);

        Assert.Equal("Heat", Assert.Single(results).Name);
        Assert.Equal(3, handler.Calls);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], rig.Clock.Delays);
        Assert.Equal(KeyState.Ok, rig.Hub.Current.KeyState);
    }

    [Fact]
    public async Task Http429_WithoutRetryAfter_WaitsTwoSeconds()
    {
        var handler = new StubHandler((call, _) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : Json("""{"results": []}"""));
        var rig = Build(handler);

        await rig.Client.SearchAsync("Heat", null, TitleKind.Movie, Ct);

        Assert.Equal([TimeSpan.FromSeconds(2)], rig.Clock.Delays);
    }

    [Fact]
    public async Task Http429_WithHttpDate_WaitsUntilThatTime()
    {
        var rig = Build(new StubHandler((call, _) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(new FakeClock().GetUtcNow().AddSeconds(7)) },
            }
            : Json("""{"results": []}""")));

        await rig.Client.SearchAsync("Heat", null, TitleKind.Movie, Ct);

        Assert.Equal([TimeSpan.FromSeconds(7)], rig.Clock.Delays);
    }

    [Fact]
    public async Task Http429_GivesUpAfterThreeRetries_WithServerError()
    {
        var handler = StubHandler.Status(HttpStatusCode.TooManyRequests);
        var rig = Build(handler);

        var ex = await Unavailable(() => rig.Client.SearchAsync("Heat", null, TitleKind.Movie, Ct));

        Assert.Equal(UnavailableReason.ServerError, ex.Reason);
        Assert.Equal(4, handler.Calls); // first try plus three retries
        Assert.Equal(3, rig.Clock.Delays.Count);
    }

    [Fact]
    public async Task Http500_ThrowsServerError_WithoutRetry()
    {
        var handler = StubHandler.Status(HttpStatusCode.BadGateway);
        var rig = Build(handler);

        var ex = await Unavailable(() => rig.Client.SearchAsync("Heat", null, TitleKind.Movie, Ct));

        Assert.Equal(UnavailableReason.ServerError, ex.Reason);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GarbageBody_ThrowsServerError()
    {
        var rig = Build(new StubHandler((_, _) => Json("<html>oops</html>")));

        var ex = await Unavailable(() => rig.Client.SearchAsync("Heat", null, TitleKind.Movie, Ct));

        Assert.Equal(UnavailableReason.ServerError, ex.Reason);
    }

    [Fact]
    public async Task NetworkFailure_ThrowsOffline_AndSetsHubOffline()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("No such host is known."));
        var rig = Build(handler);
        rig.Hub.SetOnline(true);

        var ex = await Unavailable(() => rig.Client.SearchAsync("Heat", null, TitleKind.Movie, Ct));

        Assert.Equal(UnavailableReason.Offline, ex.Reason);
        Assert.False(rig.Hub.Current.Online);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Timeout_ThrowsOffline()
    {
        var rig = Build(new StubHandler((_, _) => throw new TaskCanceledException("The request timed out.")));
        rig.Hub.SetOnline(true);

        var ex = await Unavailable(() => rig.Client.SearchAsync("Heat", null, TitleKind.Movie, Ct));

        Assert.Equal(UnavailableReason.Offline, ex.Reason);
        Assert.False(rig.Hub.Current.Online);
    }

    [Fact]
    public async Task CancelledByCaller_PropagatesCancellation_AndLeavesStatusAlone()
    {
        var rig = Build(new FixtureHandler());
        rig.Hub.SetOnline(true);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Client.SearchAsync("Dune", 2021, TitleKind.Movie, cts.Token));

        Assert.True(rig.Hub.Current.Online);
    }

    [Fact]
    public async Task Success_SetsHubOnlineAndKeyOk()
    {
        var rig = Build(new FixtureHandler());
        rig.Hub.SetKeyState(KeyState.Rejected);

        await rig.Client.SearchAsync("Dune", 2021, TitleKind.Movie, Ct);

        Assert.True(rig.Hub.Current.Online);
        Assert.Equal(KeyState.Ok, rig.Hub.Current.KeyState);
    }

    [Fact]
    public async Task TheKey_NeverAppearsInLogsOrExceptionMessages()
    {
        var failures = new HttpMessageHandler[]
        {
            StubHandler.Status(HttpStatusCode.Unauthorized),
            StubHandler.Status(HttpStatusCode.InternalServerError),
            StubHandler.Status(HttpStatusCode.TooManyRequests),
            new StubHandler((_, request) => throw new HttpRequestException($"Failed: {request.RequestUri}")),
        };

        foreach (var handler in failures)
        {
            var rig = Build(handler, BearerKey + V3Key);
            var ex = await Unavailable(() => rig.Client.SearchAsync("Heat", null, TitleKind.Movie, Ct));

            Assert.DoesNotContain(V3Key, ex.ToString());
            Assert.All(rig.Hub.Recent, e => Assert.DoesNotContain(V3Key, e.Message + e.Hint));
            Assert.NotEmpty(rig.Hub.Recent);
        }
    }

    // --- pacing ----------------------------------------------------------------------------------

    [Fact]
    public async Task RequestQueue_AllowsTwentyPerSecond_ThenWaitsForTheWindow()
    {
        var rig = Build(new FixtureHandler());

        for (var i = 0; i < 20; i++) await rig.Client.SearchAsync("Dune", 2021, TitleKind.Movie, Ct);
        Assert.Empty(rig.Clock.Delays);

        await rig.Client.SearchAsync("Dune", 2021, TitleKind.Movie, Ct);

        Assert.Equal([TimeSpan.FromSeconds(1)], rig.Clock.Delays);
    }

    [Fact]
    public async Task RequestQueue_SendsOneRequestAtATime()
    {
        var inFlight = 0;
        var peak = 0;
        var handler = new StubHandler((_, _) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            peak = Math.Max(peak, now);
            Thread.Sleep(10);
            Interlocked.Decrement(ref inFlight);
            return Json("""{"results": []}""");
        });
        var rig = Build(handler);

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => rig.Client.SearchAsync("Heat", null, TitleKind.Movie, Ct))));

        Assert.Equal(8, handler.Calls);
        Assert.Equal(1, peak);
    }
}
