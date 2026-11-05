using Avalonia.Threading;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Remote;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>
/// Runs a view model's library queries on the thread pool and applies the result on the UI thread, so a slow query
/// (a large library, or a folder on a drive that takes seconds to answer) never freezes the window. Give each view
/// model its own instance for each thing it loads; a newer <see cref="RunAsync{T}"/> on the same instance makes the
/// older result stale, and a stale result is dropped. The older call's task still completes only once the newest one
/// has, so awaiting any run means the newest data is shown. A failed query is logged once with a hint (a repeat of the
/// same failure is not logged again until a query succeeds) and never reaches the dispatcher as an exception. An
/// apply step that throws is logged the same way, since callers do not await the task.
/// </summary>
/// <param name="hub">Where failures are logged.</param>
/// <param name="what">What loads, for the log.</param>
/// <param name="failed">Called on the UI thread each time the newest query fails, or null.</param>
/// <param name="capabilities">Gives the hint for a failure and, for a viewer, the connection; null for this PC's library.</param>
public sealed class BackgroundQuery(ActivityHub hub, string what, Action? failed = null, AppCapabilities? capabilities = null)
{
    public const string FailureHint = "Restart MediaBoard to try again.";

    private readonly string _hint = capabilities?.FailureHint ?? FailureHint;
    private int _generation;
    private string? _lastFailure;
    private Task _latest = Task.CompletedTask;

    /// <summary>Call on the UI thread. Makes the result of a query that is still running stale, so it is dropped.</summary>
    public void Discard()
    {
        Dispatcher.UIThread.VerifyAccess();
        _generation++;
        _latest = Task.CompletedTask; // nothing newer to wait for
    }

    /// <summary>
    /// Call on the UI thread. Returns at once; the task completes after <paramref name="apply"/> ran, or, when a newer
    /// run replaced this one, after that newer run completed. Only <see cref="Discard"/> ends a run with nothing applied.
    /// </summary>
    public Task RunAsync<T>(Func<T> query, Action<T> apply)
    {
        Dispatcher.UIThread.VerifyAccess();
        var run = RunCoreAsync(++_generation, query, apply);
        _latest = run;
        return run;
    }

    private async Task RunCoreAsync<T>(int generation, Func<T> query, Action<T> apply)
    {
        T result;
        try
        {
            result = await Task.Run(query); // resumes on the UI thread
        }
        catch (Exception e)
        {
            if (generation != _generation)
            {
                await JoinLatestAsync();
                return;
            }

            failed?.Invoke();
            if (TheShellSaysSo(e) || e.Message == _lastFailure) return;
            _lastFailure = e.Message;
            hub.Log(ActivityLevel.Error, $"Couldn't load {what}: {e.Message}", _hint);
            return;
        }

        if (generation != _generation)
        {
            await JoinLatestAsync();
            return;
        }

        _lastFailure = null;
        try
        {
            apply(result);
        }
        catch (Exception e)
        {
            hub.Log(ActivityLevel.Error, $"Couldn't load {what}: {e.Message}", _hint);
        }
    }

    // A viewer's status bar and connection panel say this already, but only if the monitor heard. A timeout, an error with
    // no code and unreadable JSON all arrive as unreachable with the monitor still Connected, and then this is all there is.
    private bool TheShellSaysSo(Exception e) => capabilities?.Connection is { } link
        && (e is IdentityChangedException or ApiException { Code: ApiErrorCode.Unauthorized }
            || (e is ServerUnreachableException && link.State != ConnectionState.Connected));

    // A stale run is never the latest (a newer RunAsync or Discard replaced it), so this never waits on itself. The
    // latest can be replaced again while we wait, so wait until it stops changing.
    private async Task JoinLatestAsync()
    {
        Task latest;
        do
        {
            latest = _latest;
            await latest; // never throws: RunCoreAsync catches everything
        }
        while (!ReferenceEquals(latest, _latest));
    }
}
