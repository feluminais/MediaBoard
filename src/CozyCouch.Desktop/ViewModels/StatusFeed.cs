using System.Globalization;
using Avalonia.Threading;
using CozyCouch.Core.Activity;

namespace CozyCouch.Desktop.ViewModels;

/// <summary>
/// Hands a view model the hub's status on the UI thread. The hub raises <see cref="ActivityHub.StatusChanged"/> on
/// background threads, as often as every 25 files of a scan; however many changes arrive before the dispatcher gets
/// to it, one post runs, and it applies <see cref="ActivityHub.Current"/>, which is always the latest snapshot.
/// The hub lives as long as the app: a view model that lives shorter disposes its feed, or the hub keeps it alive.
/// </summary>
public sealed class StatusFeed : IDisposable
{
    private readonly ActivityHub _hub;
    private readonly Action<StatusSnapshot> _apply;
    private int _queued;
    private volatile bool _disposed;

    public StatusFeed(ActivityHub hub, Action<StatusSnapshot> apply)
    {
        _hub = hub;
        _apply = apply;
        hub.StatusChanged += OnStatusChanged;
    }

    /// <summary>Stops listening; a post already queued does nothing.</summary>
    public void Dispose()
    {
        _disposed = true;
        _hub.StatusChanged -= OnStatusChanged;
    }

    private void OnStatusChanged(StatusSnapshot _)
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _queued, 0);
            if (!_disposed) _apply(_hub.Current);
        });
    }
}

/// <summary>What an empty poster page says while the library is still filling up.</summary>
public static class LibraryBusy
{
    public const string Line = "Titles appear as they're found.";

    /// <summary>
    /// "Scanning folders…" while a folder scans or waits to, "Identifying 3 files…" while only identification runs,
    /// null when neither runs and an empty page really is empty.
    /// </summary>
    public static string? Title(StatusSnapshot s) =>
        s.IsScanning || s.FolderScans.Count > 0 ? "Scanning folders…"
        : s.PendingIdentification switch
        {
            <= 0 => null,
            1 => "Identifying 1 file…",
            var n => string.Create(CultureInfo.InvariantCulture, $"Identifying {n} files…"),
        };
}
