using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CozyCouch.Core.Parsing;
using CozyCouch.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using HttpProtocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols;

namespace CozyCouch.Desktop.Remote;

/// <summary>
/// What players read from on a viewer's PC: plain HTTP on loopback, relayed to the server over the pinned connection. So
/// the token never reaches a player or its history, and a stop or an error on the server arrives somewhere the app can
/// act on it, which matters because a player left without its stream reports the file as finished instead of failing.
/// Only URLs handed out by <see cref="UrlFor"/> and not yet released answer, and each holds its own random segment that
/// other programs on this PC can't guess. It listens only while one of those exists, on a new port each time. Bytes pass
/// through memory only.
/// </summary>
/// <param name="api">The server.</param>
/// <param name="port">The loopback port, or 0 for any free one.</param>
public sealed class StreamProxy(ApiClient api, int port = 0) : IStreamUrls, IAsyncDisposable
{
    private const int BufferBytes = 64 * 1024;

    // Leaving the app ends both, so an open response gets a moment and no more.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, int> _files = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;
    private string _origin = "";
    private bool _disposed;

    public event Action<int, string, ApiErrorCode>? StreamFailed;

    /// <summary>Starts listening, unless it already is. <see cref="UrlFor"/> does this by itself.</summary>
    /// <exception cref="StreamUnavailableException">The proxy was disposed, or the address couldn't be bound.</exception>
    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await ListenAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <exception cref="StreamUnavailableException">The proxy was disposed, or the address couldn't be bound.</exception>
    public string UrlFor(int fileId, string fileName)
    {
        // Waited on because the interface is synchronous, which is safe on the pool whatever the caller's thread is. This
        // runs once per Play, off the session gate, and a start takes milliseconds.
        return Task.Run(() => HandOutAsync(fileId, fileName)).GetAwaiter().GetResult();
    }

    public int? FileOf(string locator) => _files.TryGetValue(locator, out var fileId) ? fileId : null;

    public void Release(string url)
    {
        if (_files.TryRemove(url, out _) && _files.IsEmpty) _ = Task.Run(StopIfUnusedAsync);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            _disposed = true;
            await StopAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Plain ASCII letters, digits, dots and dashes, which need no encoding in any player, and the real extension, which
    /// spares MPC-HC a probe. Only a video's: the name comes from the server, and a player handed a playlist extension
    /// would follow the links inside it to any host or file share.
    /// </summary>
    private static string SafeName(string fileName)
    {
        var extension = MediaFileRules.IsVideo(fileName) ? Safe(Path.GetExtension(fileName)) : ".mkv";
        var stem = Safe(Path.GetFileNameWithoutExtension(fileName)).Trim('.');

        // A segment of dots would be walked up by whatever cleans the URL.
        return (stem.Length > 0 ? stem : "file") + (extension.Length > 1 ? extension : "");

        static string Safe(string text)
        {
            var safe = new StringBuilder(text.Length);
            foreach (var letter in text)
            {
                if (char.IsAsciiLetterOrDigit(letter) || letter is '.' or '-') safe.Append(letter);
            }

            return safe.ToString();
        }
    }

    private static void CopyHeaders(HttpResponseMessage from, HttpResponse to)
    {
        to.StatusCode = (int)from.StatusCode;
        to.ContentLength = from.Content.Headers.ContentLength;
        SetTypeHeaders(from, to);
        to.Headers.AcceptRanges = "bytes";
        if (from.Content.Headers.ContentRange is { } range) to.Headers.ContentRange = range.ToString();
    }

    // A player reads the type to decide how to treat the bytes, so a server can't call them a playlist.
    private static void SetTypeHeaders(HttpResponseMessage from, HttpResponse to)
    {
        var type = from.Content.Headers.ContentType?.MediaType;
        to.ContentType = type?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ? type : "application/octet-stream";
        to.Headers.XContentTypeOptions = "nosniff";
    }

    /// <summary>
    /// Copies a body until it ends, the server cuts it or the player leaves. The two ends fail apart on purpose: a player
    /// drops its request on every seek, so nothing that happens on its side may read as the stream breaking.
    /// </summary>
    /// <returns>The bytes copied, and whether the server cut the body.</returns>
    private static async Task<(long Copied, bool Cut)> CopyAsync(HttpResponseMessage from, HttpResponse to, byte[] buffer, CancellationToken player)
    {
        var copied = 0L;
        try
        {
            await using var body = await from.Content.ReadAsStreamAsync(player);
            while (await body.ReadAsync(buffer, player) is var read and > 0)
            {
                try
                {
                    await to.Body.WriteAsync(buffer.AsMemory(0, read), player);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException)
                {
                    return (copied, false);
                }

                copied += read;
            }

            return (copied, false);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException)
        {
            return (copied, !player.IsCancellationRequested);
        }
    }

    private async Task<string> HandOutAsync(int fileId, string fileName)
    {
        await _lifecycle.WaitAsync();
        try
        {
            await ListenAsync();

            // Under the gate, so a stop that found nothing left can't run past a URL handed out behind it.
            var segment = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var url = string.Create(CultureInfo.InvariantCulture, $"{_origin}/{segment}/{fileId}/{SafeName(fileName)}");
            _files[url] = fileId;
            return url;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    // Called with _lifecycle held.
    private async Task ListenAsync()
    {
        if (_disposed) throw new StreamUnavailableException();
        if (_app is not null) return;

        // The empty builder reads no configuration, so nothing can add a listener, and it logs nothing, so no URL lands in a log.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRoutingCore();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;

            // A paused player stops reading, and that is no reason to drop it.
            kestrel.Limits.MinResponseDataRate = null;
            kestrel.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1);
        });

        var app = builder.Build();
        app.Run(context => ServeAsync(context, app.Lifetime.ApplicationStopping));
        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw new StreamUnavailableException(e);
        }

        _origin = app.Urls.First();
        _app = app;
    }

    private async Task StopIfUnusedAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_files.IsEmpty) await StopAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    // Called with _lifecycle held.
    private async Task StopAsync()
    {
        if (_app is not { } app) return;

        _app = null;
        using var timeout = new CancellationTokenSource(StopTimeout);
        try
        {
            await app.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // A response still open. Disposing cuts it.
        }

        await app.DisposeAsync();
    }

    private async Task ServeAsync(HttpContext context, CancellationToken stopping)
    {
        var url = _origin + context.Request.Path;
        if (!_files.TryGetValue(url, out var fileId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        else if (HttpMethods.IsHead(context.Request.Method))
        {
            await AnswerHeadAsync(context, url, fileId);
        }
        else if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        }
        else if (await OpenAsync(url, fileId, context.Request.Headers.Range.ToString(), context.RequestAborted) is { } upstream)
        {
            await RelayAsync(context, url, fileId, upstream, stopping);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    /// <summary>The stream route is GET only, and one byte of the file gives its length and type.</summary>
    private async Task AnswerHeadAsync(HttpContext context, string url, int fileId)
    {
        using var upstream = await OpenAsync(url, fileId, "bytes=0-0", context.RequestAborted);
        if (upstream is null)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = upstream.Content.Headers.ContentRange?.Length ?? upstream.Content.Headers.ContentLength;
        SetTypeHeaders(upstream, context.Response);
        context.Response.Headers.AcceptRanges = "bytes";
    }

    /// <returns>The answer, or null when there is none: a failure, already raised, or <paramref name="ct"/> ending the wait.</returns>
    private async Task<HttpResponseMessage?> OpenAsync(string url, int fileId, string range, CancellationToken ct)
    {
        try
        {
            return await api.OpenStreamAsync(fileId, range, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (ApiException e)
        {
            Fail(url, fileId, e.Code);
            return null;
        }
        catch (Exception e) when (e is ServerUnreachableException or IdentityChangedException)
        {
            Fail(url, fileId, ApiErrorCode.Unreachable);
            return null;
        }
    }

    // A URL already released belongs to a session that's over, and the same file may be playing again on a new one.
    private void Fail(string url, int fileId, ApiErrorCode code)
    {
        if (_files.ContainsKey(url)) StreamFailed?.Invoke(fileId, url, code);
    }

    private async Task RelayAsync(HttpContext context, string url, int fileId, HttpResponseMessage first, CancellationToken stopping)
    {
        var current = first;
        try
        {
            CopyHeaders(first, context.Response);

            var player = context.RequestAborted;
            var promised = first.Content.Headers.ContentRange;
            var start = promised?.From ?? 0;
            var last = promised?.To ?? first.Content.Headers.ContentLength - 1;
            var buffer = new byte[BufferBytes];
            var sent = 0L;
            var asked = false;
            while (true)
            {
                var (copied, cut) = await CopyAsync(current, context.Response, buffer, player);
                sent += copied;
                if (!cut) return;

                // An answer that's cut before a byte arrives would go round for good.
                if (asked && copied == 0)
                {
                    Fail(url, fileId, ApiErrorCode.Unreachable);
                    context.Abort();
                    return;
                }

                // A cut carries no code, so asking again is the only way to learn why, and if the server has a reason it
                // refuses. This outlives the player's request on purpose: its session is still open, and a player that
                // gives up here goes on to report the file as finished unless the failure is raised. The client's limit
                // on headers ends the wait.
                var at = start + sent;
                var next = await OpenAsync(url, fileId, string.Create(CultureInfo.InvariantCulture, $"bytes={at}-{last}"), stopping);
                if (next is null)
                {
                    context.Abort();
                    return;
                }

                current.Dispose();
                current = next;
                asked = true;
                if (next.StatusCode == HttpStatusCode.PartialContent && next.Content.Headers.ContentRange?.From == at) continue;

                // The bytes promised to the player aren't there any more: the file shrank or was replaced.
                Fail(url, fileId, ApiErrorCode.FileMissing);
                context.Abort();
                return;
            }
        }
        finally
        {
            // Mid-body this closes the connection to the server, which is what ends its side when the player leaves.
            current.Dispose();
        }
    }
}
