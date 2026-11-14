using System.Threading.Channels;
using CozyCouch.Core.Accounts;
using CozyCouch.Core.Library;

namespace CozyCouch.Server;

/// <summary>
/// Who has an events connection open and what each is owed, for as long as the server listens. Library changes come in
/// bursts and from the UI thread, so nothing here waits: a frame lands in a listener's queue and its request sends it.
/// </summary>
internal sealed class EventHub(AccountService service, StreamRegistry streams, LocalLibrary library, TimeProvider time) : IDisposable
{
    // Every frame has a data line, empty as it is: a standard reader drops an event without one.
    private const string LibraryChanged = "event: library-changed\ndata: \n\n";
    private const string StreamStopped = "event: stream-stopped\ndata: \n\n";
    private const string SignedOut = "event: signed-out\ndata: \n\n";
    private const string KeepAlive = ": keep-alive\n\n";

    // Only a client that has stopped reading comes near this. The newest frame must get through, and signed-out is the last.
    private const int MaxQueued = 16;

    private static readonly TimeSpan LibraryChangedGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(20);

    private readonly Lock _gate = new();
    private readonly HashSet<Listener> _listeners = [];
    private readonly HashSet<int> _accountsChanged = [];
    private ITimer? _keepAlive;
    private ITimer? _libraryChanged;
    private bool _everyoneChanged;

    public void Start()
    {
        service.AccountClosed += OnAccountClosed;
        service.DeviceSignedOut += OnDeviceSignedOut;
        service.AccessChanged += OnAccountChanged;
        streams.Stopped += OnStreamStopped;
        library.ContentChanged += OnLibraryChanged;
        library.ProgressChanged += OnAccountChanged;
        _keepAlive = time.CreateTimer(_ => Send(KeepAlive), null, KeepAliveEvery, KeepAliveEvery);
    }

    public void Dispose()
    {
        service.AccountClosed -= OnAccountClosed;
        service.DeviceSignedOut -= OnDeviceSignedOut;
        service.AccessChanged -= OnAccountChanged;
        streams.Stopped -= OnStreamStopped;
        library.ContentChanged -= OnLibraryChanged;
        library.ProgressChanged -= OnAccountChanged;

        lock (_gate)
        {
            _keepAlive?.Dispose();
            _libraryChanged?.Dispose();
        }
    }

    /// <summary>
    /// Starts queueing frames for this device. Disposing the listener stops it. A device needs one connection, so an
    /// older one ends here: a token alone could otherwise fill every connection the server allows.
    /// </summary>
    public Listener Join(RequestViewer who)
    {
        var listener = new Listener(this, who.Account.Id, who.Device.Id);
        lock (_gate)
        {
            foreach (var older in _listeners.Where(other => other.DeviceId == who.Device.Id)) older.End();
            _listeners.RemoveWhere(other => other.DeviceId == who.Device.Id);
            _listeners.Add(listener);
        }

        return listener;
    }

    private void Leave(Listener listener)
    {
        lock (_gate) _listeners.Remove(listener);
    }

    private void OnAccountClosed(int userId) => SignOut(listener => listener.UserId == userId);

    private void OnDeviceSignedOut(int deviceId) => SignOut(listener => listener.DeviceId == deviceId);

    private void OnStreamStopped(int deviceId) => Send(StreamStopped, listener => listener.DeviceId == deviceId);

    private void OnLibraryChanged()
    {
        lock (_gate)
        {
            _everyoneChanged = true;
            ArmLibraryChanged();
        }
    }

    // Folders or progress: only that account's lists alter, so nobody else reloads.
    private void OnAccountChanged(int userId)
    {
        lock (_gate)
        {
            _accountsChanged.Add(userId);
            ArmLibraryChanged();
        }
    }

    // Called with _gate held. The first change arms the timer and the ones after it land under the same frame, which
    // goes out when a burst has had time to settle.
    private void ArmLibraryChanged() =>
        _libraryChanged ??= time.CreateTimer(_ => SendLibraryChanged(), null, LibraryChangedGap, Timeout.InfiniteTimeSpan);

    private void SendLibraryChanged()
    {
        lock (_gate)
        {
            _libraryChanged?.Dispose();
            _libraryChanged = null;

            foreach (var listener in _listeners.Where(l => _everyoneChanged || _accountsChanged.Contains(l.UserId))) listener.Send(LibraryChanged);

            _everyoneChanged = false;
            _accountsChanged.Clear();
        }
    }

    private void SignOut(Func<Listener, bool> which)
    {
        lock (_gate)
        {
            foreach (var listener in _listeners.Where(which)) listener.SendLast(SignedOut);
        }
    }

    private void Send(string frame, Func<Listener, bool>? which = null)
    {
        lock (_gate)
        {
            foreach (var listener in _listeners.Where(which ?? (_ => true))) listener.Send(frame);
        }
    }

    /// <summary>One open connection's queue. <see cref="Frames"/> ends on <see cref="End"/>, and after <see cref="SendLast"/>'s frame.</summary>
    internal sealed class Listener(EventHub hub, int userId, int deviceId) : IDisposable
    {
        private readonly Channel<string> _queue = Channel.CreateBounded<string>(
            new BoundedChannelOptions(MaxQueued) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public int UserId { get; } = userId;

        public int DeviceId { get; } = deviceId;

        public ChannelReader<string> Frames => _queue.Reader;

        public void Send(string frame) => _queue.Writer.TryWrite(frame);

        public void SendLast(string frame)
        {
            Send(frame);
            End();
        }

        public void End() => _queue.Writer.TryComplete();

        public void Dispose() => hub.Leave(this);
    }
}
