using MediaBoard.Core.Players;

namespace MediaBoard.Core.Tests.Support;

/// <summary>
/// An <see cref="IAttachableSession"/> a test drives by hand: <see cref="Raise"/> sends the service an event, and
/// <paramref name="answers"/> says whether a player is there to attach to.
/// </summary>
public sealed class FakeAttachableSession(string name = "mpv", bool answers = true) : IAttachableSession
{
    public string PlayerName => name;

    public bool TracksProgress => true;

    public event Action<PlaybackEvent>? Event;

    /// <summary>What <see cref="GetPositionAsync"/> reports.</summary>
    public TimeSpan? Position { get; set; }

    public int AttachCalls { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>When set, <see cref="AttachAsync"/> throws it.</summary>
    public Exception? AttachFailure { get; set; }

    /// <summary>Raised from inside <see cref="AttachAsync"/>, as a real session does for the file it finds open.</summary>
    public PlaybackEvent? RaiseOnAttach { get; set; }

    public void Raise(PlaybackEvent e) => Event?.Invoke(e);

    public Task<bool> AttachAsync(CancellationToken ct)
    {
        AttachCalls++;
        if (AttachFailure is not null) throw AttachFailure;
        if (answers && RaiseOnAttach is { } e) Event?.Invoke(e);
        return Task.FromResult(answers);
    }

    public Task StartAsync(string filePath, TimeSpan? startAt, CancellationToken ct) =>
        throw new NotSupportedException("An attachable session is attached, not started.");

    public Task<TimeSpan?> GetPositionAsync(CancellationToken ct) => Task.FromResult(Position);

    public void Detach()
    {
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
