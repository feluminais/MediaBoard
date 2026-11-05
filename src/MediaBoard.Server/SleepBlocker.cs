using System.Runtime.InteropServices;

namespace MediaBoard.Server;

/// <summary>Keeps the PC awake while something streams. <see cref="StreamRegistry"/> holds it from the first session to the last.</summary>
public interface ISleepBlocker
{
    void Hold();

    void Release();
}

/// <summary>
/// Windows ties this state to the thread that set it, and it lasts only as long as that thread does. A thread-pool
/// thread could never clear it from elsewhere and might take it away early, so a hold gets a thread of its own.
/// The screen may still turn off.
/// </summary>
public sealed partial class SleepBlocker : ISleepBlocker
{
    private const uint Continuous = 0x80000000;
    private const uint SystemRequired = 0x00000001;

    private readonly Func<uint, uint> _setState;
    private readonly Lock _gate = new();
    private ManualResetEventSlim? _released;

    public SleepBlocker()
        : this(SetThreadExecutionState)
    {
    }

    internal SleepBlocker(Func<uint, uint> setState) => _setState = setState;

    public void Hold()
    {
        lock (_gate)
        {
            if (_released is not null) return;

            var released = new ManualResetEventSlim();
            _released = released;
            new Thread(() => HoldUntil(released)) { IsBackground = true, Name = "Sleep blocker" }.Start();
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _released?.Set();
            _released = null;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    private void HoldUntil(ManualResetEventSlim released)
    {
        _setState(Continuous | SystemRequired);
        released.Wait();
        _setState(Continuous);
        released.Dispose();
    }
}
