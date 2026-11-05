using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace MediaBoard.Core.Remote;

public sealed class ApiException(ApiErrorCode code) : Exception(PlainWords(code))
{
    public ApiErrorCode Code { get; } = code;

    // The activity list shows a message as it stands, so it has to read as words to a viewer.
    private static string PlainWords(ApiErrorCode code) => code switch
    {
        ApiErrorCode.NotFound => "It isn't on the server anymore.",
        ApiErrorCode.Unauthorized => "You've been signed out.",
        ApiErrorCode.InviteDead => "This invite no longer works.",
        ApiErrorCode.WrongPassword => "Wrong password.",
        ApiErrorCode.Locked => "Too many tries.",
        ApiErrorCode.Busy => "The server is busy.",
        ApiErrorCode.Disabled => "This account is disabled.",
        ApiErrorCode.PasswordTooShort => "That password is too short.",
        ApiErrorCode.FileMissing => "The file is missing.",
        ApiErrorCode.DriveUnavailable => "The drive with this file isn't available.",
        ApiErrorCode.StreamStopped => "The stream was stopped.",
        ApiErrorCode.TooManyStreams => "Too many streams on this account.",
        ApiErrorCode.TooLarge => "That's too much to send.",
        ApiErrorCode.VersionMismatch => "MediaBoard needs updating.",
        ApiErrorCode.Unreachable => "The server can't be reached.",
        _ => "The server turned this down.",
    };
}

public sealed class ServerUnreachableException(Exception? innerException = null) : Exception("The server can't be reached.", innerException);

public sealed class IdentityChangedException() : Exception("This server's identity changed.");

/// <summary>
/// Talks to one server over a connection that trusts its pinned certificate and nothing else. Whatever a request proves
/// about the connection is also reported, so what shows its state doesn't depend on which caller met it. A slow or broken
/// answer proves the server is there, so that goes only to the caller.
/// </summary>
/// <param name="endpoint">The server.</param>
/// <param name="token">The device token, asked for per request, or null for none.</param>
/// <param name="report">Told what requests learn about the connection, on whichever thread made them.</param>
/// <param name="time">The clock, or null for the real one.</param>
public sealed class ApiClient(ServerEndpoint endpoint, Func<string?> token, Action<ConnectionEvent> report, TimeProvider? time = null) : IDisposable
{
    private const string EventField = "event:";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    // The stream route opens the file before any header leaves, and a drive that has to spin up takes a while.
    private static readonly TimeSpan StreamHeadersTimeout = TimeSpan.FromSeconds(30);

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
    /// <exception cref="ServerUnreachableException">No address gave a usable answer in time.</exception>
    /// <exception cref="IdentityChangedException">An address showed another certificate and none passed the pin.</exception>
    public T Get<T>(string route)
    {
        using var response = Exchange(root => Request(HttpMethod.Get, root + route));
        return Read<T>(response);
    }

    /// <inheritdoc cref="Get{T}"/>
    public void Send(HttpMethod method, string route, object? body = null) =>
        Exchange(root => Request(method, root + route, body)).Dispose();

    /// <inheritdoc cref="Get{T}"/>
    public Task<T> GetAsync<T>(string route, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, route, null, ct);

    /// <inheritdoc cref="Get{T}"/>
    public async Task SendAsync(HttpMethod method, string route, object? body, CancellationToken ct)
    {
        using var response = await ExchangeAsync(root => Request(method, root + route, body), HttpCompletionOption.ResponseContentRead, blocking: false, ct);
    }

    /// <summary>
    /// For a body that isn't JSON, such as an image, read whole. The server's route sends no length, so the limit is
    /// checked as the bytes arrive, and the whole read has one deadline: <see cref="ExchangeAsync"/>'s timeout ends with the headers.
    /// </summary>
    /// <param name="route">The route.</param>
    /// <param name="maxBytes">The most to accept. A longer body is a server that isn't one of ours, or is broken.</param>
    /// <param name="ct">Stops the read.</param>
    /// <exception cref="ServerUnreachableException">The body was longer than <paramref name="maxBytes"/>, broke off, or didn't finish in time.</exception>
    /// <inheritdoc cref="Get{T}"/>
    public async Task<byte[]> GetBytesAsync(string route, int maxBytes, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(RequestTimeout, _time);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            using var response = await ExchangeAsync(root => Request(HttpMethod.Get, root + route), HttpCompletionOption.ResponseHeadersRead, blocking: false, limit.Token);
            if (response.Content.Headers.ContentLength > maxBytes) throw new ServerUnreachableException();

            await using var body = await response.Content.ReadAsStreamAsync(limit.Token);
            using var all = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, limit.Token)) > 0)
            {
                if (all.Length + read > maxBytes) throw new ServerUnreachableException();

                all.Write(chunk, 0, read);
            }

            return all.ToArray();
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ServerUnreachableException(e);
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            throw new ServerUnreachableException(e);
        }
    }

    /// <summary>For a request whose answer matters, such as the <see cref="SessionReply"/> of <c>sign-in</c>.</summary>
    /// <inheritdoc cref="Get{T}"/>
    public async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken ct)
    {
        using var response = await ExchangeAsync(root => Request(method, root + route, body), HttpCompletionOption.ResponseContentRead, blocking: false, ct);
        return Read<T>(response);
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
            ct,
            StreamHeadersTimeout);
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
    /// Tries the addresses in turn under one timeout. It moves on only from a failure to connect, so a request the server
    /// may have acted on is never sent twice. Only a failure to connect at every address, with time left, is reported.
    /// Running out of time proves nothing, and a slow route must not look like a dead server. Two addresses that drop
    /// packets end that way too, because together they use up the time, so a server that has really gone is found by the
    /// end of the events connection, its silence limit or the monitor's own ping.
    /// </summary>
    private async Task<HttpResponseMessage> ExchangeAsync(
        Func<string, HttpRequestMessage> build, HttpCompletionOption completion, bool blocking, CancellationToken ct, TimeSpan? within = null)
    {
        using var timeout = new CancellationTokenSource(within ?? RequestTimeout, _time);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        Exception? failure = null;
        var serverMayBeUp = false;
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

                // After a timeout nobody can tell: the request may have been waiting for an answer or still connecting.
                serverMayBeUp = timeout.IsCancellationRequested || !NeverReachedTheServer(e);
                if (serverMayBeUp) break;
            }
        }

        if (serverMayBeUp) throw new ServerUnreachableException(failure);

        // A dead address says nothing either way. Invites list the home address first, so away from home one is always
        // dead, and waiting for it to agree would keep a viewer from ever hearing that the certificate was replaced.
        // The price is a false alarm from a stranger on that port at the home address while the real server is down.
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

        // With no code this didn't come from the API, and a person can do nothing with it but try again later.
        return code is { } known ? new ApiException(known) : new ServerUnreachableException();
    }

    private T Read<T>(HttpResponseMessage response)
    {
        try
        {
            if (JsonSerializer.Deserialize<T>(response.Content.ReadAsStream(), ApiJson.Options) is { } value) return value;
        }
        catch (JsonException)
        {
            // Thrown below, with the null.
        }

        throw new ServerUnreachableException();
    }
}
