using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBoard.Desktop.Sharing;

namespace MediaBoard.Desktop;

/// <summary>
/// One running app per data directory. Closing to the tray makes starting the app again the normal way back to it, so
/// a second process hands over and leaves. A named event does both jobs: whoever creates it is first, and setting it
/// is the request to show the window. Windows drops it with the last handle, so a crash leaves nothing to clear.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly EventWaitHandle _activate;
    private readonly bool _isFirst;
    private RegisteredWaitHandle? _listening;

    private SingleInstance(string name) => _activate = new EventWaitHandle(false, EventResetMode.AutoReset, name, out _isFirst);

    /// <summary>The same directory spelled differently is the same claim. "Local" keeps it to this Windows session.</summary>
    public static SingleInstance For(string dataDir)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        return new SingleInstance(string.Create(CultureInfo.InvariantCulture, $@"Local\MediaBoard-{hash}"));
    }

    /// <summary>
    /// True for the first process. A second one started by hand asks the first to show its window. One started with
    /// Windows has nothing to ask for, because the user didn't ask to see anything.
    /// </summary>
    public bool ShouldRun(IEnumerable<string> args)
    {
        if (_isFirst) return true;

        if (!StartWithWindows.IsTrayStart(args)) _activate.Set();
        return false;
    }

    /// <summary>Calls <paramref name="activated"/> on a pool thread for each request, including one made before this call.</summary>
    public void Listen(Action activated) =>
        _listening = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => activated(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _listening?.Unregister(null);
        _activate.Dispose();
    }
}
