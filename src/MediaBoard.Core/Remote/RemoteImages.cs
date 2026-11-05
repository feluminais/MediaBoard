using System.Collections.Concurrent;
using MediaBoard.Core.Metadata;

namespace MediaBoard.Core.Remote;

/// <summary>
/// Poster files kept on disk for a viewer, fetched from the server once and read from disk after that. It has the shape
/// of <c>ImageCache</c> and keeps its files in a folder of its own, so nothing here mixes with images this PC fetched itself.
/// </summary>
/// <param name="client">The server to fetch from.</param>
/// <param name="imagesDir">The images folder (<c>AppPaths.ImagesDir</c>); the cache lives in a folder below it.</param>
public sealed class RemoteImages(ApiClient client, string imagesDir, TimeProvider? time = null)
{
    // The server answers 404 for "TMDB can't be reached right now" as well, so a miss is only remembered for a while.
    private static readonly TimeSpan MissMemory = TimeSpan.FromMinutes(5);

    private const int MaxBytes = 10 * 1024 * 1024;

    private readonly string _dir = Path.GetFullPath(Path.Combine(imagesDir, "remote"));
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _downloads = new();
    private readonly ConcurrentDictionary<string, long> _failed = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// The local file for a TMDB poster path such as <c>/abc123.jpg</c>, fetching it when it is missing and the server can
    /// be reached. Returns null when the file is missing and can't be had now: the server is out of reach, it has no such
    /// image (not asked again for five minutes), or the path is not a plain TMDB file name. Cancelling stops the wait, not a
    /// fetch other callers share.
    /// </summary>
    /// <param name="width">342 or 780 for a poster, 300 for an episode still.</param>
    public async Task<string?> GetAsync(string posterPath, int width, CancellationToken ct)
    {
        if (width is not (300 or 342 or 780)) throw new ArgumentOutOfRangeException(nameof(width), width, "Only 300, 342 and 780 are cached.");

        if (FileNameOf(posterPath) is not { } file) return null;

        var name = $"w{width}{file}";
        var path = Path.GetFullPath(Path.Combine(_dir, name));

        // The name is checked already; this holds the line if that ever changes.
        if (!path.StartsWith(_dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;

        if (File.Exists(path)) return path;
        if (RecentlyMissed(name)) return null;

        // One fetch per file, shared by everyone who asks meanwhile, and not tied to the first caller's token.
        var created = new Lazy<Task<string?>>(() => FetchAsync(name, file, width, path), LazyThreadSafetyMode.ExecutionAndPublication);
        var entry = _downloads.GetOrAdd(name, created);
        if (ReferenceEquals(entry, created))
        {
            _ = entry.Value.ContinueWith(
                _ => _downloads.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(name, entry)),
                TaskScheduler.Default);
        }

        return await entry.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> FetchAsync(string name, string file, int width, string path)
    {
        await Task.Yield();

        if (File.Exists(path)) return path;

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_dir);
            var bytes = await client.GetBytesAsync($"images/{width}/{file}", MaxBytes, CancellationToken.None).ConfigureAwait(false);
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);

            // A crash before this line leaves only the temp file, never a half image under the final name.
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (ApiException e) when (e.Code == ApiErrorCode.NotFound)
        {
            _failed[name] = _time.GetTimestamp();
            return null;
        }
        catch (Exception e) when (e is ServerUnreachableException or ApiException or IdentityChangedException or IOException or UnauthorizedAccessException)
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
                // A leftover temp file is harmless; nothing reads it.
            }
        }
    }

    private bool RecentlyMissed(string name)
    {
        if (!_failed.TryGetValue(name, out var at)) return false;
        if (_time.GetElapsedTime(at) < MissMemory) return true;

        _failed.TryRemove(name, out _);
        return false;
    }

    // The path comes over the network, so it must be "/" and one plain TMDB file name before it goes into a file name.
    private static string? FileNameOf(string posterPath) =>
        posterPath is ['/', .. var file] && TmdbImageName.IsValid(file) ? file : null;
}
