using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MediaBoard.Core.Tests.Support;

namespace MediaBoard.Core.Tests.Metadata;

/// <summary>Record mode of <see cref="FixtureHandler"/>, against a fake inner handler (no network).</summary>
public sealed class FixtureHandlerRecordTests : IDisposable
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string Body = """{"page":1,"results":[{"id":438631,"title":"Dune"}]}""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-record-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private (HttpClient Client, FixtureHandler Handler, List<HttpRequestMessage> Seen) Record(
        Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var seen = new List<HttpRequestMessage>();
        var inner = new StubHandler((_, request) =>
        {
            seen.Add(request);
            return answer(request);
        });
        var handler = new FixtureHandler(_dir, inner);
        return (new HttpClient(handler), handler, seen);
    }

    private string[] Files => Directory.Exists(_dir) ? Directory.GetFiles(_dir) : [];

    [Fact]
    public async Task Success_IsWrittenUnderTheNameWithoutTheKey_AndReturned()
    {
        var (client, handler, seen) = Record(_ => Json(Body));

        var text = await client.GetStringAsync(
            $"https://api.themoviedb.org/3/search/movie?query=Dune&year=2021&language=en-US&api_key={Key}", Ct);

        Assert.Equal(Body, text);
        var file = Assert.Single(Files);
        Assert.Equal("3_search_movie__language=en-US&query=Dune&year=2021.json", Path.GetFileName(file));
        Assert.Equal(Body, File.ReadAllText(file));
        Assert.DoesNotContain(Key, file);
        Assert.DoesNotContain(Key, File.ReadAllText(file));
        Assert.Equal([Path.GetFileName(file)], handler.Recorded);
        Assert.True(handler.IsRecording);

        // The real request still carried the key.
        Assert.Contains($"api_key={Key}", Assert.Single(seen).RequestUri!.Query);
    }

    [Fact]
    public async Task BearerHeader_IsForwardedButNeverPersisted()
    {
        var (client, _, seen) = Record(_ => Json(Body));
        const string token = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiJ0ZXN0In0.c2lnbmF0dXJl";
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/tv/1399?language=en-US");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(token, Assert.Single(seen).Headers.Authorization!.Parameter);
        var file = Assert.Single(Files);
        Assert.Equal("3_tv_1399__language=en-US.json", Path.GetFileName(file));
        Assert.DoesNotContain(token, File.ReadAllText(file));
    }

    [Fact]
    public async Task FileMadeInRecordMode_IsFoundInReplayMode()
    {
        var (client, _, _) = Record(_ => Json(Body));
        await client.GetStringAsync($"https://api.themoviedb.org/3/search/movie?query=Dune&api_key={Key}", Ct);

        using var replay = new HttpClient(new FixtureHandler(_dir));
        var text = await replay.GetStringAsync("https://api.themoviedb.org/3/search/movie?api_key=other&query=Dune", Ct);

        Assert.Equal(Body, text);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NonSuccess_IsPassedThroughAndNotSaved(HttpStatusCode status)
    {
        var (client, handler, _) = Record(_ => Json("""{"status_message":"nope"}""", status));

        using var response = await client.GetAsync($"https://api.themoviedb.org/3/movie/1?api_key={Key}", Ct);

        Assert.Equal(status, response.StatusCode);
        Assert.Empty(Files);
        Assert.Empty(handler.Recorded);
    }

    [Fact]
    public async Task BodyThatEchoesTheKey_IsRefusedAndNotSaved()
    {
        var (client, _, _) = Record(_ => Json($$"""{"debug":"{{Key}}"}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetStringAsync($"https://api.themoviedb.org/3/movie/1?api_key={Key}", Ct));

        Assert.DoesNotContain(Key, error.Message);
        Assert.Empty(Files);
    }

    [Fact]
    public async Task Replay_MissingFixture_NamesTheFileAndIsListed()
    {
        using var handler = new FixtureHandler(_dir);
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<FixtureHandler.MissingFixtureException>(
            () => client.GetStringAsync($"https://api.themoviedb.org/3/movie/7?api_key={Key}&language=en-US", Ct));

        Assert.Contains("Fixtures/tmdb/3_movie_7__language=en-US.json", error.Message);
        Assert.DoesNotContain(Key, error.Message);
        Assert.Equal(["3_movie_7__language=en-US.json"], handler.MissingFixtures);
        Assert.False(handler.IsRecording);
        Assert.Empty(Files);
    }
}
