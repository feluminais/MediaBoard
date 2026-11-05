using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Library;
using MediaBoard.Server;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>One stream in Watching now. The row stays the same object while the stream runs, so its Stop button keeps the focus.</summary>
public sealed partial class WatchingRow(StreamSession session) : ObservableObject
{
    public Guid Id { get; } = session.Id;

    public int UserId { get; } = session.UserId;

    public string Person { get; } = session.UserName;

    public string Device { get; } = session.DeviceName;

    public string Title { get; } = session.Title;

    /// <summary>Stop has been pressed, so another press has nothing to do.</summary>
    internal bool Stopping { get; set; }

    /// <summary>"12:05 of 1:30:00", or "12:05" while the length isn't known.</summary>
    [ObservableProperty]
    public partial string Position { get; private set; } = "";

    /// <summary>"8.0 Mbit/s".</summary>
    [ObservableProperty]
    public partial string Speed { get; private set; } = "";

    internal void Apply(StreamSession now)
    {
        Position = now.DurationS > 0 ? $"{Clock(now.PositionS)} of {Clock(now.DurationS)}" : Clock(now.PositionS);
        Speed = string.Create(CultureInfo.InvariantCulture, $"{now.MbitPerSecond:0.0} Mbit/s");
    }

    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(seconds, 0));
        return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }
}

/// <summary>One account in People. It stays the same object across reloads, so a focused button in it keeps the focus.</summary>
public sealed partial class PersonRow(int id, AccountRole role) : ObservableObject
{
    public int Id { get; } = id;

    public bool IsOwner { get; } = role == AccountRole.Owner;

    /// <summary>Folders, devices, Reset password, Disable and Remove are for everyone but the owner.</summary>
    public bool CanManage => !IsOwner;

    public bool CanResetPassword => CanManage && CanInvite;

    /// <summary>An invite for a disabled account couldn't be used.</summary>
    public bool CanInvite => !IsDisabled;

    public string DisableLabel => IsDisabled ? "Enable" : "Disable";

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    /// <summary>"All folders", "2 folders", "No folders".</summary>
    [ObservableProperty]
    public partial string Access { get; private set; } = "";

    /// <summary>"1 invite pending", or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingInvites))]
    public partial string PendingInvites { get; private set; } = "";

    public bool HasPendingInvites => PendingInvites.Length > 0;

    /// <summary>"Seen 5 min ago", "Hasn't joined yet".</summary>
    [ObservableProperty]
    public partial string Seen { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisableLabel), nameof(CanInvite), nameof(CanResetPassword))]
    public partial bool IsDisabled { get; private set; }

    /// <summary>Too many wrong passwords: nobody can sign in with this name for now.</summary>
    [ObservableProperty]
    public partial bool IsLocked { get; private set; }

    [ObservableProperty]
    public partial bool IsOpen { get; internal set; }

    /// <summary>What shows while the row is open; null while it's closed, so a closed row builds no controls for it.</summary>
    [ObservableProperty]
    public partial PersonDetail? Detail { get; internal set; }

    /// <summary>What the last action in the row came to, when that needs saying.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; internal set; }

    public bool HasMessage => Message is not null;

    /// <summary>The account as last read, for filling the detail the moment the row opens.</summary>
    internal Account? Account { get; private set; }

    internal void Apply(Account account, bool isLocked, string seen, int pendingInvites)
    {
        PendingInvites = pendingInvites == 0 ? "" : $"{Plural.Of(pendingInvites, "invite")} pending";
        Account = account;
        Name = account.Name;
        IsDisabled = account.Disabled;
        IsLocked = isLocked;
        Seen = seen;
        Access = account.FolderIds switch
        {
            null => "All folders",
            [] => "No folders",
            var folders => Plural.Of(folders.Count, "folder"),
        };
    }
}

/// <summary>An open row: which folders the person sees and which devices they've signed in on.</summary>
public sealed partial class PersonDetail(PersonRow row, Action<PersonDetail> foldersChanged) : ObservableObject
{
    private bool _applying;

    public PersonRow Row { get; } = row;

    public ObservableCollection<FolderChoice> Folders { get; } = [];

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    /// <summary>On, the person also gets folders added later.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFolders))]
    public partial bool AllFolders { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoDevices))]
    public partial bool DevicesLoaded { get; private set; }

    public bool HasNoDevices => DevicesLoaded && Devices.Count == 0 && Row.CanManage;

    /// <summary>Everyone's row lists devices. The owner's shows them only when there are some, so a lost PC can be signed out.</summary>
    public bool ShowsDevices => Row.CanManage || Devices.Count > 0;

    public bool HasNoFolders => !AllFolders && Folders.Count == 0;

    /// <summary>Null for no choice, or the ticked folders.</summary>
    internal IReadOnlyCollection<int>? ChosenFolders =>
        AllFolders ? null : Folders.Where(f => f.IsChecked).Select(f => f.Id).ToList();

    /// <summary>Takes what the library says. A choice the owner just made isn't here yet: the caller holds this until it lands.</summary>
    internal void ApplyFolders(Account account, IReadOnlyList<FolderRow> folders)
    {
        _applying = true;
        try
        {
            AllFolders = account.FolderIds is null;
            var shown = Folders.ToDictionary(f => f.Id);
            var choices = folders.Select(folder =>
            {
                var choice = shown.GetValueOrDefault(folder.Id) ?? new FolderChoice(folder, false, OnChoiceChanged);
                choice.Show(account.FolderIds?.Contains(folder.Id) ?? true);
                return choice;
            }).ToList();
            CollectionSync.Sync(Folders, choices);
            OnPropertyChanged(nameof(HasNoFolders));
        }
        finally
        {
            _applying = false;
        }
    }

    /// <returns>True when a device went, and with it a Sign out button that may have had the focus.</returns>
    internal bool ApplyDevices(IReadOnlyList<Device> devices, Func<Device, string> describe)
    {
        var before = Devices.Count;
        var shown = Devices.ToDictionary(d => d.Id);
        var rows = devices.Select(device =>
        {
            var shownRow = shown.GetValueOrDefault(device.Id) ?? new DeviceRow(device);
            shownRow.Details = describe(device);
            return shownRow;
        }).ToList();
        var gone = shown.Keys.Except(devices.Select(d => d.Id)).Any();
        CollectionSync.Sync(Devices, rows);
        DevicesLoaded = true;
        if (before != Devices.Count)
        {
            OnPropertyChanged(nameof(HasNoDevices));
            OnPropertyChanged(nameof(ShowsDevices));
        }

        return gone;
    }

    partial void OnAllFoldersChanged(bool value)
    {
        if (_applying) return;

        // Leaving "All folders" starts from everything ticked, so nothing vanishes until the owner unticks it.
        foreach (var folder in Folders) folder.Show(true);
        foldersChanged(this);
    }

    private void OnChoiceChanged()
    {
        if (!_applying) foldersChanged(this);
    }
}

/// <summary>A folder with a tick. <see cref="Show"/> changes the tick without counting as the owner's choice.</summary>
public sealed partial class FolderChoice(FolderRow folder, bool isChecked, Action? changed = null) : ObservableObject
{
    private bool _showing;

    public int Id { get; } = folder.Id;

    public string Name { get; } = FolderItem.NameOf(folder.Path);

    public string Path { get; } = folder.Path;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    internal void Show(bool value)
    {
        _showing = true;
        IsChecked = value;
        _showing = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_showing) changed?.Invoke();
    }
}

/// <summary>A signed-in device under an open row.</summary>
public sealed partial class DeviceRow(Device device) : ObservableObject
{
    public int Id { get; } = device.Id;

    public int UserId { get; } = device.UserId;

    public string Name { get; } = device.Name;

    /// <summary>When it was last seen and from where.</summary>
    [ObservableProperty]
    public partial string Details { get; internal set; } = "";
}

/// <summary>One line of Activity.</summary>
/// <param name="Id">The log entry's id.</param>
/// <param name="When">Local time, with the date unless it's today.</param>
/// <param name="Text">What happened, in plain words.</param>
public sealed record SharingActivityRow(long Id, string When, string Text);
