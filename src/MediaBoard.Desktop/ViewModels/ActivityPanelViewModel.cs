using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaBoard.Core.Activity;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>One line of the activity panel. <see cref="Hint"/> says what to do about a problem.</summary>
public sealed record ActivityItem(string Time, ActivityLevel Level, string Message, string? Hint)
{
    public bool IsError => Level == ActivityLevel.Error;

    public bool IsWarning => Level == ActivityLevel.Warning;

    public static ActivityItem From(ActivityEntry entry) =>
        new(entry.At.ToString("HH:mm", CultureInfo.InvariantCulture), entry.Level, entry.Message, entry.Hint);
}

/// <summary>Recent events and errors, newest first. New entries arrive on background threads and are posted to the UI thread.</summary>
public sealed partial class ActivityPanelViewModel : ObservableObject
{
    // Entries read from the hub at construction. One logged between subscribing and reading is in that snapshot
    // and also raises EntryLogged, so its event is skipped. Compared by reference: two entries can be equal.
    private readonly HashSet<ActivityEntry> _listed = new(ReferenceEqualityComparer.Instance);

    public ActivityPanelViewModel(ActivityHub hub)
    {
        hub.EntryLogged += OnEntryLogged;
        foreach (var entry in hub.Recent.Reverse())
        {
            _listed.Add(entry);
            Entries.Add(ActivityItem.From(entry));
        }
    }

    public ObservableCollection<ActivityItem> Entries { get; } = [];

    public bool IsEmpty => Entries.Count == 0;

    private void OnEntryLogged(ActivityEntry entry) => Dispatcher.UIThread.Post(() =>
    {
        if (_listed.Remove(entry)) return;

        Entries.Insert(0, ActivityItem.From(entry));
        while (Entries.Count > ActivityHub.MaxEntries) Entries.RemoveAt(Entries.Count - 1);
        OnPropertyChanged(nameof(IsEmpty));
    });
}
