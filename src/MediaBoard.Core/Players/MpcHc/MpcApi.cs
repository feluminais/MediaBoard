using System.Globalization;
using System.Text;

namespace MediaBoard.Core.Players.MpcHc;

/// <summary>Constants and payload helpers from MPC-HC's <c>MpcApi.h</c> (WM_COPYDATA "slave" API).</summary>
public static class MpcApi
{
    // MPC-HC to host
    public const uint CMD_CONNECT = 0x50000000;
    public const uint CMD_STATE = 0x50000001;
    public const uint CMD_PLAYMODE = 0x50000002;
    public const uint CMD_NOWPLAYING = 0x50000003;
    public const uint CMD_CURRENTPOSITION = 0x50000007;
    public const uint CMD_NOTIFYSEEK = 0x50000008;
    public const uint CMD_NOTIFYENDOFSTREAM = 0x50000009;
    public const uint CMD_DISCONNECT = 0x5000000B;

    // Host to MPC-HC
    public const uint CMD_OPENFILE = 0xA0000000;
    public const uint CMD_PLAY = 0xA0000004;
    public const uint CMD_PAUSE = 0xA0000005;
    public const uint CMD_SETPOSITION = 0xA0002000;
    public const uint CMD_GETNOWPLAYING = 0xA0003002;
    public const uint CMD_GETCURRENTPOSITION = 0xA0003004;
    public const uint CMD_CLOSEAPP = 0xA0004006;

    /// <summary>
    /// Splits a multi-parameter payload on <c>|</c>. MPC-HC escapes a literal <c>|</c> as <c>\|</c>; any other
    /// backslash (as in a file path) is kept as is.
    /// </summary>
    public static IReadOnlyList<string> SplitPayload(string payload)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < payload.Length; i++)
        {
            var c = payload[i];
            if (c == '\\' && i + 1 < payload.Length && payload[i + 1] == '|')
            {
                current.Append('|');
                i++;
            }
            else if (c == '|')
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>Reads <c>title|author|description|filename|duration</c>, duration in seconds (may be empty).</summary>
    /// <exception cref="FormatException">The payload does not have five fields or the duration is not a number.</exception>
    public static NowPlaying ParseNowPlaying(string payload)
    {
        var parts = SplitPayload(payload);
        if (parts.Count < 5) throw new FormatException($"Expected 5 fields in now-playing payload, got {parts.Count}");

        var duration = TimeSpan.Zero;
        if (parts[4].Length > 0 && !TryParseSeconds(parts[4], out duration))
        {
            throw new FormatException($"Not a duration in seconds: {parts[4]}");
        }

        return new NowPlaying(parts[0], parts[1], parts[2], parts[3], duration);
    }

    /// <summary>Seconds as MPC-HC reads them (<c>_wtof</c>: invariant decimal point).</summary>
    public static string FormatSeconds(TimeSpan at) =>
        at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    // As the other players' readers cap it: a TimeSpan can't hold whatever a broken file reports.
    private const double MaxSeconds = 10_000_000;

    /// <summary>Reads seconds as MPC-HC writes them (<c>%.3f</c>).</summary>
    public static bool TryParseSeconds(string text, out TimeSpan at)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds is >= 0 and <= MaxSeconds)
        {
            at = TimeSpan.FromSeconds(seconds);
            return true;
        }

        at = TimeSpan.Zero;
        return false;
    }
}

/// <summary>MPC_LOADSTATE.</summary>
public enum MpcLoadState
{
    Closed = 0,
    Loading = 1,
    Loaded = 2,
    Closing = 3,
    Failing = 4,
}

/// <summary>MPC_PLAYSTATE.</summary>
public enum MpcPlayState
{
    Play = 0,
    Pause = 1,
    Stop = 2,
}

/// <summary>The CMD_NOWPLAYING payload for a file.</summary>
public sealed record NowPlaying(string Title, string Author, string Description, string FileName, TimeSpan Duration);

/// <summary>Carries WM_COPYDATA messages between the app and MPC-HC.</summary>
public interface IMpcTransport : IDisposable
{
    /// <summary>The window MPC-HC sends its notifications to (passed as <c>/slave &lt;hwnd&gt;</c>).</summary>
    nint HostHandle { get; }

    /// <summary>A message from MPC-HC: command and payload.</summary>
    event Action<uint, string>? Received;

    /// <summary>Sends a command to MPC-HC's window. Silently does nothing if the window is gone or hung.</summary>
    void Send(nint target, uint cmd, string payload);
}
