using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>
/// The always-visible bottom strip. Each property is one segment; a null segment is hidden. The hub raises
/// <see cref="ActivityHub.StatusChanged"/> on background threads, so changes are posted to the UI thread and
/// applied from <see cref="ActivityHub.Current"/>, which is always the latest snapshot even when two threads'
/// events arrive out of order.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    private readonly ActivityHub _hub;
    private int _applyQueued;

    public StatusBarViewModel(ActivityHub hub)
    {
        _hub = hub;
        Apply(hub.Current);
        hub.StatusChanged += OnStatusChanged;
    }

    /// <summary>Raised when the user clicks the TMDB key segment.</summary>
    public event Action? SettingsRequested;

    /// <summary>"Scanning Supernatural, 12 of 44", "Up to date", or null while only identification runs.</summary>
    [ObservableProperty]
    public partial string? Progress { get; private set; }

    /// <summary>"Identifying 37", or null when nothing waits.</summary>
    [ObservableProperty]
    public partial string? Identifying { get; private set; }

    [ObservableProperty]
    public partial string Connection { get; private set; } = "";

    /// <summary>"TMDB key missing" or "TMDB key rejected"; null when the key works.</summary>
    [ObservableProperty]
    public partial string? KeyProblem { get; private set; }

    /// <summary>"Folder unavailable: Torrent Downloads"; null when every folder can be read.</summary>
    [ObservableProperty]
    public partial string? UnavailableFolders { get; private set; }

    /// <summary>"Can't open the library" when the database could not be opened.</summary>
    [ObservableProperty]
    public partial string? LibraryProblem { get; private set; }

    /// <summary>What to do about <see cref="LibraryProblem"/>; Home shows it.</summary>
    [ObservableProperty]
    public partial string? LibraryErrorHint { get; private set; }

    /// <summary>"Scan failed: Movies" until a scan of that folder works again.</summary>
    [ObservableProperty]
    public partial string? FailedScans { get; private set; }

    /// <summary>The player's name while a file plays.</summary>
    [ObservableProperty]
    public partial string? Player { get; private set; }

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    private void OnStatusChanged(StatusSnapshot _)
    {
        if (Interlocked.Exchange(ref _applyQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _applyQueued, 0);
            Apply(_hub.Current);
        });
    }

    private void Apply(StatusSnapshot s)
    {
        Progress = s switch
        {
            { ScanFolder: { } folder, ScanTotal: > 0 } => string.Create(CultureInfo.InvariantCulture,
                $"Scanning {folder}, {s.ScanDone} of {s.ScanTotal}"),
            { ScanFolder: { } folder } => $"Scanning {folder}",
            // "Up to date" would be wrong next to any of these.
            { PendingIdentification: > 0 } or { LibraryError: not null } or { FailedScans.Count: > 0 } => null,
            _ => "Up to date",
        };
        LibraryProblem = s.LibraryError is null ? null : "Can't open the library";
        LibraryErrorHint = s.LibraryError;
        FailedScans = s.FailedScans.Count switch
        {
            0 => null,
            1 => $"Scan failed: {s.FailedScans[0]}",
            _ => $"Scans failed: {string.Join(", ", s.FailedScans)}",
        };
        Identifying = s.PendingIdentification > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Identifying {s.PendingIdentification}")
            : null;
        Connection = s.Online ? "Online" : "Offline";
        KeyProblem = s.KeyState switch
        {
            KeyState.Missing => "TMDB key missing",
            KeyState.Rejected => "TMDB key rejected",
            _ => null,
        };
        UnavailableFolders = s.UnavailableFolders.Count switch
        {
            0 => null,
            1 => $"Folder unavailable: {s.UnavailableFolders[0]}",
            _ => $"Folders unavailable: {string.Join(", ", s.UnavailableFolders)}",
        };
        Player = s.PlayerName;
    }
}
