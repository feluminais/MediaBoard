using System.Net;
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
}
