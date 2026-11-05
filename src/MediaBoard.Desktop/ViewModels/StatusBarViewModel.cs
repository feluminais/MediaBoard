using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>
/// The always-visible bottom strip. Each property is one segment; a null segment is hidden. Hub changes arrive on
/// the UI thread through a <see cref="StatusFeed"/>, which applies the latest snapshot even when two threads'
/// events arrive out of order.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    private int _unseenProblems;
    private string? _latestProblem;
    private string? _connectionLine;

    public StatusBarViewModel(ActivityHub hub)
    {
        Apply(hub.Current);
        _ = new StatusFeed(hub, Apply);
        hub.EntryLogged += OnEntryLogged;
    }

    /// <summary>Raised when the user clicks the TMDB key segment.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised when the user clicks the sharing segment.</summary>
    public event Action? SharingRequested;

    /// <summary>"Scanning Supernatural, 12 of 44", "Up to date", or null while only identification runs.</summary>
    [ObservableProperty]
    public partial string? Progress { get; private set; }

    /// <summary>"Identifying 37", or null when nothing waits.</summary>
    [ObservableProperty]
    public partial string? Identifying { get; private set; }

    /// <summary>A scan or identification is running: the strip draws a progress bar and the accent colour.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>The scan's total is not known yet, or only identification runs (it has no total).</summary>
    [ObservableProperty]
    public partial bool IsBusyIndeterminate { get; private set; }

    /// <summary>How far the scan is, 0 to 100, when <see cref="IsBusyIndeterminate"/> is false.</summary>
    [ObservableProperty]
    public partial double BusyPercent { get; private set; }

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

    /// <summary>
    /// Warnings and errors logged since the activity panel was last open: the latest message, or "3 problems".
    /// Null when there is nothing unseen. Clicking the strip opens the panel, which clears it.
    /// </summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>The activity panel is open (set by the shell): problems logged meanwhile are seen as they arrive.</summary>
    [ObservableProperty]
    public partial bool IsActivityOpen { get; set; }

    /// <summary>The player's name while a file plays.</summary>
    [ObservableProperty]
    public partial string? Player { get; private set; }

    /// <summary>"Sharing: 2 watching" while sharing is on.</summary>
    [ObservableProperty]
    public partial string? Sharing { get; private set; }

    /// <summary>A viewer's line about the server, which takes the place of "Online" and of a scan's "Up to date".</summary>
    public void ShowConnection(string line)
    {
        _connectionLine = line;
        Connection = line;
        Progress = null;
    }

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private void OpenSharing() => SharingRequested?.Invoke();

    partial void OnIsActivityOpenChanged(bool value)
    {
        if (!value) return;
        _unseenProblems = 0;
        _latestProblem = null;
        Problem = null;
    }

    // Raised on the logging thread; counted on the UI thread, where IsActivityOpen lives.
    private void OnEntryLogged(ActivityEntry entry)
    {
        if (entry.Level == ActivityLevel.Info) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (IsActivityOpen) return;
            _unseenProblems++;
            _latestProblem = entry.Message;
            Problem = _unseenProblems == 1
                ? _latestProblem
                : string.Create(CultureInfo.InvariantCulture, $"{_unseenProblems} problems");
        });
    }

    private void Apply(StatusSnapshot s)
    {
        IsBusy = s.IsScanning || s.PendingIdentification > 0;
        IsBusyIndeterminate = IsBusy && !(s.IsScanning && s.ScanTotal > 0); // never animating while hidden
        BusyPercent = IsBusyIndeterminate ? 0 : Math.Clamp(s.ScanDone * 100.0 / s.ScanTotal, 0, 100);
        Progress = s switch
        {
            { ScanFolder: { } folder, ScanTotal: > 0 } => string.Create(CultureInfo.InvariantCulture,
                $"Scanning {folder}, {s.ScanDone} of {s.ScanTotal}"),
            { ScanFolder: { } folder } => $"Scanning {folder}",
            // "Up to date" would be wrong next to any of these.
            { PendingIdentification: > 0 } or { LibraryError: not null } or { FailedScans.Count: > 0 } => null,
            _ when _connectionLine is not null => null,
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
        Connection = _connectionLine ?? (s.Online ? "Online" : "Offline");
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
        Sharing = s.Sharing;
    }
}
