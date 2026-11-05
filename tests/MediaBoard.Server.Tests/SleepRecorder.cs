namespace MediaBoard.Server.Tests;

/// <summary>Counts holds and leaves the real power state alone.</summary>
public sealed class SleepRecorder : ISleepBlocker
{
    private int _holds;
    private int _releases;

    public int Holds => Volatile.Read(ref _holds);

    public int Releases => Volatile.Read(ref _releases);

    public void Hold() => Interlocked.Increment(ref _holds);

    public void Release() => Interlocked.Increment(ref _releases);
}
