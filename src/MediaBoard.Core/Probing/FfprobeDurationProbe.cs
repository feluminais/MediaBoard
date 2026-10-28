using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaBoard.Core.Activity;

namespace MediaBoard.Core.Probing;

/// <summary>Reads durations by running ffprobe. Without ffprobe every call returns null.</summary>
/// <param name="ffprobePath">Configured ffprobe location; null or blank looks "ffprobe" up on PATH.</param>
public sealed class FfprobeDurationProbe(Func<string?> ffprobePath, ActivityHub hub) : IDurationProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string MissingMessage = "Durations unavailable: ffprobe not found";

    private int _missingLogged;

    public async Task<double?> GetDurationSecondsAsync(string path, CancellationToken ct)
    {
        var configured = ffprobePath();
        var info = new ProcessStartInfo(string.IsNullOrWhiteSpace(configured) ? "ffprobe" : configured)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path })
        {
            info.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new Win32Exception();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            if (Interlocked.Exchange(ref _missingLogged, 1) == 0)
            {
                hub.Log(ActivityLevel.Warning, MissingMessage);
            }

            return null;
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var json = await stdout.ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                return process.ExitCode == 0 ? ParseDuration(json) : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Kill(process);
                return null;
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }
        }
    }

    /// <summary>
    /// Duration in seconds from <c>ffprobe -print_format json -show_format -show_streams</c> output:
    /// format.duration, else a stream's DURATION or DURATION-eng tag (HH:MM:SS.fffffffff).
    /// Null when neither gives a positive number.
    /// </summary>
    public static double? ParseDuration(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("format", out var format)
                && format.ValueKind == JsonValueKind.Object
                && format.TryGetProperty("duration", out var value)
                && ParseSeconds(value) is { } fromFormat)
            {
                return fromFormat;
            }

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.ValueKind != JsonValueKind.Object
                        || !stream.TryGetProperty("tags", out var tags)
                        || tags.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (var tag in tags.EnumerateObject())
                    {
                        if (tag.Name.Equals("DURATION", StringComparison.OrdinalIgnoreCase)
                            || tag.Name.Equals("DURATION-eng", StringComparison.OrdinalIgnoreCase))
                        {
                            if (ParseClock(tag.Value) is { } fromTag) return fromTag;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static double? ParseSeconds(JsonElement value)
    {
        double seconds;
        switch (value.ValueKind)
        {
            case JsonValueKind.String
                when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds):
                break;
            case JsonValueKind.Number:
                seconds = value.GetDouble();
                break;
            default:
                return null;
        }

        return double.IsFinite(seconds) && seconds > 0 ? seconds : null;
    }

    private static double? ParseClock(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return null;

        var parts = value.GetString()?.Split(':');
        if (parts is not { Length: 3 }
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        var total = hours * 3600.0 + minutes * 60.0 + seconds;
        return double.IsFinite(total) && total > 0 ? total : null;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }
}
