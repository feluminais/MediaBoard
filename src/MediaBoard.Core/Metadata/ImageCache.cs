using System.Collections.Concurrent;

namespace MediaBoard.Core.Metadata;

/// <summary>
/// Poster files kept on disk, so the library looks complete offline. A poster is downloaded once, the first
/// time it is asked for while online, and read from disk after that.
/// </summary>
public sealed class ImageCache(HttpClient http, Connectivity connectivity, string imagesDir)
{
    private const string BaseUrl = "https://image.tmdb.org/t/p/";

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private readonly ConcurrentDictionary<string, Task<string?>> _downloads = new();
    private readonly ConcurrentDictionary<string, bool> _failed = new();

    /// <summary>
    /// The local file for a TMDB poster path such as <c>/abc123.jpg</c>, downloading it when it is missing and
    /// TMDB is reachable. Returns null when the file is missing and cannot be had now: offline, a network
    /// failure, a non-success answer (not asked again during this run), or a path that is not a plain file name.
    /// </summary>
    /// <param name="width">342 or 780.</param>
    public async Task<string?> GetAsync(string posterPath, int width, CancellationToken ct)
    {
        if (width is not (342 or 780)) throw new ArgumentOutOfRangeException(nameof(width), width, "Only 342 and 780 are cached.");

        var name = LocalName(posterPath, width);
        if (name is null) return null;

        var path = Path.Combine(imagesDir, name);
        if (File.Exists(path)) return path;
        if (!connectivity.IsOnline || _failed.ContainsKey(name)) return null;

        // One download per file, shared by everyone who asks meanwhile. It does not take the first caller's
        // token, so that caller giving up does not fail the others.
        var download = _downloads.GetOrAdd(name, _ => DownloadAndForgetAsync(name, posterPath, width, path));
        return await download.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> DownloadAndForgetAsync(string name, string posterPath, int width, string path)
    {
        // Run off the caller's context, and never complete synchronously before GetOrAdd has stored the task.
        await Task.Yield();
        try
        {
            return await DownloadAsync(name, posterPath, width, path).ConfigureAwait(false);
        }
        finally
        {
            _downloads.TryRemove(name, out _);
        }
    }

    private async Task<string?> DownloadAsync(string name, string posterPath, int width, string path)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(imagesDir);
            using var response = await http
                .GetAsync(new Uri($"{BaseUrl}w{width}{posterPath}"), HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _failed[name] = true;
                return null;
            }

            await using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(target).ConfigureAwait(false);
            }

            // A crash before this line leaves only the temp file, never a half image under the final name.
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            connectivity.ReportOffline();
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp file is harmless; the next run does not read it.
            }
        }
    }

    // "w342" + the file name, or null when the path is anything but "/" followed by one plain file name.
    private static string? LocalName(string posterPath, int width)
    {
        if (posterPath is not ['/', .. var file] || file.Length == 0) return null;
        if (file.Contains("..", StringComparison.Ordinal) || file.IndexOfAny(InvalidNameChars) >= 0) return null;

        return $"w{width}{file}";
    }
}
