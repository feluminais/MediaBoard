using System.Globalization;
using System.Net;
using System.Text;

namespace MediaBoard.Core.Players.MpcHc;

/// <summary>What MPC-HC's playback state number means in <c>variables.html</c> (DirectShow's filter states).</summary>
public enum MpcWebState
{
    Stopped = 0,
    Paused = 1,
    Playing = 2,
}

/// <summary>What the web interface page <c>variables.html</c> says that a session cares about.</summary>
/// <param name="FilePath">The open file's path as MPC-HC has it; empty when no file is open.</param>
/// <param name="State">The playback state; null for a number this session does not know (MPC-HC says "N/A" then).</param>
/// <param name="Position">The position, from the page's milliseconds.</param>
/// <param name="Duration">The length, from the page's milliseconds; zero when MPC-HC has none.</param>
public readonly record struct MpcWebStatus(string FilePath, MpcWebState? State, TimeSpan Position, TimeSpan Duration)
{
    // Longer than any file; keeps a garbage number from overflowing TimeSpan.
    private const double MaxSeconds = 10_000_000;

    /// <summary>
    /// Reads the page: a list of <c>&lt;p id="name"&gt;value&lt;/p&gt;</c> lines. False when it has no state, which
    /// is what any other page on the port looks like. A missing or unreadable position or length counts as zero.
    /// The values are not HTML-escaped by MPC-HC, so they are taken as they stand.
    /// </summary>
    public static bool TryParse(string html, out MpcWebStatus status)
    {
        status = default;
        if (string.IsNullOrEmpty(html) || !TryRead(html, "state", out var stateText)) return false;
        if (!int.TryParse(stateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var state)) return false;

        TryRead(html, "filepath", out var path);
        status = new MpcWebStatus(
            path,
            Enum.IsDefined(typeof(MpcWebState), state) ? (MpcWebState)state : null,
            ReadMilliseconds(html, "position"),
            ReadMilliseconds(html, "duration"));
        return true;
    }

    private static bool TryRead(string html, string id, out string value)
    {
        value = "";
        var open = $"<p id=\"{id}\">";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return false;

        start += open.Length;
        var end = html.IndexOf("</p>", start, StringComparison.Ordinal);
        if (end < 0) return false;

        value = html[start..end].Trim();
        return true;
    }

    private static TimeSpan ReadMilliseconds(string html, string id)
    {
        if (!TryRead(html, id, out var text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) return TimeSpan.Zero;

        var seconds = ms / 1000;
        return double.IsFinite(seconds) && seconds > 0 ? TimeSpan.FromSeconds(Math.Min(seconds, MaxSeconds)) : TimeSpan.Zero;
    }
}

/// <summary>Asks MPC-HC's web interface for <c>variables.html</c>. Throws <see cref="HttpRequestException"/> when it does not answer.</summary>
public delegate Task<string> MpcWebFetcher(int port, CancellationToken ct);

/// <summary>Fetches <c>/variables.html</c> from an MPC-HC web interface listening on 127.0.0.1.</summary>
public sealed class MpcWebClient
{
    private readonly HttpClient _http;

    /// <param name="handler">Replaces the network, for tests.</param>
    public MpcWebClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
    }

    public async Task<string> FetchAsync(int port, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/variables.html", ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"MPC-HC answered {(int)response.StatusCode}", inner: null, response.StatusCode);
        }

        // The page is UTF-8 whatever the header says, and file names are in it.
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }
}
