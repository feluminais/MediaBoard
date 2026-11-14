using CozyCouch.Core.Players;
using CozyCouch.Core.Players.MpcHc;

namespace CozyCouch.Core.Tests.Players;

/// <summary>An <see cref="IPlayerProcess"/> that exits when a test says so.</summary>
internal sealed class FakePlayerProcess : IPlayerProcess
{
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool ExitOnClose { get; set; } = true;
    public bool CloseRequested { get; private set; }
    public bool Killed { get; private set; }
    public bool Disposed { get; private set; }
    public Task Exited => _exited.Task;
    public bool HasExited => _exited.Task.IsCompleted;
    public int ExitCode { get; private set; }

    public void Exit(int exitCode = 0)
    {
        if (HasExited) return;
        ExitCode = exitCode;
        _exited.TrySetResult();
    }

    public void CloseMainWindow()
    {
        CloseRequested = true;
        if (ExitOnClose) Exit();
    }

    public void Kill()
    {
        Killed = true;
        Exit();
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Collects what a session raises.</summary>
internal sealed class PlaybackLog
{
    private readonly List<PlaybackEvent> _events = [];

    public List<PlaybackEvent> All
    {
        get { lock (_events) return [.. _events]; }
    }

    public void Add(PlaybackEvent e)
    {
        lock (_events) _events.Add(e);
    }

    public List<T> OfType<T>() => All.OfType<T>().ToList();

    public Task<T> WaitFor<T>() where T : PlaybackEvent => WaitForAsync<T>(Waiting.Timeout);

    private async Task<T> WaitForAsync<T>(TimeSpan timeout) where T : PlaybackEvent
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var found = OfType<T>().FirstOrDefault();
            if (found is not null) return found;
            if (DateTime.UtcNow > deadline) Assert.Fail($"No {typeof(T).Name} event in time; got: {string.Join(", ", All)}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

internal static class Waiting
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Condition not met in time");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>A clock that moves only when a test moves it.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>An <see cref="IMpcTransport"/> that records what is sent; a test plays MPC-HC by calling <see cref="Receive"/> or setting <see cref="Reply"/>.</summary>
internal sealed class FakeTransport : IMpcTransport
{
    private readonly List<(nint Target, uint Cmd, string Payload)> _sent = [];

    public nint HostHandle => 4096;
    public event Action<uint, string>? Received;
    public Func<uint, string, (uint Cmd, string Payload)?>? Reply { get; set; }
    public bool Disposed { get; private set; }

    public List<(nint Target, uint Cmd, string Payload)> Sent
    {
        get { lock (_sent) return [.. _sent]; }
    }

    public List<string> SentTo(nint target, uint cmd) =>
        Sent.Where(s => s.Target == target && s.Cmd == cmd).Select(s => s.Payload).ToList();

    public void Receive(uint cmd, string payload) => Received?.Invoke(cmd, payload);

    public void Send(nint target, uint cmd, string payload)
    {
        lock (_sent) _sent.Add((target, cmd, payload));
        if (Reply?.Invoke(cmd, payload) is { } reply) Receive(reply.Cmd, reply.Payload);
    }

    public void Dispose() => Disposed = true;
}
