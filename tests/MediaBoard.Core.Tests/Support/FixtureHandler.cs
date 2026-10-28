using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaBoard.Core.Tests.Support;

/// <summary>
/// A fake <see cref="HttpMessageHandler"/> that answers from files in <c>Fixtures/tmdb/</c>.
///
/// File naming (see <see cref="FileNameFor"/>): the URL path with '/' turned into '_' and the leading one
/// dropped, then "__", then the query as sorted "key=value" pairs joined by '&amp;', then ".json".
/// The <c>api_key</c> parameter is removed first, so a fixture never depends on the key (and the key never
/// appears in a file name). Characters other than letters, digits, '.', '-', '_' and ' ' in values become '_'.
/// <code>
/// GET /3/search/movie?query=Dune&amp;year=2021&amp;language=en-US&amp;api_key=SECRET
///   -> 3_search_movie__language=en-US&amp;query=Dune&amp;year=2021.json
/// GET /3/tv/1399/season/1?language=en-US
///   -> 3_tv_1399_season_1__language=en-US.json
/// </code>
/// A request with no fixture throws <see cref="MissingFixtureException"/> naming the file it expected.
/// Every request is kept in <see cref="Requests"/> for assertions (URL as sent, auth header).
/// </summary>
public sealed class FixtureHandler : HttpMessageHandler
{
    private readonly string _directory;

    public FixtureHandler(string? directory = null) =>
        _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "tmdb");

    public List<RecordedRequest> Requests { get; } = [];

    public static string FileNameFor(Uri uri)
    {
        var path = uri.AbsolutePath.Trim('/').Replace('/', '_');
        var pairs = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var eq = pair.IndexOf('=');
                var name = Decode(eq < 0 ? pair : pair[..eq]);
                var value = eq < 0 ? "" : Decode(pair[(eq + 1)..]);
                return (name, value);
            })
            .Where(p => !p.name.Equals("api_key", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.name, StringComparer.Ordinal)
            .Select(p => $"{Sanitize(p.name)}={Sanitize(p.value)}");

        return $"{path}__{string.Join('&', pairs)}.json";
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add(new RecordedRequest(uri, request.Headers.Authorization?.ToString()));

        var fileName = FileNameFor(uri);
        var fullPath = Path.Combine(_directory, fileName);
        if (!File.Exists(fullPath))
            throw new MissingFixtureException(
                $"No fixture for {request.Method} {uri.AbsolutePath}. Expected file: Fixtures/tmdb/{fileName}");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(File.ReadAllText(fullPath), Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }

    private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    private static string Sanitize(string s) => Regex.Replace(s, @"[^A-Za-z0-9._ -]", "_");

    public sealed record RecordedRequest(Uri Uri, string? Authorization);

    public sealed class MissingFixtureException(string message) : Exception(message);
}
