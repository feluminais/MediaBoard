using System.Collections.Concurrent;

namespace CozyCouch.Core.Metadata;

/// <summary>
/// Poster files kept on disk, so the library looks complete offline. A poster is downloaded once, the first
/// time it is asked for while online, and read from disk after that.
/// </summary>
/// <param name="http">Fetches from TMDB.</param>
/// <param name="connectivity">Says whether to try.</param>
/// <param name="imagesDir">The folder.</param>
/// <param name="time">The clock, or null for the real one.</param>
/// <param name="maxBytes">Past this the folder has no room for viewers' requests. This PC's own posters still download.</param>
public sealed class ImageCache(HttpClient http, Connectivity connectivity, string imagesDir, TimeProvider? time = null, long maxBytes = 5L * 1024 * 1024 * 1024)
{
    private const string BaseUrl = "https://image.tmdb.org/t/p/";
    private const int MaxRememberedFailures = 1_000;

    private static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(5);
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private static readonly TimeSpan SizeMeasureEvery = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _sizeGate = new();
    private long _size;
    private long _sizeMeasuredAt;
    private bool _sizeMeasured;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _downloads = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new();

    /// <summary>Whether this poster is on disk already, so handing it out costs nothing.</summary>
    public bool Has(string posterPath, int width) =>
        LocalName(posterPath, width) is { } name && File.Exists(Path.Combine(imagesDir, name));

    /// <summary>Whether the folder is under its limit. Measured at most once a minute, since it walks every file.</summary>
    public bool HasRoom()
    {
        var now = _time.GetTimestamp();
        lock (_sizeGate)
        {
            if (_sizeMeasured && _time.GetElapsedTime(_sizeMeasuredAt, now) < SizeMeasureEvery) return _size < maxBytes;

            try
            {
                _size = Directory.Exists(imagesDir) ? new DirectoryInfo(imagesDir).EnumerateFiles().Sum(file => file.Length) : 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The folder went as it was read. The last size stands until the next measure.
            }

            _sizeMeasured = true;
            _sizeMeasuredAt = now;
            return _size < maxBytes;
        }
    }

    /// <summary>How many failed names are held. A viewer can ask for any name, so this has a limit.</summary>
    internal int RememberedFailures => _failed.Count;

    /// <summary>
    /// The local file for a TMDB poster path such as <c>/abc123.jpg</c>, downloading it when it is missing and
    /// TMDB is reachable. Returns null when the file is missing and cannot be had now: offline, a network
    /// failure, a non-success answer (not asked again for five minutes), or a path that is not a plain file name.
    /// </summary>
    /// <param name="width">342 or 780 for a poster, 300 for an episode still.</param>
    public async Task<string?> GetAsync(string posterPath, int width, CancellationToken ct)
    {
        if (width is not (300 or 342 or 780)) throw new ArgumentOutOfRangeException(nameof(width), width, "Only 300, 342 and 780 are cached.");

        var name = LocalName(posterPath, width);
        if (name is null) return null;

        var path = Path.Combine(imagesDir, name);
        if (File.Exists(path)) return path;
        if (!connectivity.IsOnline || RecentlyFailed(name)) return null;

        // One download per file, shared by everyone who asks meanwhile. Racing threads may each build an entry,
        // but only the one stored wins, and only the winner starts the download (the entry is lazy, so a
        // discarded one never runs). The download does not take the first caller's token, so that caller
        // giving up does not fail the others.
        var created = new Lazy<Task<string?>>(
            () => DownloadAsync(name, posterPath, width, path), LazyThreadSafetyMode.ExecutionAndPublication);
        var entry = _downloads.GetOrAdd(name, created);
        if (ReferenceEquals(entry, created))
        {
            // Forget the entry once it is done, so a later miss (say, the file was deleted) downloads again.
            // Removing by pair leaves any newer entry for the same name alone.
            _ = entry.Value.ContinueWith(
                _ => _downloads.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(name, entry)),
                TaskScheduler.Default);
        }

        return await entry.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> DownloadAsync(string name, string posterPath, int width, string path)
    {
        // Run off the caller's context.
        await Task.Yield();

        // A caller can pass the first File.Exists check just before an earlier download lands.
        if (File.Exists(path)) return path;

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(imagesDir);
            using var response = await http
                .GetAsync(new Uri($"{BaseUrl}w{width}{posterPath}"), HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RememberFailure(name);
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

    private bool RecentlyFailed(string name)
    {
        if (!_failed.TryGetValue(name, out var failedAt)) return false;
        if (_time.GetUtcNow() - failedAt < FailureMemory) return true;

        _failed.TryRemove(name, out _);
        return false;
    }

    // Forgetting them all at the limit costs one more request per name, and nothing has to sort them.
    private void RememberFailure(string name)
    {
        if (_failed.Count >= MaxRememberedFailures) _failed.Clear();
        _failed[name] = _time.GetUtcNow();
    }

    // "w342" + the file name, or null when the path is anything but "/" followed by one plain file name.
    private static string? LocalName(string posterPath, int width)
    {
        if (posterPath is not ['/', .. var file] || file.Length == 0) return null;
        if (file.Contains("..", StringComparison.Ordinal) || file.IndexOfAny(InvalidNameChars) >= 0) return null;

        return $"w{width}{file}";
    }
}
