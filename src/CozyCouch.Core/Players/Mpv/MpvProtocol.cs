using System.Text.Json;

namespace CozyCouch.Core.Players.Mpv;

/// <summary>What mpv's JSON IPC reports that a session cares about.</summary>
public abstract record MpvMessage
{
    /// <summary>The playback position (<c>time-pos</c>).</summary>
    public sealed record TimePos(TimeSpan At) : MpvMessage;

    /// <summary>The length of the file (<c>duration</c>).</summary>
    public sealed record Duration(TimeSpan Length) : MpvMessage;

    public sealed record Pause(bool Paused) : MpvMessage;

    /// <summary>Whether playback has reached the end of the file (<c>eof-reached</c>).</summary>
    public sealed record EofReached(bool Reached) : MpvMessage;

    /// <summary>The file mpv has open (<c>path</c>), as it was given to mpv; null when mpv has none.</summary>
    public sealed record Path(string? File) : MpvMessage;

    /// <summary>The file stopped playing. <paramref name="Reason"/> is mpv's: eof, stop, quit, error, redirect.</summary>
    public sealed record EndFile(string Reason) : MpvMessage;
}

/// <summary>
/// mpv's JSON IPC: one JSON object per line. Commands go in as <c>{"command":[...]}</c>; property changes and
/// events come back as <c>{"event":"property-change","name":...,"data":...}</c> and <c>{"event":"end-file",...}</c>.
/// </summary>
public static class MpvProtocol
{
    public const string TimePosProperty = "time-pos";
    public const string DurationProperty = "duration";
    public const string PauseProperty = "pause";
    public const string EofReachedProperty = "eof-reached";
    public const string PathProperty = "path";

    // Longer than any file; keeps a garbage number from overflowing TimeSpan.
    private const double MaxSeconds = 10_000_000;

    /// <summary>The command that asks mpv to report every change of <paramref name="property"/>.</summary>
    public static string Observe(int id, string property)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("command");
            json.WriteStringValue("observe_property");
            json.WriteNumberValue(id);
            json.WriteStringValue(property);
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Reads one line mpv sent. Null for anything the session does not use: command replies, other events,
    /// properties that are unavailable (<c>data</c> null or missing), malformed lines.
    /// </summary>
    public static MpvMessage? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("event", out var eventName) || eventName.ValueKind != JsonValueKind.String) return null;

            return eventName.GetString() switch
            {
                "property-change" => ParseProperty(root),
                "end-file" => root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                    ? new MpvMessage.EndFile(reason.GetString()!)
                    : null,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MpvMessage? ParseProperty(JsonElement root)
    {
        if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return null;
        if (!root.TryGetProperty("data", out var data))
        {
            // mpv leaves data out when the property is unavailable; for the path that means no file.
            return name.GetString() == PathProperty ? new MpvMessage.Path(null) : null;
        }

        switch (name.GetString())
        {
            case TimePosProperty:
                return TryReadSeconds(data, out var at) ? new MpvMessage.TimePos(at) : null;
            case DurationProperty:
                return TryReadSeconds(data, out var length) ? new MpvMessage.Duration(length) : null;
            case PauseProperty:
                return TryReadBool(data, out var paused) ? new MpvMessage.Pause(paused) : null;
            case EofReachedProperty:
                return TryReadBool(data, out var reached) ? new MpvMessage.EofReached(reached) : null;
            case PathProperty:
                return data.ValueKind == JsonValueKind.String ? new MpvMessage.Path(data.GetString()) : new MpvMessage.Path(null);
            default:
                return null;
        }
    }

    private static bool TryReadSeconds(JsonElement data, out TimeSpan seconds)
    {
        seconds = default;
        if (data.ValueKind != JsonValueKind.Number || !data.TryGetDouble(out var value)) return false;
        if (!double.IsFinite(value) || value < 0 || value > MaxSeconds) return false;

        seconds = TimeSpan.FromSeconds(value);
        return true;
    }

    private static bool TryReadBool(JsonElement data, out bool value)
    {
        value = data.ValueKind == JsonValueKind.True;
        return data.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }
}
