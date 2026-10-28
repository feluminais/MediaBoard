using System.Collections.Concurrent;
using System.Diagnostics;

namespace MediaBoard.Core.Players;

/// <summary>
/// Delivers a session's events to its handler one at a time and in order, with no lock held while a handler runs.
/// Finality is decided under a lock; handlers run outside it, so a handler that waits on another thread (say, one
/// busy in <c>DisposeAsync</c>) cannot deadlock it.
/// </summary>
/// <remarks>
/// <see cref="PlaybackEvent.Closed"/> and <see cref="PlaybackEvent.Failed"/> are final: nothing is raised after
/// them. One thread at a time delivers queued events in queue order; a thread that finds delivery in progress leaves
/// its event to that thread, and a handler that raises again does not recurse. A handler that throws is traced and
/// delivery carries on, so the event it was handed to deliver (often the final Closed or Failed) is never stranded.
/// </remarks>
internal sealed class EventPump
{
    private readonly string _source;
    private readonly Func<Action<PlaybackEvent>?> _handler;
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<PlaybackEvent> _outbox = new();
    private volatile bool _finished;
    private volatile bool _stopped;
    private int _delivering;

    /// <param name="source">Names the session in the trace of a failing handler.</param>
    /// <param name="handler">The handler to invoke, read at delivery time (the session's event field).</param>
    public EventPump(string source, Func<Action<PlaybackEvent>?> handler)
    {
        _source = source;
        _handler = handler;
    }

    /// <summary>Whether a final event was raised or the pump was stopped.</summary>
    public bool IsFinished => _finished;

    /// <summary>Queues the event and delivers it, unless the pump is finished.</summary>
    public void Raise(PlaybackEvent e)
    {
        lock (_gate)
        {
            if (_finished) return;
            if (e is PlaybackEvent.Closed or PlaybackEvent.Failed) _finished = true;
            _outbox.Enqueue(e);
        }

        Deliver();
    }

    /// <summary>Raises nothing more and drops events still queued; one already being delivered may still arrive.</summary>
    public void Stop()
    {
        _stopped = true;
        lock (_gate) _finished = true;
    }

    private void Deliver()
    {
        while (!_outbox.IsEmpty && Interlocked.CompareExchange(ref _delivering, 1, 0) == 0)
        {
            try
            {
                while (_outbox.TryDequeue(out var e))
                {
                    if (_stopped)
                    {
                        _outbox.Clear();
                        break;
                    }

                    try
                    {
                        _handler()?.Invoke(e);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError($"{_source} session: a {e.GetType().Name} handler failed: {ex}");
                    }
                }
            }
            finally
            {
                // A full fence, so the IsEmpty check that follows cannot be reordered before the release: an event
                // queued by a thread whose CompareExchange saw 1 is then always picked up here.
                Interlocked.Exchange(ref _delivering, 0);
            }
        }
    }
}
