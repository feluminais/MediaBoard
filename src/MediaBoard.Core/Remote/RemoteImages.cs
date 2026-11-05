using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace MediaBoard.Core.Remote;

/// <summary>
/// Poster files kept on disk for a viewer, fetched from the server once and read from disk after that. It has the shape
/// of <c>ImageCache</c> and keeps its files in a folder of its own, so nothing here mixes with images this PC fetched itself.
/// </summary>
/// <param name="client">The server to fetch from.</param>
/// <param name="imagesDir">The images folder (<c>AppPaths.ImagesDir</c>); the cache lives in a folder below it.</param>
public sealed partial class RemoteImages(ApiClient client, string imagesDir)
{
    private readonly string _dir = Path.GetFullPath(Path.Combine(imagesDir, "remote"));
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _downloads = new();
    private readonly ConcurrentDictionary<string, bool> _failed = new();

    /// <summary>
    /// The local file for a TMDB poster path such as <c>/abc123.jpg</c>, fetching it when it is missing and the server can
    /// be reached. Returns null when the file is missing and can't be had now: the server is out of reach, it has no such
    /// image (not asked again during this run), or the path is not a plain TMDB file name. Cancelling stops the wait, not a
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
        if (_failed.ContainsKey(name)) return null;

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
            var bytes = await client.GetBytesAsync($"images/{width}/{file}", CancellationToken.None).ConfigureAwait(false);
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);

            // A crash before this line leaves only the temp file, never a half image under the final name.
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (ApiException e) when (e.Code == ApiErrorCode.NotFound)
        {
            _failed[name] = true;
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

    // The path comes over the network, so it must be "/" and one plain TMDB file name before it goes into a file name.
    private static string? FileNameOf(string posterPath) =>
        posterPath is ['/', .. var file] && TmdbFileName().IsMatch(file) ? file : null;

    // \z, not $: a trailing newline would pass $. The server's route takes the same names.
    [GeneratedRegex(@"^[A-Za-z0-9]+\.(?:jpg|png)\z", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TmdbFileName();
}
