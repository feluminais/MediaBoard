using MediaBoard.Core.Accounts;

namespace MediaBoard.Server;

/// <summary>
/// Ends streams whose access went away, for as long as the server listens. A library edit or a change of folders can
/// come from the UI thread and can take any file away, so those re-checks run on the pool and pile up into one.
/// </summary>
internal sealed class StreamWatch(
    StreamRegistry streams, AccountService service, AccountStore accounts, DeviceStore devices, SharedLibrary library) : IDisposable
{
    private int _queued;

    public void Start()
    {
        service.AccountClosed += streams.CloseAccount;
        service.DeviceSignedOut += streams.CloseDevice;
        service.AccessChanged += OnAccessChanged;
        library.Owner.Changed += RecheckSoon;
    }

    public void Dispose()
    {
        service.AccountClosed -= streams.CloseAccount;
        service.DeviceSignedOut -= streams.CloseDevice;
        service.AccessChanged -= OnAccessChanged;
        library.Owner.Changed -= RecheckSoon;
    }

    /// <summary>Whether the device's row still exists. Every way of revoking a token deletes it.</summary>
    public bool IsSignedIn(int deviceId) => devices.Get(deviceId) is not null;

    /// <summary>Reads the account afresh: a viewer built earlier holds the folders as they were then.</summary>
    public bool Reachable(int userId, int fileId) =>
        accounts.Get(userId) is { Disabled: false } account && library.Access.File(accounts.ViewerOf(account), fileId) is not null;

    private void OnAccessChanged(int userId) => RecheckSoon();

    private void RecheckSoon()
    {
        if (streams.Current.Count == 0 || Interlocked.Exchange(ref _queued, 1) == 1) return;

        _ = Task.Run(() =>
        {
            // Cleared first: a change arriving during the check must get a check of its own.
            Volatile.Write(ref _queued, 0);
            streams.CloseUnreachable(Reachable);
        });
    }
}
