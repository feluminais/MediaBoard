using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MediaBoard.Core.Players.Vlc;

public enum VlcState
{
    Playing,
    Paused,
    Stopped,
}

/// <summary>What VLC's <c>/requests/status.json</c> says that a session cares about.</summary>
/// <param name="Time">The position, in whole seconds as VLC reports it; zero when VLC has none.</param>
/// <param name="Length">The length of the file, in whole seconds; zero when VLC has none.</param>
public readonly record struct VlcStatus(VlcState State, TimeSpan Time, TimeSpan Length)
{
    // Longer than any file; keeps a garbage number from overflowing TimeSpan.
    private const double MaxSeconds = 10_000_000;

    /// <summary>
    /// Reads VLC's answer. False when it is not a status object or its state is one this session does not use
    /// (VLC also reports transient states while it opens a file). A missing or negative time or length counts as zero.
    /// </summary>
    public static bool TryParse(string json, out VlcStatus status)
    {
        status = default;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("state", out var stateElement) || stateElement.ValueKind != JsonValueKind.String) return false;

            VlcState state;
            switch (stateElement.GetString())
            {
                case "playing":
                    state = VlcState.Playing;
                    break;
                case "paused":
                    state = VlcState.Paused;
                    break;
                case "stopped":
                    state = VlcState.Stopped;
                    break;
                default:
                    return false;
            }

            if (!TryReadSeconds(root, "time", out var time) || !TryReadSeconds(root, "length", out var length)) return false;

            status = new VlcStatus(state, time, length);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadSeconds(JsonElement root, string name, out TimeSpan seconds)
    {
        seconds = TimeSpan.Zero;
        if (!root.TryGetProperty(name, out var element)) return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value) || !double.IsFinite(value)) return false;

        seconds = value > 0 ? TimeSpan.FromSeconds(Math.Min(value, MaxSeconds)) : TimeSpan.Zero;
        return true;
    }
}

/// <summary>Asks VLC's HTTP interface for its status. Throws <see cref="HttpRequestException"/> when VLC does not answer.</summary>
public delegate Task<string> VlcStatusFetcher(int port, string password, CancellationToken ct);

/// <summary>Fetches <c>/requests/status.json</c> from a VLC listening on 127.0.0.1.</summary>
public sealed class VlcStatusClient
{
    private readonly HttpClient _http;

    /// <param name="handler">Replaces the network, for tests.</param>
    public VlcStatusClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
    }

    /// <summary>
    /// GET <c>/requests/status.json</c> with Basic authentication: VLC's HTTP interface takes an empty user name and
    /// the <c>--http-password</c>. The password goes only into the Authorization header, never into an error message.
    /// </summary>
    public async Task<string> FetchAsync(int port, string password, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/requests/status.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + password)));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"VLC answered {(int)response.StatusCode}", inner: null, response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
