using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MediaBoard.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using HttpProtocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols;

namespace MediaBoard.Desktop.Remote;

/// <summary>
/// What players read from on a viewer's PC: plain HTTP on loopback, relayed to the server over the pinned connection. So
/// the token never reaches a player or its history, and a stop or an error on the server arrives somewhere the app can
/// act on it, which matters because a player left without its stream reports the file as finished instead of failing.
/// Only URLs handed out by <see cref="UrlFor"/> answer, and each holds a random segment that other programs on this PC
/// can't guess. Bytes pass through memory only.
/// </summary>
/// <param name="api">The server.</param>
public sealed class StreamProxy(ApiClient api) : IStreamUrls, IAsyncDisposable
{
    private const int BufferBytes = 64 * 1024;

    // Leaving the app ends both, so an open response gets a moment and no more.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(1);

    private readonly string _segment = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private readonly ConcurrentDictionary<string, int> _files = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;
    private string _origin = "";

    public event Action<int, ApiErrorCode>? StreamFailed;

    /// <summary>Starts listening on a random loopback port, unless it already is.</summary>
    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is not null) return;

            // The empty builder reads no configuration, so nothing can add a listener, and it logs nothing, so no URL lands in a log.
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.Services.AddRoutingCore();
            builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
            {
                kestrel.AddServerHeader = false;

                // A paused player stops reading, and that is no reason to drop it.
                kestrel.Limits.MinResponseDataRate = null;
                kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            });

            var app = builder.Build();
            app.Run(ServeAsync);
            await app.StartAsync().ConfigureAwait(false);

            _origin = app.Urls.First();
            _app = app;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public string UrlFor(int fileId, string fileName)
    {
        // Waited on because the interface is synchronous. Once per run, off the session gate, and a start takes milliseconds.
        StartAsync().GetAwaiter().GetResult();

        var url = string.Create(CultureInfo.InvariantCulture, $"{_origin}/{_segment}/{fileId}/{SafeName(fileName)}");
        _files[url] = fileId;
        return url;
    }

    public int? FileOf(string locator) => _files.TryGetValue(locator, out var fileId) ? fileId : null;

    public async ValueTask DisposeAsync()
    {
        if (_app is { } app)
        {
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

        _lifecycle.Dispose();
    }

    /// <summary>
    /// Plain ASCII letters, digits, dots and dashes, which need no encoding in any player, and the real extension, which
    /// spares MPC-HC a probe.
    /// </summary>
    private static string SafeName(string fileName)
    {
        var extension = Safe(Path.GetExtension(fileName));
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
        to.ContentType = from.Content.Headers.ContentType?.ToString();
        to.Headers.AcceptRanges = "bytes";
        if (from.Content.Headers.ContentRange is { } range) to.Headers.ContentRange = range.ToString();
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

    private async Task ServeAsync(HttpContext context)
    {
        if (!_files.TryGetValue(_origin + context.Request.Path, out var fileId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        if (await OpenAsync(fileId, context.Request.Headers.Range.ToString(), context.RequestAborted) is not { } upstream)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        await RelayAsync(context, fileId, upstream);
    }

    /// <returns>The answer, or null when there is none: a failure, already raised, or the player leaving.</returns>
    private async Task<HttpResponseMessage?> OpenAsync(int fileId, string range, CancellationToken player)
    {
        try
        {
            return await api.OpenStreamAsync(fileId, range, player);
        }
        catch (OperationCanceledException) when (player.IsCancellationRequested)
        {
            return null;
        }
        catch (ApiException e)
        {
            StreamFailed?.Invoke(fileId, e.Code);
            return null;
        }
        catch (Exception e) when (e is ServerUnreachableException or IdentityChangedException)
        {
            StreamFailed?.Invoke(fileId, ApiErrorCode.Unreachable);
            return null;
        }
    }

    private async Task RelayAsync(HttpContext context, int fileId, HttpResponseMessage first)
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

                // A cut carries no code, so asking again is the only way to learn why, and if the server has a reason it
                // refuses. An answer that's cut before a byte arrives would go round for good.
                if (asked && copied == 0)
                {
                    StreamFailed?.Invoke(fileId, ApiErrorCode.Unreachable);
                    context.Abort();
                    return;
                }

                var at = start + sent;
                var next = await OpenAsync(fileId, string.Create(CultureInfo.InvariantCulture, $"bytes={at}-{last}"), player);
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
                StreamFailed?.Invoke(fileId, ApiErrorCode.FileMissing);
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
