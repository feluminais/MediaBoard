using Avalonia.Threading;
using MediaBoard.Core.Activity;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>
/// Runs a view model's library queries on the thread pool and applies the result on the UI thread, so a slow query
/// (a large library, or a folder on a drive that takes seconds to answer) never freezes the window. Give each view
/// model its own instance for each thing it loads; a newer <see cref="RunAsync{T}"/> on the same instance makes the
/// older result stale, and a stale result is dropped. A failed query is logged once with a hint (a repeat of the
/// same failure is not logged again until a query succeeds) and never reaches the dispatcher as an exception. An
/// apply step that throws is logged the same way, since callers do not await the task.
/// </summary>
public sealed class BackgroundQuery(ActivityHub hub, string what)
{
    public const string FailureHint = "Restart MediaBoard to try again.";

    private int _generation;
    private string? _lastFailure;

    /// <summary>Call on the UI thread. Makes the result of a query that is still running stale, so it is dropped.</summary>
    public void Discard() => _generation++;

    /// <summary>Call on the UI thread. Returns at once; the task completes after <paramref name="apply"/> ran or was skipped.</summary>
    public async Task RunAsync<T>(Func<T> query, Action<T> apply)
    {
        Dispatcher.UIThread.VerifyAccess();
        var generation = ++_generation;

        T result;
        try
        {
            result = await Task.Run(query); // resumes on the UI thread
        }
        catch (Exception e)
        {
            if (generation != _generation || e.Message == _lastFailure) return;
            _lastFailure = e.Message;
            hub.Log(ActivityLevel.Error, $"Couldn't load {what}: {e.Message}", FailureHint);
            return;
        }

        if (generation != _generation) return;
        _lastFailure = null;
        try
        {
            apply(result);
        }
        catch (Exception e)
        {
            hub.Log(ActivityLevel.Error, $"Couldn't load {what}: {e.Message}", FailureHint);
        }
    }
}
