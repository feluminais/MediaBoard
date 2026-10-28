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
/// A request with no fixture throws <see cref="MissingFixtureException"/> naming the file it expected (and is
/// kept in <see cref="MissingFixtures"/>, because code under test may swallow the exception).
/// Every request is kept in <see cref="Requests"/> for assertions (URL as sent, auth header).
///
/// Record mode (<see cref="FromEnvironment"/> with MEDIABOARD_RECORD=1 and MEDIABOARD_TMDB_KEY set, or the
/// constructor's <c>recordWith</c> argument): each request is forwarded to the inner handler, a 2xx body is
/// written to the fixture file and returned, and any other answer is passed through without being saved.
/// Only the response body is stored: the name has no <c>api_key</c>, a bearer header is never persisted, and
/// a body that contains the key is refused rather than written.
/// </summary>
public sealed class FixtureHandler : HttpMessageHandler
{
    public const string RecordVariable = "MEDIABOARD_RECORD";
    public const string KeyVariable = "MEDIABOARD_TMDB_KEY";

    private readonly string _directory;
    private readonly HttpMessageInvoker? _live;

    /// <param name="directory">Where fixtures are read from (and written to in record mode); default: the copy in bin.</param>
    /// <param name="recordWith">Turns on record mode: the handler that really sends the request. Owned (disposed) by this handler.</param>
    public FixtureHandler(string? directory = null, HttpMessageHandler? recordWith = null)
    {
        _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "tmdb");
        _live = recordWith is null ? null : new HttpMessageInvoker(recordWith, disposeHandler: true);
    }

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>File names (not paths) of requests that had no fixture.</summary>
    public List<string> MissingFixtures { get; } = [];

    /// <summary>Fixture files written by record mode.</summary>
    public List<string> Recorded { get; } = [];

    public bool IsRecording => _live is not null;

    /// <summary>True when MEDIABOARD_RECORD=1 and a TMDB key is set in MEDIABOARD_TMDB_KEY.</summary>
    public static bool RecordingRequested =>
        Environment.GetEnvironmentVariable(RecordVariable) == "1" && !string.IsNullOrWhiteSpace(EnvironmentKey);

    public static string? EnvironmentKey => Environment.GetEnvironmentVariable(KeyVariable)?.Trim();

    /// <summary>A recording handler writing into the source tree when recording is requested, otherwise a plain replay handler.</summary>
    public static FixtureHandler FromEnvironment() =>
        RecordingRequested ? new FixtureHandler(SourceTree.FixtureDirectory, new HttpClientHandler()) : new FixtureHandler();

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
        lock (Requests) Requests.Add(new RecordedRequest(uri, request.Headers.Authorization?.ToString()));

        var fileName = FileNameFor(uri);
        var fullPath = Path.Combine(_directory, fileName);
        if (_live is not null) return RecordAsync(request, fileName, fullPath, cancellationToken);

        if (!File.Exists(fullPath))
        {
            lock (MissingFixtures) MissingFixtures.Add(fileName);
            throw new MissingFixtureException(
                $"No fixture for {request.Method} {uri.AbsolutePath}. Expected file: Fixtures/tmdb/{fileName}");
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(File.ReadAllText(fullPath), Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _live?.Dispose();
        base.Dispose(disposing);
    }

    private async Task<HttpResponseMessage> RecordAsync(
        HttpRequestMessage request, string fileName, string fullPath, CancellationToken ct)
    {
        var response = await _live!.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return response;

        string body;
        using (response)
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (SecretsIn(request).Any(secret => body.Contains(secret, StringComparison.Ordinal)))
            throw new InvalidOperationException($"The response for {fileName} contains the API key; not saving it.");

        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(fullPath, body, new UTF8Encoding(false), ct).ConfigureAwait(false);
        lock (Recorded) Recorded.Add(fileName);

        return new HttpResponseMessage(response.StatusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
    }

    private static IEnumerable<string> SecretsIn(HttpRequestMessage request)
    {
        foreach (var pair in request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && Decode(pair[..eq]).Equals("api_key", StringComparison.OrdinalIgnoreCase))
            {
                var value = Decode(pair[(eq + 1)..]);
                if (value.Length > 0) yield return value;
            }
        }

        if (request.Headers.Authorization?.Parameter is { Length: > 0 } token) yield return token;
    }

    private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    private static string Sanitize(string s) => Regex.Replace(s, @"[^A-Za-z0-9._ -]", "_");

    public sealed record RecordedRequest(Uri Uri, string? Authorization);

    public sealed class MissingFixtureException(string message) : Exception(message);
}
