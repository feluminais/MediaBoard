using System.Net;
using System.Net.Http.Headers;

namespace MediaBoard.Core.Metadata.Tmdb;

/// <summary>
/// Sends requests one at a time, at most <see cref="MaxPerSecond"/> per second. On 429 it waits for
/// Retry-After (2 s when absent) and sends the request again, up to <see cref="MaxRetries"/> times, then
/// gives up with <see cref="UnavailableReason.ServerError"/>. The back-off holds the queue, so other
/// callers wait too. Pacing and back-off both run on the injected <see cref="TimeProvider"/>.
/// </summary>
internal sealed class RequestQueue(TimeProvider? timeProvider = null)
{
    public const int MaxPerSecond = 20;
    public const int MaxRetries = 3;
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(2);

    // A hostile or buggy Retry-After must not park the queue for hours.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _sent = new();

    /// <summary>
    /// Sends the request built by <paramref name="createRequest"/> (called again for every attempt).
    /// The caller disposes the returned response.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(HttpClient http, Func<HttpRequestMessage> createRequest,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await WaitForSlotAsync(ct).ConfigureAwait(false);
                using var request = createRequest();
                var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.TooManyRequests) return response;

                var delay = RetryDelay(response.Headers.RetryAfter);
                response.Dispose();
                if (attempt >= MaxRetries)
                    throw new MetadataUnavailableException(UnavailableReason.ServerError,
                        "TMDB is rate limiting requests.");

                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WaitForSlotAsync(CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(1);
        var now = _time.GetUtcNow();
        while (_sent.Count > 0 && now - _sent.Peek() >= window) _sent.Dequeue();

        if (_sent.Count >= MaxPerSecond)
        {
            await Task.Delay(window - (now - _sent.Peek()), _time, ct).ConfigureAwait(false);
            now = _time.GetUtcNow();
            while (_sent.Count > 0 && now - _sent.Peek() >= window) _sent.Dequeue();
        }

        _sent.Enqueue(now);
    }

    private TimeSpan RetryDelay(RetryConditionHeaderValue? retryAfter)
    {
        TimeSpan? delay = retryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - _time.GetUtcNow(),
            _ => null,
        };

        if (delay is null) return DefaultRetryAfter;
        if (delay < TimeSpan.Zero) return TimeSpan.Zero;
        return delay > MaxRetryAfter ? MaxRetryAfter : delay.Value;
    }
}
