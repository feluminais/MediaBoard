using System.Diagnostics;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Players;
using MediaBoard.Core.Remote;
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

    /// <summary>
    /// Saved progress changed in a way the pages show: a session ended or was stopped (its last position is saved),
    /// a file became watched, or <see cref="AnswerFinished"/> recorded an answer. Raised on a background thread.
    /// </summary>
    event Action? ProgressChanged;

    /// <summary>The name of the player running the current file, or null when nothing plays.</summary>
    string? CurrentPlayer { get; }

    /// <summary>Asks the current player where it is and saves that position; the file keeps playing.</summary>
    Task SavePositionAsync(CancellationToken ct);

    /// <summary>
    /// Saves the current file's position and ends its session. <paramref name="closePlayer"/> false leaves the
    /// player running, and from then on nothing it does is saved.
    /// </summary>
    Task StopAsync(bool closePlayer, CancellationToken ct);
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

    private readonly IPlaybackSource _source;
    private readonly IProgressSink _progress;
    private readonly PlayerDetector _detector;
    private readonly SettingsStore _settings;
    private readonly ActivityHub _hub;
    private readonly Func<PlayerInfo, IPlayerSession> _createSession;

    private readonly SemaphoreSlim _playGate = new(1, 1);
    private readonly Lock _stateGate = new();
    private Playing? _current;

    // A session on its way out, still waiting for its final position: a stream that fails now must still stop that save.
    private Playing? _stopping;

    // A launch-only program's locator after it exited. Some hand the file to a running instance and exit at once, so its
    // exit says nothing about whether the locator is still being read. The next Play or the end of the app gives it back.
    private string? _lingering;

    public PlaybackService(
        IPlaybackSource source,
        IProgressSink progress,
        PlayerDetector detector,
        SettingsStore settings,
        ActivityHub hub,
        Func<PlayerInfo, IPlayerSession>? createSession = null)
    {
        _source = source;
        _progress = progress;
        _detector = detector;
        _settings = settings;
        _hub = hub;
        _createSession = createSession ?? PlayerSessionFactory.Create;
    }

    public event Action<int, string>? AskFinished;

    public event Action? ProgressChanged;

    public string? CurrentPlayer
    {
        get
        {
            lock (_stateGate) return _current?.PlayerName;
        }
    }

    public async Task PlayAsync(int fileId, bool fromStart, CancellationToken ct)
    {
        await _playGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PlayCoreAsync(fileId, fromStart, ct).ConfigureAwait(false);
        }
        catch (ServerUnreachableException)
        {
            _hub.Log(ActivityLevel.Warning, "Can't reach the server", "Their PC may be off or asleep, or sharing is off");
        }
        catch (StreamUnavailableException)
        {
            _hub.Log(ActivityLevel.Warning, "Couldn't start the stream", "Try again");
        }
        catch (Exception e) when (e is ApiException or IdentityChangedException)
        {
            // Signed out and a new certificate each have their own panel. This is for whoever pressed Play.
            Trace.TraceWarning($"Asking for a file failed: {e.GetType().Name}");
            _hub.Log(ActivityLevel.Warning, "Couldn't open this file");
        }
        finally
        {
            _playGate.Release();
        }
    }

    /// <summary>
    /// Follows a player the viewer started without MediaBoard (see <see cref="IAttachableSession"/>): from then on the
    /// library files it plays keep their progress, as if MediaBoard had launched them. Returns false, and disposes
    /// the session, when a session already runs or no player answers.
    /// </summary>
    public async Task<bool> AttachAsync(IAttachableSession session, CancellationToken ct)
    {
        await _playGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool busy;
            lock (_stateGate) busy = _current is not null;
            if (busy)
            {
                await DisposeQuietlyAsync(session).ConfigureAwait(false);
                return false;
            }

            var playing = new Playing(null, "", session.PlayerName, session);
            session.Event += e => OnEvent(playing, e);

            var attached = false;
            try
            {
                attached = await session.AttachAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await EndAsync(playing).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.TraceWarning($"{session.PlayerName} could not be followed: {ex}");
            }

            if (!attached)
            {
                await EndAsync(playing).ConfigureAwait(false);
                return false;
            }

            lock (_stateGate)
            {
                lock (playing.Gate)
                {
                    if (playing.Final) return true; // the player was gone again before this point
                }

                _current = playing;
                _hub.SetPlayer(session.PlayerName);
            }

            return true;
        }
        finally
        {
            _playGate.Release();
        }
    }

    public void AnswerFinished(int fileId, bool finished)
    {
        _progress.SetWatched(fileId, finished);
        ProgressChanged?.Invoke();
    }

    public async Task SavePositionAsync(CancellationToken ct)
    {
        await _playGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Playing? playing;
            lock (_stateGate) playing = _current;
            if (playing is null) return;

            var position = await FreshPositionAsync(playing, ct).ConfigureAwait(false);
            SaveAt(playing, position, final: false);
        }
        finally
        {
            _playGate.Release();
        }
    }

    public async Task StopAsync(bool closePlayer, CancellationToken ct)
    {
        await _playGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCurrentAsync(ct, closePlayer).ConfigureAwait(false);
        }
        finally
        {
            _playGate.Release();
        }
    }

    /// <summary>
    /// Ends the session on <paramref name="fileId"/> because its stream failed. A player left without its stream raises no
    /// error: it plays out its buffer, then reports Ended and the file's full length. So from here nothing it says counts,
    /// the position saved before the failure stands, and the player is closed. Doesn't wait for a Play in progress, so the
    /// session stops listening at once. Does nothing unless <paramref name="url"/> is what the session playing that file
    /// started on: an old session's URL can fail late, while the same file plays again on a new one.
    /// </summary>
    /// <param name="fileId">The file whose stream failed.</param>
    /// <param name="url">The URL it failed on.</param>
    /// <param name="reason">What to tell the viewer.</param>
    public async Task AbandonAsync(int fileId, string url, string reason)
    {
        Playing? playing;
        lock (_stateGate) playing = _current ?? _stopping;
        if (playing is null || playing.Locator != url) return;

        lock (playing.Gate)
        {
            if (playing.Final || playing.FileId != fileId) return;

            playing.Final = true;
            _progress.Finished(fileId);
        }

        ClearCurrent(playing);
        _hub.Log(ActivityLevel.Warning, reason);
        await DisposeQuietlyAsync(playing.Session).ConfigureAwait(false);
        Release(playing);
        ProgressChanged?.Invoke();
    }

    /// <summary>Saves the open session's position and closes its player.</summary>
    public async ValueTask DisposeAsync()
    {
        await _playGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            ReleaseLingering();
        }
        finally
        {
            _playGate.Release();
        }
    }

    private async Task PlayCoreAsync(int fileId, bool fromStart, CancellationToken ct)
    {
        var target = await _source.ResolveAsync(fileId, ct).ConfigureAwait(false);
        if (target is null)
        {
            LogFileMissing(null);
            return;
        }

        if (target.Missing)
        {
            LogFileMissing(Path.GetFileName(target.Locator));
            return;
        }

        var player = _detector.Resolve(_settings.Load());
        if (player is null)
        {
            _source.Released(target.Locator);
            return;
        }

        // The old session is closed only now: a file or player that cannot start leaves it playing.
        var stoppedFileId = await StopCurrentAsync(ct).ConfigureAwait(false);

        // Stopping saved a fresher position and may have marked the file watched, so the resolved progress is stale.
        if (stoppedFileId == target.FileId)
        {
            var stale = target;
            try
            {
                target = await _source.ResolveAsync(fileId, ct).ConfigureAwait(false);
            }
            finally
            {
                _source.Released(stale.Locator);
            }

            if (target is null)
            {
                LogFileMissing(null);
                return;
            }

            if (target.Missing)
            {
                LogFileMissing(Path.GetFileName(target.Locator));
                return;
            }
        }

        var saved = target.Saved;
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
            _source.Released(target.Locator);
            LogCouldNotStart(player);
            return;
        }

        var playing = new Playing(fileId, target.Title, player.Name, session, target.Locator)
        {
            Duration = target.DurationS ?? saved?.DurationS ?? 0,
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

        ReleaseLingering();

        try
        {
            await session.StartAsync(target.Locator, startAt, ct).ConfigureAwait(false);
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
        if (e is PlaybackEvent.FileChanged changed)
        {
            OnFileChanged(playing, changed.Path);
            return;
        }

        var end = e switch
        {
            PlaybackEvent.Closed => EndKind.Closed,
            PlaybackEvent.Failed => EndKind.Failed,
            _ => EndKind.None,
        };
        bool markedNow;
        lock (playing.Gate)
        {
            if (playing.Final) return;
            playing.Final = end != EndKind.None;

            var wasMarked = playing.Marked;
            try
            {
                Apply(playing, e);
            }
            catch (Exception ex) when (IsSaveFailure(ex))
            {
                // The player keeps playing; say so once instead of on every position.
                Trace.TraceWarning($"Could not save progress: {ex}");
                if (!playing.SaveFailureLogged)
                {
                    playing.SaveFailureLogged = true;
                    _hub.Log(ActivityLevel.Warning, "Couldn't save progress", "Check that the library database is writable");
                }
            }

            markedNow = playing.Marked && !wasMarked;
        }

        if (end == EndKind.None)
        {
            if (markedNow) ProgressChanged?.Invoke();
            return;
        }

        if (e is PlaybackEvent.Failed failed)
        {
            _hub.Log(ActivityLevel.Warning, failed.Message, "Try again or choose another player in Settings");
        }

        ClearCurrent(playing);
        var lingers = end == EndKind.Closed && !playing.Session.TracksProgress;
        if (lingers) Linger(playing);

        // Not awaited: this runs on the player's event thread, which must not wait on the session.
        _ = Task.Run(async () =>
        {
            await DisposeQuietlyAsync(playing.Session).ConfigureAwait(false);
            if (!lingers) Release(playing);
        });

        ProgressChanged?.Invoke();
        if (end == EndKind.Closed && !playing.Session.TracksProgress && playing.FileId is { } fileId) AskFinished?.Invoke(fileId, playing.Title);
    }

    /// <summary>
    /// The player moved to another file (or closed its file). The file it leaves keeps the position it had reached,
    /// and marked watched when that counts as watched; from here on the positions belong to the new file, or to
    /// nothing when the new file is not in the library.
    /// </summary>
    private void OnFileChanged(Playing playing, string? path)
    {
        lock (playing.Gate)
        {
            // Under the gate on the player's event thread: the source must answer without blocking.
            var next = path is null ? null : _source.FindByLocator(path);
            if (playing.Final || next?.FileId == playing.FileId) return;

            try
            {
                SaveLastPosition(playing);
            }
            catch (Exception ex) when (IsSaveFailure(ex))
            {
                Trace.TraceWarning($"Could not save progress: {ex}");
            }

            Finish(playing);
            Rebind(playing, next);
        }

        ProgressChanged?.Invoke();
    }

    // Called with playing.Gate held.
    private static void Rebind(Playing playing, PlayTarget? target)
    {
        var saved = target?.Saved;
        playing.FileId = target?.FileId;
        playing.Title = target?.Title ?? "";
        playing.Duration = target?.DurationS ?? saved?.DurationS ?? 0;
        playing.LastPosition = null;
        playing.EndedEarly = false;
        playing.Marked = saved?.Watched ?? false;
        // The player starts a file it opened itself at the start, so a saved position stays until it is passed.
        playing.CanSave = saved is null || saved.PositionS <= RealPositionS;
    }

    // A local database or file failed, or a remote sink could not reach its server. The player keeps playing.
    private static bool IsSaveFailure(Exception ex) => ex is SqliteException or IOException or HttpRequestException;

    // Called with playing.Gate held. Closed and Failed are already marked final, so Closed's save is the last one.
    private void Apply(Playing playing, PlaybackEvent e)
    {
        switch (e)
        {
            case PlaybackEvent.Loaded loaded:
                if (loaded.Duration > TimeSpan.Zero) playing.Duration = loaded.Duration.TotalSeconds;
                break;

            case PlaybackEvent.Playing:
                playing.EndedEarly = false;
                break;

            case PlaybackEvent.Position position:
                // MPC-HC's answer to asking for the position comes this way too, so the claim would get in here.
                if (playing.EndedEarly) break;

                playing.LastPosition = position.At.TotalSeconds;
                if (playing.LastPosition > RealPositionS) playing.CanSave = true;
                SaveLastPosition(playing);
                break;

            case PlaybackEvent.Paused:
                SaveLastPosition(playing);
                if (playing.FileId is { } paused) _progress.Paused(paused);
                break;

            case PlaybackEvent.Ended:
                SaveLastPosition(playing);

                // A player that lost its stream without anyone hearing of it now claims the whole file, for as long as it
                // sits here. Without a position and a length there's no telling how far it got, so that proves nothing.
                var canLose = _source.PlayerCanLoseItsStream;
                var unmeasured = playing.LastPosition is null || playing.Duration <= 0;
                var counts = WatchRules.EndedCountsAsWatched(playing.LastPosition, playing.Duration) && !(canLose && unmeasured);
                if (counts && !playing.Marked) MarkWatched(playing);

                playing.EndedEarly = !counts && canLose;
                Finish(playing);
                break;

            case PlaybackEvent.Closed:
                SaveLastPosition(playing);
                Finish(playing);
                break;

            // Whatever the player reported before failing is not progress, so nothing is saved.
            case PlaybackEvent.Failed:
                Finish(playing);
                break;
        }
    }

    // Called with playing.Gate held.
    private void Finish(Playing playing)
    {
        if (playing.FileId is { } fileId) _progress.Finished(fileId);
    }

    private void SaveLastPosition(Playing playing)
    {
        if (playing.FileId is not { } fileId || playing.LastPosition is not { } position || !playing.CanSave) return;

        _progress.Save(fileId, position, playing.Duration);
        if (!playing.Marked && WatchRules.IsWatched(position, playing.Duration)) MarkWatched(playing);
    }

    private void MarkWatched(Playing playing)
    {
        if (playing.FileId is not { } fileId) return;

        _progress.SetWatched(fileId, true);
        playing.Marked = true;
    }

    /// <summary>
    /// Saves the current session's last position (asking the player for a fresher one) and ends it: closes the
    /// player, or with <paramref name="closePlayer"/> false leaves it running.
    /// </summary>
    /// <returns>The file the stopped session was on, or null when nothing ran or the player had no library file.</returns>
    private async Task<int?> StopCurrentAsync(CancellationToken ct, bool closePlayer = true)
    {
        Playing? playing;
        lock (_stateGate)
        {
            playing = _current;
            _current = null;
            _stopping = playing;
            if (playing is not null) _hub.SetPlayer(null);
        }

        if (playing is null) return null;

        var position = await FreshPositionAsync(playing, ct).ConfigureAwait(false);
        var saved = SaveAt(playing, position, final: true);
        lock (_stateGate) _stopping = null;
        if (saved) ProgressChanged?.Invoke();

        int? fileId;
        lock (playing.Gate) fileId = playing.FileId;

        if (!closePlayer) playing.Session.Detach();
        await DisposeQuietlyAsync(playing.Session).ConfigureAwait(false);
        Release(playing);
        return fileId;
    }

    /// <summary>Where the player is now, or null when it can't tell (the last reported position then stands).</summary>
    private static async Task<TimeSpan?> FreshPositionAsync(Playing playing, CancellationToken ct)
    {
        if (!playing.Session.TracksProgress) return null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PositionTimeout);
            return await playing.Session.GetPositionAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The player may be gone already; the last reported position stands.
            Trace.TraceInformation($"No final position from {playing.Title}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Saves <paramref name="position"/> (or the last reported one); <paramref name="final"/> ends the session's saving.</summary>
    /// <returns>False when the session had already ended.</returns>
    private bool SaveAt(Playing playing, TimeSpan? position, bool final)
    {
        lock (playing.Gate)
        {
            if (playing.Final) return false;
            playing.Final = final;
            try
            {
                if (position is { } at && !playing.EndedEarly)
                {
                    playing.LastPosition = at.TotalSeconds;
                    if (at.TotalSeconds > RealPositionS) playing.CanSave = true;
                }

                SaveLastPosition(playing);
                if (final) Finish(playing);
            }
            catch (Exception ex) when (IsSaveFailure(ex))
            {
                Trace.TraceWarning($"Could not save progress: {ex}");
                _hub.Log(ActivityLevel.Warning, "Couldn't save progress", "Check that the library database is writable");
            }

            return true;
        }
    }

    /// <summary>Gives up on a session that never got going.</summary>
    private async Task EndAsync(Playing playing)
    {
        lock (playing.Gate) playing.Final = true;
        ClearCurrent(playing);
        await DisposeQuietlyAsync(playing.Session).ConfigureAwait(false);
        Release(playing);
    }

    private void Release(Playing playing)
    {
        if (playing.Locator is { } locator) _source.Released(locator);
    }

    private void Linger(Playing playing)
    {
        string? older;
        lock (_stateGate)
        {
            older = _lingering;
            _lingering = playing.Locator;
        }

        if (older is not null) _source.Released(older);
    }

    private void ReleaseLingering()
    {
        string? lingering;
        lock (_stateGate)
        {
            lingering = _lingering;
            _lingering = null;
        }

        if (lingering is not null) _source.Released(lingering);
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

    private sealed class Playing(int? fileId, string title, string playerName, IPlayerSession session, string? locator = null)
    {
        public readonly Lock Gate = new();

        public string PlayerName => playerName;
        public IPlayerSession Session => session;

        // What the session started on, and gives back at its end. Null for a player it attached to.
        public string? Locator => locator;

        // Everything below is guarded by Gate. The file changes when the player moves on to another one; it is
        // null while the player has a file that is not in the library, or none.
        public int? FileId = fileId;
        public string Title = title;
        public double Duration;
        public double? LastPosition;

        // Set by an Ended that didn't count, when the player can lose its stream. Until it plays again, it isn't believed.
        public bool EndedEarly;
        public bool CanSave;
        public bool Marked;
        public bool Final;
        public bool SaveFailureLogged;
    }
}
