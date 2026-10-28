using System.Diagnostics;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Players;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Playback;

public interface IPlaybackService
{
    /// <summary>
    /// Plays a library file in the chosen player, resuming at the saved position unless the file is watched or
    /// <paramref name="fromStart"/> is set. Problems are logged to the activity hub, not thrown.
    /// </summary>
    Task PlayAsync(int fileId, bool fromStart, CancellationToken ct);

    /// <summary>
    /// A launch-only player closed, so nobody knows whether the viewer finished the file: the arguments are the file
    /// that was playing and its title. Raised on a background thread; answer with <see cref="AnswerFinished"/>.
    /// </summary>
    event Action<int, string>? AskFinished;

    /// <summary>Records the viewer's answer to <see cref="AskFinished"/>.</summary>
    void AnswerFinished(int fileId, bool finished);
}

/// <summary>
/// Plays files in external players and keeps the viewer's progress. One file plays at a time: starting another
/// saves and closes the first. Player events arrive on background threads; each session's state is guarded by its
/// own lock, and every save is one short upsert.
/// </summary>
public sealed class PlaybackService : IPlaybackService, IAsyncDisposable
{
    // A position at or below this is the player still sitting at the start, not the viewer's progress.
    private const double RealPositionS = 1;
    private static readonly TimeSpan PositionTimeout = TimeSpan.FromSeconds(2);

    private readonly FileStore _files;
    private readonly ProgressStore _progress;
    private readonly PlayerDetector _detector;
    private readonly SettingsStore _settings;
    private readonly ActivityHub _hub;
    private readonly Func<PlayerInfo, IPlayerSession> _createSession;

    private readonly SemaphoreSlim _playGate = new(1, 1);
    private readonly Lock _stateGate = new();
    private Playing? _current;

    public PlaybackService(
        FileStore files,
        ProgressStore progress,
        PlayerDetector detector,
        SettingsStore settings,
        ActivityHub hub,
        Func<PlayerInfo, IPlayerSession>? createSession = null)
    {
        _files = files;
        _progress = progress;
        _detector = detector;
        _settings = settings;
        _hub = hub;
        _createSession = createSession ?? PlayerSessionFactory.Create;
    }

    public event Action<int, string>? AskFinished;

    public async Task PlayAsync(int fileId, bool fromStart, CancellationToken ct)
    {
        await _playGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PlayCoreAsync(fileId, fromStart, ct).ConfigureAwait(false);
        }
        finally
        {
            _playGate.Release();
        }
    }

    public void AnswerFinished(int fileId, bool finished) => _progress.SetWatched(fileId, finished);

    /// <summary>Saves the open session's position and closes its player.</summary>
    public async ValueTask DisposeAsync()
    {
        await _playGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _playGate.Release();
        }
    }

    private async Task PlayCoreAsync(int fileId, bool fromStart, CancellationToken ct)
    {
        var file = _files.Get(fileId);
        if (file is null)
        {
            LogFileMissing(null);
            return;
        }

        var path = _files.FullPath(file);
        if (file.Missing || !File.Exists(path))
        {
            LogFileMissing(Path.GetFileName(file.RelPath));
            return;
        }

        var player = _detector.Resolve(_settings.Load());
        if (player is null) return;

        // The old session is closed only now: a file or player that cannot start leaves it playing.
        await StopCurrentAsync(ct).ConfigureAwait(false);

        var saved = _progress.Get(fileId);
        var startAt = !fromStart && saved is { Watched: false, PositionS: > 0 }
            ? TimeSpan.FromSeconds(saved.PositionS)
            : (TimeSpan?)null;

        IPlayerSession session;
        try
        {
            session = _createSession(player);
        }
        catch (ArgumentException)
        {
            LogCouldNotStart(player);
            return;
        }

        var title = string.IsNullOrWhiteSpace(file.Parsed.Title) ? Path.GetFileNameWithoutExtension(file.RelPath) : file.Parsed.Title;
        var playing = new Playing(fileId, title, session)
        {
            Duration = file.DurationS ?? saved?.DurationS ?? 0,
            Marked = saved?.Watched ?? false,
            // With a saved position, the player's first positions may still be 0 (the resume seek has not landed).
            CanSave = saved is null || saved.PositionS <= RealPositionS,
        };
        session.Event += e => OnEvent(playing, e);

        lock (_stateGate)
        {
            _current = playing;
            _hub.SetPlayer(player.Name);
        }

        try
        {
            await session.StartAsync(path, startAt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await EndAsync(playing).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"{player.Name} did not start: {ex}");
            LogCouldNotStart(player);
            await EndAsync(playing).ConfigureAwait(false);
        }
    }

    private void OnEvent(Playing playing, PlaybackEvent e)
    {
        var end = e switch
        {
            PlaybackEvent.Closed => EndKind.Closed,
            PlaybackEvent.Failed => EndKind.Failed,
            _ => EndKind.None,
        };
        lock (playing.Gate)
        {
            if (playing.Final) return;
            playing.Final = end != EndKind.None;

            try
            {
                Apply(playing, e);
            }
            catch (Exception ex) when (ex is SqliteException or IOException)
            {
                // The player keeps playing; say so once instead of on every position.
                Trace.TraceWarning($"Could not save progress: {ex}");
                if (!playing.SaveFailureLogged)
                {
                    playing.SaveFailureLogged = true;
                    _hub.Log(ActivityLevel.Warning, "Couldn't save progress", "Check that the library database is writable");
                }
            }
        }

        if (end == EndKind.None) return;

        if (e is PlaybackEvent.Failed failed)
        {
            _hub.Log(ActivityLevel.Warning, failed.Message, "Try again or choose another player in Settings");
        }

        ClearCurrent(playing);
        // Not awaited: this runs on the player's event thread, which must not wait on the session.
        _ = Task.Run(() => DisposeQuietlyAsync(playing.Session));

        if (end == EndKind.Closed && !playing.Session.TracksProgress) AskFinished?.Invoke(playing.FileId, playing.Title);
    }

    // Called with playing.Gate held. Closed and Failed are already marked final, so Closed's save is the last one.
    private void Apply(Playing playing, PlaybackEvent e)
    {
        switch (e)
        {
            case PlaybackEvent.Loaded loaded:
                if (loaded.Duration > TimeSpan.Zero) playing.Duration = loaded.Duration.TotalSeconds;
                break;

            case PlaybackEvent.Position position:
                playing.LastPosition = position.At.TotalSeconds;
                if (playing.LastPosition > RealPositionS) playing.CanSave = true;
                SaveLastPosition(playing);
                break;

            case PlaybackEvent.Paused:
                SaveLastPosition(playing);
                break;

            case PlaybackEvent.Ended:
                SaveLastPosition(playing);
                if (!playing.Marked && WatchRules.EndedCountsAsWatched(playing.LastPosition, playing.Duration))
                {
                    MarkWatched(playing);
                }

                break;

            case PlaybackEvent.Closed:
                SaveLastPosition(playing);
                break;

            // Failed: whatever the player reported before failing is not progress, so nothing is saved.
        }
    }

    private void SaveLastPosition(Playing playing)
    {
        if (playing.LastPosition is not { } position || !playing.CanSave) return;

        _progress.Save(playing.FileId, position, playing.Duration);
        if (!playing.Marked && WatchRules.IsWatched(position, playing.Duration)) MarkWatched(playing);
    }

    private void MarkWatched(Playing playing)
    {
        _progress.SetWatched(playing.FileId, true);
        playing.Marked = true;
    }

    /// <summary>Saves the current session's last position (asking the player for a fresher one) and closes it.</summary>
    private async Task StopCurrentAsync(CancellationToken ct)
    {
        Playing? playing;
        lock (_stateGate)
        {
            playing = _current;
            _current = null;
        }

        if (playing is null) return;

        TimeSpan? position = null;
        if (playing.Session.TracksProgress)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(PositionTimeout);
                position = await playing.Session.GetPositionAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The player may be gone already; the last reported position stands.
                Trace.TraceInformation($"No final position from {playing.Title}: {ex.Message}");
            }
        }

        lock (playing.Gate)
        {
            if (!playing.Final)
            {
                playing.Final = true;
                try
                {
                    if (position is { } at)
                    {
                        playing.LastPosition = at.TotalSeconds;
                        if (at.TotalSeconds > RealPositionS) playing.CanSave = true;
                    }

                    SaveLastPosition(playing);
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    Trace.TraceWarning($"Could not save progress: {ex}");
                    _hub.Log(ActivityLevel.Warning, "Couldn't save progress", "Check that the library database is writable");
                }
            }
        }

        await DisposeQuietlyAsync(playing.Session).ConfigureAwait(false);
    }

    /// <summary>Gives up on a session that never got going.</summary>
    private async Task EndAsync(Playing playing)
    {
        lock (playing.Gate) playing.Final = true;
        ClearCurrent(playing);
        await DisposeQuietlyAsync(playing.Session).ConfigureAwait(false);
    }

    private void ClearCurrent(Playing playing)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_current, playing)) return;
            _current = null;
            _hub.SetPlayer(null);
        }
    }

    private static async Task DisposeQuietlyAsync(IPlayerSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"Closing a player session failed: {ex}");
        }
    }

    private void LogFileMissing(string? name) =>
        _hub.Log(ActivityLevel.Warning, name is null ? "File missing" : $"File missing: {name}", "Reconnect the drive or rescan the folder");

    private void LogCouldNotStart(PlayerInfo player) =>
        _hub.Log(ActivityLevel.Warning, $"Couldn't start {player.Name}", "Choose another player in Settings");

    private enum EndKind { None, Closed, Failed }

    private sealed class Playing(int fileId, string title, IPlayerSession session)
    {
        public readonly Lock Gate = new();

        public int FileId => fileId;
        public string Title => title;
        public IPlayerSession Session => session;

        // Everything below is guarded by Gate.
        public double Duration;
        public double? LastPosition;
        public bool CanSave;
        public bool Marked;
        public bool Final;
        public bool SaveFailureLogged;
    }
}
