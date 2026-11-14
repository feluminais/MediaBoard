using Avalonia.Threading;
using CozyCouch.Core.Activity;
using CozyCouch.Core.Playback;

namespace CozyCouch.Desktop.ViewModels;

/// <summary>
/// Starts files in the player for every page and answers a launch-only player's "did you finish?" question. Starting
/// runs on the thread pool (finding the player and the file can touch a slow drive), and a start while another one is
/// still starting is ignored, so a double click opens one player.
/// </summary>
public sealed class PlaybackLauncher
{
    private readonly IPlaybackService _playback;
    private readonly ActivityHub _hub;
    private readonly Func<string, Task<bool?>> _askFinished;
    private int _starting;

    /// <param name="askFinished">Shows "Did you finish ...?" and answers yes or no; null when the viewer closed it without answering.</param>
    public PlaybackLauncher(IPlaybackService playback, ActivityHub hub, Func<string, Task<bool?>> askFinished)
    {
        _playback = playback;
        _hub = hub;
        _askFinished = askFinished;
        playback.AskFinished += OnAskFinished;
    }

    /// <summary>Call on the UI thread. Resumes the file where the viewer left it. A failed start is logged, not thrown.</summary>
    public async Task PlayAsync(int fileId)
    {
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0) return;

        try
        {
            await Task.Run(() => _playback.PlayAsync(fileId, fromStart: false, CancellationToken.None));
        }
        catch (Exception e)
        {
            _hub.Log(ActivityLevel.Error, $"Couldn't start playback: {e.Message}");
        }
        finally
        {
            Volatile.Write(ref _starting, 0);
        }
    }

    // The service names the file that was playing. Raised on a player thread.
    private void OnAskFinished(int fileId, string title)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (await _askFinished(title) is { } finished) await Task.Run(() => _playback.AnswerFinished(fileId, finished));
            }
            catch (Exception e)
            {
                _hub.Log(ActivityLevel.Warning, $"Couldn't save your answer: {e.Message}");
            }
        });
    }
}
