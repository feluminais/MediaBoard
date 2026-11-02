namespace MediaBoard.Desktop.ViewModels;

/// <summary>
/// Turns a TMDB poster path into a local file, off the UI thread and a few at a time. A card asks only once it is on
/// screen, so a library of thousands never queues thousands of downloads; a card that scrolls away cancels its request
/// and that request leaves the queue unless it already started.
/// </summary>
public sealed class PosterLoader
{
    /// <summary>The width the image cache holds for cards and rows.</summary>
    public const int CardWidth = 342;

    /// <summary>The width TMDB serves episode stills at (stills have no 342).</summary>
    public const int StillWidth = 300;

    private readonly Func<string, int, CancellationToken, Task<string?>> _source;
    private readonly SemaphoreSlim _slots;

    /// <param name="source">The image cache's <c>GetAsync</c>: poster path, width, token, local path or null.</param>
    public PosterLoader(Func<string, int, CancellationToken, Task<string?>> source, int maxConcurrent = 6)
    {
        _source = source;
        _slots = new SemaphoreSlim(maxConcurrent);
    }

    /// <summary>The local file, or null when there is none to show (offline, no such poster, a failed read). Throws only <see cref="OperationCanceledException"/>.</summary>
    public Task<string?> LoadAsync(string posterPath, CancellationToken ct) => LoadAsync(posterPath, CardWidth, ct);

    /// <summary>The local file of an episode still, with the same rules as <see cref="LoadAsync(string, CancellationToken)"/>.</summary>
    public Task<string?> LoadStillAsync(string stillPath, CancellationToken ct) => LoadAsync(stillPath, StillWidth, ct);

    private async Task<string?> LoadAsync(string path, int width, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _source(path, width, ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null; // the image cache never throws for a network problem; anything else just means no poster
        }
        finally
        {
            _slots.Release();
        }
    }
}
