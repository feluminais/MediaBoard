using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Remote;

/// <summary>The newest state of one file that the server hasn't taken yet.</summary>
/// <param name="FileId">The file.</param>
/// <param name="PositionS">Null when only the watched state changed: a position has to be a real one, or it would replace the saved one.</param>
/// <param name="DurationS">0 means unknown.</param>
/// <param name="Watched">Null keeps the stored flag.</param>
/// <param name="At">When this was made.</param>
public sealed record PendingProgress(int FileId, double? PositionS, double DurationS, bool? Watched, DateTimeOffset At);

/// <summary>
/// Keeps a viewer's progress on disk and sends it from the background, so the player's event thread never touches the
/// network and a file finished during a dropout still counts. Holds the newest state per file until the server has it.
/// </summary>
/// <param name="api">Where progress goes.</param>
/// <param name="path">The outbox file.</param>
/// <param name="time">The clock.</param>
public sealed class ProgressOutbox(ApiClient api, string path, TimeProvider time) : IProgressSink, IAsyncDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    // Leaving the app shouldn't wait on a server that's gone. What doesn't get out is still on disk.
    private static readonly TimeSpan LastSendLimit = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private readonly Dictionary<int, PendingProgress> _waiting = [];
    private readonly SemaphoreSlim _sending = new(1, 1);

    // True asks for the keep-alive too. Bounded, because a timer that fires through an outage must not queue up
    // rounds, and a dropped one only waits for the next tick.
    private readonly Channel<bool> _rounds = Channel.CreateBounded<bool>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();

    private ITimer? _timer;
    private Task? _loop;
    private PendingProgress? _last;
    private int? _paused;
    private bool _read;
    private bool _readFailed;
    private bool _writeFailed;
    private int _disposed;

    /// <summary>Starts the background sends. The first is one interval away.</summary>
    public void Start()
    {
        // On the pool: called on a UI thread, the loop's awaits would come back to it for good.
        _loop = Task.Run(() => RunAsync(_stop.Token));
        _timer = time.CreateTimer(_ => _rounds.Writer.TryWrite(true), null, Every, Every);
    }

    public void Save(int fileId, double positionS, double durationS)
    {
        lock (_gate)
        {
            ReadFile();
            _waiting.TryGetValue(fileId, out var earlier);
            _last = new PendingProgress(fileId, positionS, durationS, earlier?.Watched, time.GetUtcNow());
            _waiting[fileId] = _last;
            Write();
        }
    }

    public void SetWatched(int fileId, bool watched)
    {
        lock (_gate)
        {
            ReadFile();
            _waiting.TryGetValue(fileId, out var earlier);

            // Unwatched forgets the position, as it does locally, and only the watched route does that on the server. With
            // no row there yet, a beat would be stored after all, so the position goes from here too.
            _waiting[fileId] = new PendingProgress(fileId, watched ? earlier?.PositionS : null, earlier?.DurationS ?? 0, watched, time.GetUtcNow());
            if (!watched && _last?.FileId == fileId) _last = null;
            Write();
        }
    }

    public void Paused(int fileId)
    {
        lock (_gate) _paused = fileId;
        _rounds.Writer.TryWrite(false);
    }

    public void Finished(int fileId)
    {
        lock (_gate)
        {
            if (_paused == fileId) _paused = null;
        }

        _rounds.Writer.TryWrite(false);
    }

    /// <summary>What hasn't reached the server for this file, or null. A replay after a stop can run before the send.</summary>
    public PendingProgress? Waiting(int fileId)
    {
        lock (_gate)
        {
            ReadFile();
            return _waiting.GetValueOrDefault(fileId);
        }
    }

    /// <summary>Sends what's waiting. A server that can't be reached keeps it for the next time.</summary>
    public Task FlushAsync(CancellationToken ct) => SendAsync(keepAlive: false, ct);

    /// <summary>Stops the background sends and tries once more.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        if (_timer is not null) await _timer.DisposeAsync();

        await _stop.CancelAsync();
        if (_loop is not null) await _loop;

        using var limit = new CancellationTokenSource(LastSendLimit, time);
        try
        {
            await FlushAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            // On disk for the next start.
        }

        _rounds.Writer.TryComplete();
        _stop.Dispose();
        _sending.Dispose();
    }

    private static string Route(string format, PendingProgress entry) => string.Format(CultureInfo.InvariantCulture, format, entry.FileId);

    /// <summary>
    /// Called with _gate held, before anything uses the entries, and again until the file has been read. A file that's only
    /// locked still holds entries, so <see cref="Write"/> waits for this: a corrupt one holds nothing and may be replaced.
    /// </summary>
    private void ReadFile()
    {
        if (_read) return;

        try
        {
            if (File.Exists(path))
            {
                using var file = File.OpenRead(path);
                var stored = JsonSerializer.Deserialize<List<PendingProgress?>>(file, ApiJson.Options);
                if (stored is null || stored.Contains(null)) throw new JsonException();

                // What this run saved while the file was locked may be the newer.
                foreach (var entry in stored.OfType<PendingProgress>())
                {
                    if (!_waiting.TryGetValue(entry.FileId, out var mine) || mine.At < entry.At) _waiting[entry.FileId] = entry;
                }
            }

            _read = true;
        }
        catch (JsonException e)
        {
            _read = true;

            // The type alone: a message can hold the path.
            Trace.TraceWarning($"The saved progress outbox can't be understood and starts empty: {e.GetType().Name}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (_readFailed) return;

            _readFailed = true;
            Trace.TraceWarning($"The saved progress outbox can't be opened yet: {e.GetType().Name}");
        }
    }

    // One reader, so a pause during a slow round waits for it instead of sending the same entry alongside it.
    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var keepAlive in _rounds.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await SendAsync(keepAlive, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Nothing awaits this loop, and whatever failed may not fail next time.
                    Trace.TraceWarning($"Sending progress failed: {e.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
    }

    private async Task SendAsync(bool keepAlive, CancellationToken ct)
    {
        await _sending.WaitAsync(ct);
        try
        {
            List<PendingProgress> batch;
            PendingProgress? beat;
            lock (_gate)
            {
                ReadFile();
                batch = [.. _waiting.Values];

                // A paused stream sends no bytes, and the server ends one after 30 s without a report. It counts any report
                // but keeps only the newest, so this one is dated to lose. A real one would be a write: it could put back
                // what the viewer just unwatched, mark the file watched again or beat another device's newer save.
                beat = keepAlive && _last is { } last && last.FileId == _paused && !_waiting.ContainsKey(last.FileId)
                    ? last with { Watched = null, At = DateTimeOffset.UnixEpoch }
                    : null;
            }

            foreach (var entry in batch)
            {
                try
                {
                    await PutAsync(entry, ct);
                }
                catch (ApiException e) when (e.Code is ApiErrorCode.NotFound or ApiErrorCode.BadRequest)
                {
                    // Gone from what this viewer can reach, or never sound. Asking again can't change the answer.
                }

                Forget(entry);
            }

            if (beat is not null) await PutAsync(beat, ct);
        }
        catch (Exception e) when (e is ApiException or ServerUnreachableException or IdentityChangedException)
        {
            // Everything unsent stays for the next round, and the connection state says why.
        }
        finally
        {
            _sending.Release();
        }
    }

    private Task PutAsync(PendingProgress entry, CancellationToken ct)
    {
        return entry.PositionS is { } position
            ? api.SendAsync(HttpMethod.Put, Route("files/{0}/progress", entry), new ProgressEntry(position, entry.DurationS, entry.Watched, entry.At), ct)
            : api.SendAsync(HttpMethod.Put, Route("files/{0}/watched", entry), new WatchedRequest(entry.Watched), ct);
    }

    // A save that came in during the send is newer than what was sent, so it stays.
    private void Forget(PendingProgress sent)
    {
        lock (_gate)
        {
            if (_waiting.GetValueOrDefault(sent.FileId) != sent) return;

            _waiting.Remove(sent.FileId);
            Write();
        }
    }

    // Called with _gate held. Writes beside the target and swaps, so a crash never leaves a half-written file. A disk that
    // refuses costs only the protection against a restart: the entry is still in memory, on its way to the server.
    private void Write()
    {
        if (!_read) return;

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temp = path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_waiting.Values, ApiJson.Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (_writeFailed) return;

            _writeFailed = true;
            Trace.TraceWarning($"The progress outbox can't be written: {e.GetType().Name}");
        }
    }
}
