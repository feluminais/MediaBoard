using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace MediaBoard.Core.Remote;

public sealed class ApiException(ApiErrorCode code) : Exception($"The server answered {code}.")
{
    public ApiErrorCode Code { get; } = code;
}

public sealed class ServerUnreachableException(Exception? innerException = null) : Exception("The server can't be reached.", innerException);

public sealed class IdentityChangedException() : Exception("The server's certificate is not the pinned one.");

/// <summary>
/// Talks to one server over a connection that trusts its pinned certificate and nothing else. Every failure of a request
/// is also reported, so what shows the connection's state doesn't depend on which caller met it.
/// </summary>
/// <param name="endpoint">The server.</param>
/// <param name="token">The device token, asked for per request, or null for none.</param>
/// <param name="report">Told what requests learn about the connection, on whichever thread made them.</param>
/// <param name="time">The clock, or null for the real one.</param>
public sealed class ApiClient(ServerEndpoint endpoint, Func<string?> token, Action<ConnectionEvent> report, TimeProvider? time = null) : IDisposable
{
    private const string EventField = "event:";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    // Well inside the request's 10 s, so a home address that drops packets while away leaves time for the public one.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    // Two keep-alives missed. A PC that loses power or sleeps sends no goodbye, so without this the connection looks open for good.
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(45);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly HttpClient _http = NewHttp(Convert.FromHexString(endpoint.Fingerprint));

    // A malformed address can only come from a bad invite, and the others may still work.
    private readonly List<string> _roots =
        [.. endpoint.Addresses.Where(address => Uri.TryCreate($"https://{address}", UriKind.Absolute, out _)).Select(address => $"https://{address}/api/v1/")];

    private string? _working;
    private int _listening;

    /// <summary>Blocks the caller. Safe on the UI thread, where a wait on an async call could deadlock.</summary>
    /// <exception cref="ApiException">The server refused.</exception>
    /// <exception cref="ServerUnreachableException">No address answered in time.</exception>
    /// <exception cref="IdentityChangedException">An address answered with another certificate and none with the pinned one.</exception>
    public T Get<T>(string route)
    {
        using var response = Exchange(root => Request(HttpMethod.Get, root + route));
        return Read<T>(response);
    }

    /// <inheritdoc cref="Get{T}"/>
    public void Send(HttpMethod method, string route, object? body = null) =>
        Exchange(root => Request(method, root + route, body)).Dispose();

    /// <inheritdoc cref="Get{T}"/>
    public async Task<T> GetAsync<T>(string route, CancellationToken ct)
    {
        using var response = await ExchangeAsync(root => Request(HttpMethod.Get, root + route), HttpCompletionOption.ResponseContentRead, blocking: false, ct);
        return Read<T>(response);
    }

    /// <inheritdoc cref="Get{T}"/>
    public async Task SendAsync(HttpMethod method, string route, object? body, CancellationToken ct)
    {
        using var response = await ExchangeAsync(root => Request(method, root + route, body), HttpCompletionOption.ResponseContentRead, blocking: false, ct);
    }

    /// <summary>
    /// Waits for the headers only, so the body runs as long as the caller reads it. A body cut mid-way fails that read
    /// and carries no code; asking again gets the code, if there is one.
    /// </summary>
    /// <param name="fileId">The file.</param>
    /// <param name="range">The player's Range header. One that makes no sense is left out, as a server would ignore it.</param>
    /// <param name="ct">Stops the wait for the headers.</param>
    /// <returns>The caller disposes it. A range past the end comes back as 416, which is for the player to read.</returns>
    /// <inheritdoc cref="Get{T}"/>
    public Task<HttpResponseMessage> OpenStreamAsync(int fileId, string? range, CancellationToken ct)
    {
        return ExchangeAsync(
            root =>
            {
                var request = Request(HttpMethod.Get, FormattableString.Invariant($"{root}files/{fileId}/stream"));
                if (RangeHeaderValue.TryParse(range, out var parsed)) request.Headers.Range = parsed;
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            blocking: false,
            ct);
    }

    /// <summary>
    /// The server's events by name, for as long as the connection lasts. The sequence ending means the connection
    /// dropped. <see cref="ConnectionEvent.Listening"/> is reported once the server has taken it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// One is already open. The server ends a device's older connection when it opens a new one, so two would go on
    /// ending each other.
    /// </exception>
    /// <inheritdoc cref="Get{T}"/>
    public async IAsyncEnumerable<string> EventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _listening, 1) == 1) throw new InvalidOperationException("Events are already open.");

        try
        {
            using var response = await ExchangeAsync(root => Request(HttpMethod.Get, root + "events"), HttpCompletionOption.ResponseHeadersRead, blocking: false, ct);
            using var quiet = new CancellationTokenSource(SilenceLimit, _time);
            using var read = CancellationTokenSource.CreateLinkedTokenSource(ct, quiet.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            report(ConnectionEvent.Listening);

            string? name = null;
            while (await NextLineAsync(reader, read.Token, ct) is { } line)
            {
                quiet.CancelAfter(SilenceLimit);
                if (line.Length == 0)
                {
                    if (name is not null) yield return name;
                    name = null;
                }
                else if (line.StartsWith(EventField, StringComparison.Ordinal))
                {
                    name = line[EventField.Length..].Trim();
                }
            }
        }
        finally
        {
            Volatile.Write(ref _listening, 0);
        }
    }

    public void Dispose() => _http.Dispose();

    private static HttpClient NewHttp(byte[] pin)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                // The pin is the whole check: a self-signed certificate fails the chain, and an address never matches its name.
                // Thrown rather than returned, because a plain refusal looks the same as any other failed handshake.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pin)
                        ? true
                        : throw new IdentityChangedException(),
            },
        };

        // ExchangeAsync times the request instead, because one limit has to cover every address.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The next line, or null if the connection ended, broke or went quiet.</summary>
    private static async Task<string?> NextLineAsync(StreamReader reader, CancellationToken read, CancellationToken ct)
    {
        try
        {
            return await reader.ReadLineAsync(read);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            return null;
        }
    }

    private static bool NeverReachedTheServer(Exception failure) =>
        failure is OperationCanceledException or HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError };

    private static bool IsWrongIdentity(Exception? failure)
    {
        for (; failure is not null; failure = failure.InnerException)
        {
            if (failure is IdentityChangedException) return true;
        }

        return false;
    }

    private static ApiErrorCode? CodeIn(HttpResponseMessage response)
    {
        try
        {
            return JsonSerializer.Deserialize<ApiErrorBody>(response.Content.ReadAsStream(), ApiJson.Options)?.Code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(url));
        if (token() is { } bearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body, options: ApiJson.Options);
        return request;
    }

    // With blocking set the async method below never suspends, so this wait returns at once and nothing is posted
    // back to the caller's thread.
    private HttpResponseMessage Exchange(Func<string, HttpRequestMessage> build) =>
        ExchangeAsync(build, HttpCompletionOption.ResponseContentRead, blocking: true, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Tries the addresses in turn under one timeout. It moves on only from a failure to get through, so a request the
    /// server may have acted on is never sent twice.
    /// </summary>
    private async Task<HttpResponseMessage> ExchangeAsync(Func<string, HttpRequestMessage> build, HttpCompletionOption completion, bool blocking, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, _time);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        Exception? failure = null;
        var wrongIdentity = false;

        foreach (var root in InTryOrder())
        {
            using var request = build(root);
            try
            {
                var response = blocking ? _http.Send(request, completion, limit.Token) : await _http.SendAsync(request, completion, limit.Token);
                Volatile.Write(ref _working, root);
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return response;

                using (response)
                {
                    if (!blocking) await response.Content.LoadIntoBufferAsync(limit.Token);
                    throw Refusal(response);
                }
            }
            catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                failure = e;
                wrongIdentity |= IsWrongIdentity(e);
                if (timeout.IsCancellationRequested || !NeverReachedTheServer(e)) break;
            }
        }

        if (wrongIdentity)
        {
            report(ConnectionEvent.IdentityChanged);
            throw new IdentityChangedException();
        }

        report(ConnectionEvent.Unreachable);
        throw new ServerUnreachableException(failure);
    }

    private IEnumerable<string> InTryOrder()
    {
        var working = Volatile.Read(ref _working);
        return _roots.OrderBy(root => root != working);
    }

    /// <summary>What a buffered error answer means.</summary>
    private Exception Refusal(HttpResponseMessage response)
    {
        var code = CodeIn(response);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            report(ConnectionEvent.SignedOut);
            return new ApiException(code ?? ApiErrorCode.Unauthorized);
        }

        if (code is { } known) return new ApiException(known);

        // With no code this didn't come from the API, and a person can do nothing with it but try again later.
        report(ConnectionEvent.Unreachable);
        return new ServerUnreachableException();
    }

    private T Read<T>(HttpResponseMessage response)
    {
        try
        {
            if (JsonSerializer.Deserialize<T>(response.Content.ReadAsStream(), ApiJson.Options) is { } value) return value;
        }
        catch (JsonException)
        {
            // Reported below, with the null.
        }

        report(ConnectionEvent.Unreachable);
        throw new ServerUnreachableException();
    }
}
