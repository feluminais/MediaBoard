using System.Diagnostics;

namespace MediaBoard.Core.Remote;

public enum ConnectionState { Connecting, Connected, Unreachable, SignedOut, IdentityChanged, VersionMismatch }

/// <summary>What a request learned about the connection. <see cref="ApiClient"/> reports these.</summary>
public enum ConnectionEvent
{
    /// <summary>The events connection is open.</summary>
    Listening,

    /// <summary>No address could be connected to.</summary>
    Unreachable,
    SignedOut,
    IdentityChanged,
}

/// <summary>
/// Holds the one events connection and says what state the link to the server is in. Trying again can't help after a
/// sign-out, a new certificate or a version mismatch, so those wait for <see cref="Retry"/>; anything else retries by itself.
/// </summary>
/// <param name="client">
/// The client to listen through. Its report goes to <see cref="Report"/>, so the app makes the client first with a report
/// that reaches this monitor once it exists.
/// </param>
/// <param name="time">The clock, or null for the real one.</param>
public sealed class ConnectionMonitor(ApiClient client, TimeProvider? time = null)
{
    private const string SignedOutEvent = "signed-out";

    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private ConnectionState _state = ConnectionState.Connecting;
    private int? _serverVersion;
    private CancellationTokenSource? _hold;
    private TaskCompletionSource _retry = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>On whichever thread noticed.</summary>
    public event Action? Changed;

    /// <summary>
    /// The server's events by name, on the monitor's own thread. This is the only way to hear them, since a device gets
    /// one connection. <c>signed-out</c> goes to <see cref="State"/> instead.
    /// </summary>
    public event Action<string>? EventReceived;

    public ConnectionState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    /// <summary>The API version the server last gave, or null before it has answered. It says which side is behind on a mismatch.</summary>
    public int? ServerVersion
    {
        get
        {
            lock (_gate) return _serverVersion;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var wait = FirstWait;
        while (!ct.IsCancellationRequested)
        {
            var retry = RetryAsked();
            var started = _time.GetTimestamp();
            await HoldAsync(ct);

            // Only a connection that lasted clears the back-off: two loops ending each other connect fine every time.
            if (_time.GetElapsedTime(started) >= LongestWait) wait = FirstWait;

            var retried = await WaitAsync(IsFinal(State) ? Timeout.InfiniteTimeSpan : wait, retry, ct);
            wait = retried ? FirstWait : TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LongestWait.Ticks));
        }
    }

    /// <summary>Tries again now, unless a connection is up or on its way.</summary>
    public void Retry()
    {
        TaskCompletionSource asked;
        lock (_gate)
        {
            if (_state is ConnectionState.Connected or ConnectionState.Connecting) return;

            _state = ConnectionState.Connecting;
            asked = _retry;
            _retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        asked.TrySetResult();
        Safely(() => Changed?.Invoke());
    }

    /// <summary>
    /// Takes what a request found out. Connecting nowhere also ends the events connection, which may be dead without
    /// having said so, and the next try shows whether the server is there.
    /// </summary>
    public void Report(ConnectionEvent happened) => Move(happened switch
    {
        ConnectionEvent.Listening => ConnectionState.Connected,
        ConnectionEvent.SignedOut => ConnectionState.SignedOut,
        ConnectionEvent.IdentityChanged => ConnectionState.IdentityChanged,
        _ => ConnectionState.Unreachable,
    });

    private static bool IsFinal(ConnectionState state) =>
        state is ConnectionState.SignedOut or ConnectionState.IdentityChanged or ConnectionState.VersionMismatch;

    // Nothing awaits RunAsync, so a failure in a handler would end the watch unseen, and a request that happened to
    // report would fail for it.
    private static void Safely(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"A connection handler failed: {e.GetType().Name}");
        }
    }

    private TaskCompletionSource RetryAsked()
    {
        lock (_gate) return _retry;
    }

    /// <summary>One try: returns when the connection has ended or could not be made, or at once from a final state.</summary>
    private async Task HoldAsync(CancellationToken ct)
    {
        using var hold = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Checked with _hold set under one gate: a request can reach a final state during the wait before a try, and a
        // connection opened in that state could neither change it nor be ended by it.
        lock (_gate)
        {
            if (IsFinal(_state)) return;

            _hold = hold;
        }

        try
        {
            var ping = await client.GetAsync<PingReply>("ping", hold.Token);
            lock (_gate) _serverVersion = ping.Version;
            if (ping.Version != ApiVersion.Current)
            {
                Move(ConnectionState.VersionMismatch);
                return;
            }

            await foreach (var name in client.EventsAsync(hold.Token))
            {
                if (name == SignedOutEvent)
                {
                    Move(ConnectionState.SignedOut);
                    return;
                }

                Safely(() => EventReceived?.Invoke(name));
            }

            if (!hold.IsCancellationRequested) Move(ConnectionState.Unreachable);
        }
        catch (OperationCanceledException) when (hold.IsCancellationRequested)
        {
            // Whoever ended it has already set the state.
        }
        catch (Exception e)
        {
            // Anything unexpected, such as a token function that throws, is one more failed try: nothing awaits this loop.
            if (e is not (ServerUnreachableException or IdentityChangedException or ApiException))
                Trace.TraceWarning($"Connecting failed: {e.GetType().Name}");

            // Move leaves a final state alone, so a sign-out or a new certificate stays.
            Move(ConnectionState.Unreachable);
        }
        finally
        {
            lock (_gate) _hold = null;
        }
    }

    /// <returns>Whether <see cref="Retry"/> ended the wait.</returns>
    private async Task<bool> WaitAsync(TimeSpan wait, TaskCompletionSource retry, CancellationToken ct)
    {
        using var over = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAny(retry.Task, Task.Delay(wait, _time, over.Token));
        await over.CancelAsync();
        return retry.Task.IsCompleted;
    }

    private void Move(ConnectionState next)
    {
        lock (_gate)
        {
            if (_state == next || IsFinal(_state)) return;

            _state = next;
            // Under the gate: past it, HoldAsync could have disposed this.
            if (next != ConnectionState.Connected) _hold?.Cancel();
        }

        Safely(() => Changed?.Invoke());
    }
}
