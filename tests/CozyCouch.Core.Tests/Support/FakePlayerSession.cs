using CozyCouch.Core.Players;

namespace CozyCouch.Core.Tests.Support;

/// <summary>
/// An <see cref="IPlayerSession"/> a test drives by hand: <see cref="Raise"/> sends the service an event,
/// <see cref="Starts"/> records what it was asked to play.
/// </summary>
public sealed class FakePlayerSession(bool tracksProgress = true) : IPlayerSession
{
    public bool TracksProgress => tracksProgress;

    public event Action<PlaybackEvent>? Event;

    public List<(string FilePath, TimeSpan? StartAt)> Starts { get; } = [];

    /// <summary>What <see cref="GetPositionAsync"/> reports.</summary>
    public TimeSpan? Position { get; set; }

    /// <summary>When set, <see cref="StartAsync"/> throws it.</summary>
    public Exception? StartFailure { get; set; }

    public bool Disposed { get; private set; }

    /// <summary>The service let the player run on (<see cref="Detach"/>) before disposing the session.</summary>
    public bool Detached { get; private set; }

    public void Detach() => Detached = true;

    public void Raise(PlaybackEvent e) => Event?.Invoke(e);

    public Task StartAsync(string filePath, TimeSpan? startAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (StartFailure is not null) throw StartFailure;
        Starts.Add((filePath, startAt));
        return Task.CompletedTask;
    }

    public Task<TimeSpan?> GetPositionAsync(CancellationToken ct) => Task.FromResult(Position);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
