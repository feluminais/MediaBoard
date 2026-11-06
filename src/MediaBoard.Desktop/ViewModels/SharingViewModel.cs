using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Settings;
using MediaBoard.Desktop.Sharing;
using MediaBoard.Server;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>What the Sharing page works with.</summary>
/// <param name="Controller">Turns sharing on and off and makes invites.</param>
/// <param name="Accounts">Every change to an account goes through this.</param>
/// <param name="People">Read only: the accounts.</param>
/// <param name="Devices">Read only: who is signed in where.</param>
/// <param name="Invites">Read, and cancelled when the certificate changes.</param>
/// <param name="Streams">Who is watching.</param>
/// <param name="Log">What happened.</param>
/// <param name="Guard">Which names are locked out.</param>
/// <param name="Library">The folders to choose from.</param>
/// <param name="Settings">The addresses and the port.</param>
/// <param name="StartWithWindows">The Start with Windows option.</param>
/// <param name="Hub">Where failures are logged.</param>
/// <param name="Time">The clock, and the page's timers.</param>
public sealed record SharingServices(
    SharingController Controller,
    AccountService Accounts,
    AccountStore People,
    DeviceStore Devices,
    InviteStore Invites,
    StreamRegistry Streams,
    SharingLog Log,
    SignInGuard Guard,
    ILibrary Library,
    SettingsStore Settings,
    StartWithWindows StartWithWindows,
    ActivityHub Hub,
    TimeProvider Time);

/// <summary>The page's seams to the windowing system, so a test needs no window.</summary>
/// <param name="Confirm">Asks the question with the given yes button, marked as dangerous when the last argument is true; true when the owner agreed.</param>
/// <param name="ShowPanel">Shows the modal panel and finishes when it has closed.</param>
/// <param name="Copy">Puts text on the clipboard.</param>
public sealed record SharingDialogs(
    Func<string, string, bool, Task<bool>> Confirm,
    Func<SharingPanel, Task> ShowPanel,
    Func<string, Task> Copy);

/// <summary>
/// The owner's page: the switch and its connection settings, who is watching, the people and their access, and the log.
/// Commands never throw, and everything that touches the disk runs off the UI thread. Owner actions run one at a time
/// and in order (see <see cref="InTurnAsync{T}(Func{T})"/>).
/// </summary>
public sealed partial class SharingViewModel : PageViewModel
{
    public const string OffLine = "Sharing is off. Nothing can connect.";

    private const int MostFailedAddresses = 5;
    private const int ActivityLimit = 50;
    private const string NoAddressPrompt = "Invites need an address. Enter one here and save.";
    private const string ActionFailed = "That didn't work. See the activity panel.";
    private const string Checking = "Checking…";

    private static readonly TimeSpan WatchingRefresh = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PageReload = TimeSpan.FromSeconds(5);

    private readonly SharingServices _s;
    private readonly SharingDialogs _dialogs;
    private readonly BackgroundQuery _query;
    private readonly UiThrottle _watching;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private SharingSettings _saved = new();
    private IReadOnlyList<FolderRow> _folders = [];
    private ITimer? _reloadTimer;
    private volatile bool _open;
    private int _statusQueued;
    private int _folderWrites;
    private bool _acting;
    private bool _applying;
    private bool _fieldsFilled;
    private bool _checking;

    public SharingViewModel(SharingServices services, SharingDialogs dialogs)
        : base("Sharing")
    {
        _s = services;
        _dialogs = dialogs;
        _query = new BackgroundQuery(services.Hub, "the sharing page", () => LoadProblem = IsLoaded ? null : "Couldn't load this page. See the activity panel.");
        _watching = new UiThrottle(services.Time, WatchingRefresh, RefreshWatching);
        services.Controller.Changed += OnControllerChanged;
        services.Streams.Changed += OnStreamsChanged;
        ApplyStatus();
    }

    /// <summary>Raised when the owner needs the Connection settings, so the view can bring them into sight.</summary>
    public event Action? ConnectionNeeded;

    /// <summary>
    /// Raised when a row went, along with any button of it that had the focus. Carries the row to fall back on, or null
    /// for a stream.
    /// </summary>
    public event Action<PersonRow?>? RowsRemoved;

    // ---- The switch ----

    public string FirewallNote => "The first time sharing starts, Windows asks about the firewall. Allow Private networks.";

    public string ListeningWarning =>
        "While sharing is on, it listens on every network this PC is connected to, and anyone on those networks can try to " +
        "sign in. In the firewall, allow Private networks only.";

    /// <summary>Starts or stops sharing when the owner flips it. Follows the controller otherwise.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial string StatusLine { get; private set; } = OffLine;

    /// <summary>The address and who is watching, while on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusDetail))]
    public partial string StatusDetail { get; private set; } = "";

    public bool HasStatusDetail => StatusDetail.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? Warning { get; private set; }

    public bool HasWarning => Warning is not null;

    [ObservableProperty]
    public partial bool ShowFirewallNote { get; private set; }

    // ---- Connection ----

    [ObservableProperty]
    public partial bool IsConnectionOpen { get; set; }

    /// <summary>The address people connect to from outside. Typed text is left alone by reloads until it's saved.</summary>
    [ObservableProperty]
    public partial string PublicAddress { get; set; } = "";

    [ObservableProperty]
    public partial string HomeAddress { get; set; } = "";

    [ObservableProperty]
    public partial string ServerName { get; set; } = "";

    [ObservableProperty]
    public partial string PortText { get; set; } = "";

    public string ServerNameHint => $"{Environment.UserName}'s library";

    /// <summary>The option as saved. Changing it here writes the Run key.</summary>
    [ObservableProperty]
    public partial bool StartsWithWindows { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionMessage))]
    public partial string? ConnectionMessage { get; private set; }

    public bool HasConnectionMessage => ConnectionMessage is not null;

    [ObservableProperty]
    public partial IReadOnlyList<string> RouterSteps { get; private set; } = [];

    /// <summary>What Check connection found; null until it's run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheckResult))]
    public partial string? CheckResult { get; private set; }

    public bool HasCheckResult => CheckResult is not null;

    // ---- Watching now ----

    public string NobodyWatching => "Nobody is watching.";

    public ObservableCollection<WatchingRow> Watching { get; } = [];

    public bool IsNobodyWatching => Watching.Count == 0;

    // ---- People ----

    /// <summary>Rows keep their objects across reloads (see <see cref="PersonRow"/>). The owner comes first.</summary>
    public ObservableCollection<PersonRow> People { get; } = [];

    /// <summary>True once the first load landed. What needs the data waits for it.</summary>
    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    /// <summary>Why the page has no data, after a first load that failed. It tries again as it reopens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadProblem))]
    public partial string? LoadProblem { get; private set; }

    public bool HasLoadProblem => LoadProblem is not null;

    /// <summary>What the last page-wide action came to, when that needs saying.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPeopleMessage))]
    public partial string? PeopleMessage { get; private set; }

    public bool HasPeopleMessage => PeopleMessage is not null;

    // ---- Activity ----

    public ObservableCollection<SharingActivityRow> Activity { get; } = [];

    public bool HasNoActivity => IsLoaded && Activity.Count == 0;

    /// <summary>Who has been getting passwords wrong in the last hour, or null when nobody has.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailedSignIns))]
    public partial string? FailedSignInsText { get; private set; }

    public bool HasFailedSignIns => FailedSignInsText is not null;

    // The page reloads as it opens, so a library change only concerns it while it's open.
    public override bool RefreshesWhileHidden => false;

    public override void Opened()
    {
        _open = true;
        PeopleMessage = null;
        RefreshWatching();
        _ = RefreshAsync();
        ArmReload();
    }

    public override void Closed()
    {
        _open = false;
        _watching.Stop();
        _reloadTimer?.Dispose();
        _reloadTimer = null;
    }

    public override Task RefreshAsync()
    {
        var open = People.Where(row => row.IsOpen).Select(row => row.Id).ToList();
        return _query.RunAsync(() => ReadPage(open), Apply);
    }

    // ---- Loading ----

    // Thread pool.
    private PageData ReadPage(IReadOnlyList<int> openRows)
    {
        var accounts = _s.People.All();
        return new PageData(
            accounts,
            _s.Library.Folders(),
            _s.Guard.LockedNames(),
            _s.Guard.RecentFailures(MostFailedAddresses),
            _s.Invites.PendingCounts(),
            ReadActivity(accounts, ActivityLimit),
            openRows.ToDictionary(id => id, _s.Devices.ForUser),
            _s.Settings.Load().Sharing);
    }

    private List<(SharingLogEntry Entry, string? Name)> ReadActivity(IReadOnlyList<Account> accounts, int limit)
    {
        var names = accounts.ToDictionary(a => a.Id, a => a.Name);
        return _s.Log.Recent(limit).Select(e => (e, e.UserId is { } id ? names.GetValueOrDefault(id) : null)).ToList();
    }

    private void Apply(PageData data)
    {
        _folders = data.Folders;
        _saved = data.Sharing;
        RouterSteps = StepsFor(data.Sharing.Port);
        if (!_fieldsFilled) FillFields(data.Sharing);

        var removedNear = ApplyPeople(data);
        ApplyActivity(data);
        FailedSignInsText = FailuresLine(data.Failures);
        ApplyStatus();
        IsLoaded = true;
        LoadProblem = null;
        OnPropertyChanged(nameof(HasNoActivity));
        if (removedNear is { } near) RowsRemoved?.Invoke(near.Row);
    }

    // A null result means no row or device went.
    private RemovedRow? ApplyPeople(PageData data)
    {
        var locked = data.Locked.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shown = People.ToDictionary(row => row.Id);
        PersonRow? invitesGone = null;
        var wanted = data.Accounts
            .OrderBy(a => a.Role == AccountRole.Owner ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(account =>
            {
                var row = shown.GetValueOrDefault(account.Id) ?? new PersonRow(account.Id, account.Role);
                var hadInvites = row.HasPendingInvites;
                row.Apply(account, locked.Contains(account.Name), SeenText(account), data.PendingInvites.GetValueOrDefault(account.Id));
                if (hadInvites && !row.HasPendingInvites) invitesGone ??= row;
                return row;
            })
            .ToList();

        // Cancel invites hides with the last invite, and it may have had the focus.
        RemovedRow? removed = invitesGone is null ? null : new RemovedRow(invitesGone);
        var staying = wanted.ToHashSet();
        var firstGone = People.ToList().FindIndex(row => !staying.Contains(row));
        if (firstGone >= 0) removed ??= new RemovedRow(wanted.ElementAtOrDefault(firstGone) ?? wanted.LastOrDefault());
        CollectionSync.Sync(People, wanted);

        foreach (var row in wanted.Where(row => row is { Detail: not null, Account: not null }))
        {
            if (_folderWrites == 0) row.Detail!.ApplyFolders(row.Account!, data.Folders);
            if (data.Devices.TryGetValue(row.Id, out var devices) && row.Detail!.ApplyDevices(devices, DeviceText)) removed ??= new RemovedRow(row);
        }

        return removed;
    }

    private void ApplyActivity(PageData data)
    {
        var shown = Activity.ToDictionary(row => row.Id);
        CollectionSync.Sync(Activity, data.Activity.Select(e => shown.GetValueOrDefault(e.Entry.Id) ?? ActivityRow(e.Entry, e.Name)).ToList());
    }

    private void FillFields(SharingSettings saved)
    {
        _fieldsFilled = true;
        PublicAddress = saved.PublicAddress ?? "";
        HomeAddress = saved.HomeAddress ?? "";
        ServerName = saved.ServerName ?? "";
        PortText = saved.Port.ToString(CultureInfo.InvariantCulture);
        ConnectionMessage = null;

        _applying = true;
        StartsWithWindows = saved.StartWithWindows;
        _applying = false;
    }

    private void ArmReload()
    {
        _reloadTimer?.Dispose();
        _reloadTimer = _s.Time.CreateTimer(_ => Dispatcher.UIThread.Post(OnReloadDue), null, PageReload, Timeout.InfiniteTimeSpan);
    }

    // Last seen, sign-ins and locks change with no event to say so. With sharing off, nothing can change them.
    private void OnReloadDue()
    {
        if (!_open) return;

        if (_s.Controller.Status == SharingStatus.On) _ = RefreshAsync();
        ArmReload();
    }

    // ---- The switch and its status ----

    // Any thread, and more than once for one change.
    private void OnControllerChanged()
    {
        if (Interlocked.Exchange(ref _statusQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _statusQueued, 0);
            ApplyStatus();
        });
    }

    private void ApplyStatus()
    {
        var controller = _s.Controller;
        var status = controller.Status;

        // A failed resume stays on in the settings, and showing it on is what lets the owner turn it off.
        _applying = true;
        IsOn = status is SharingStatus.On or SharingStatus.Starting || controller.ComesBackAtLaunch;
        _applying = false;

        StatusLine = status switch
        {
            SharingStatus.On => string.Create(CultureInfo.InvariantCulture, $"Sharing on port {controller.Port}"),
            SharingStatus.Starting => "Starting…",
            SharingStatus.Failed => "Sharing didn't start",
            _ => OffLine,
        };
        StatusDetail = status == SharingStatus.On ? $"{ShownAddress()} · {WatchingCount(controller.Watching)}"
            : controller.ComesBackAtLaunch ? "MediaBoard tries again the next time it starts, unless you turn this off."
            : "";
        Problem = controller.Problem;
        Warning = controller.Warning;
        ShowFirewallNote = status is SharingStatus.Off or SharingStatus.Failed;
    }

    private string ShownAddress() =>
        new[] { _saved.PublicAddress, _saved.HomeAddress }.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "No address yet";

    private static string WatchingCount(int count) =>
        count == 0 ? "Nobody watching" : string.Create(CultureInfo.InvariantCulture, $"{count} watching");

    // _applying marks the page following the controller. Anything else is the owner flipping the switch.
    partial void OnIsOnChanged(bool value)
    {
        if (!_applying) _ = FlipAsync(value);
    }

    private async Task FlipAsync(bool on)
    {
        try
        {
            if (!on && _s.Controller.Watching is > 0 and var watching &&
                !await _dialogs.Confirm(OffQuestion(watching), "Turn off", true))
            {
                ApplyStatus();
                return;
            }

            await _s.Controller.SetOnAsync(on);
        }
        catch (Exception e)
        {
            Report(e, "Couldn't change the sharing switch.");
        }

        ApplyStatus();
    }

    private static string OffQuestion(int watching) => watching == 1
        ? "Someone is watching. Turn sharing off and stop their stream?"
        : string.Create(CultureInfo.InvariantCulture, $"{watching} streams are playing. Turn sharing off and stop them?");

    // ---- Connection ----

    // The switch reads on through a start and a failed resume, so "turn it on" would make no sense.
    private bool SwitchedOnButNotRunning => _s.Controller.Status == SharingStatus.Starting || _s.Controller.ComesBackAtLaunch;

    private bool HasAddress => !string.IsNullOrWhiteSpace(_saved.PublicAddress) || !string.IsNullOrWhiteSpace(_saved.HomeAddress);

    partial void OnPublicAddressChanged(string value)
    {
        ConnectionMessage = null;
        ClearCheckResult();
    }

    partial void OnHomeAddressChanged(string value) => ConnectionMessage = null;

    partial void OnServerNameChanged(string value) => ConnectionMessage = null;

    partial void OnPortTextChanged(string value)
    {
        ConnectionMessage = null;
        ClearCheckResult();
    }

    // A check under way keeps saying so, and drops its own answer when it ends.
    private void ClearCheckResult()
    {
        if (!_checking) CheckResult = null;
    }

    partial void OnStartsWithWindowsChanged(bool value)
    {
        if (!_applying) _ = ChangeStartWithWindowsAsync(value);
    }

    // Only the owner's change comes here. Writing the key to re-apply a value would, on a settings failure, undo it to the wrong one.
    private async Task ChangeStartWithWindowsAsync(bool on)
    {
        ConnectionMessage = null;
        bool changed;
        try
        {
            changed = await InTurnAsync(() => _s.StartWithWindows.Set(on));
        }
        catch (Exception e)
        {
            Report(e, "Couldn't change Start with Windows.");
            changed = false;
        }

        if (changed) return;

        _applying = true;
        StartsWithWindows = !on;
        _applying = false;
        ConnectionMessage = "Couldn't change Start with Windows. See the activity panel.";
    }

    [RelayCommand]
    private void ToggleConnection() => IsConnectionOpen = !IsConnectionOpen;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SaveConnectionAsync()
    {
        if (!int.TryParse(PortText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            ConnectionMessage = "The port is a number from 1 to 65535.";
            return;
        }

        var publicAddress = NullIfBlank(PublicAddress);
        var homeAddress = NullIfBlank(HomeAddress);
        var serverName = NullIfBlank(ServerName);
        if (new[] { publicAddress, homeAddress }.OfType<string>().Any(address => !SharingController.IsUsableAddress(address)))
        {
            ConnectionMessage = "That address can't be used. Enter a name or IP address, like myhome.example.org.";
            return;
        }

        var nameChanged = !string.Equals(serverName, _saved.ServerName, StringComparison.Ordinal);
        try
        {
            _saved = await InTurnAsync(() =>
            {
                _s.Settings.Update(s => s with
                {
                    Sharing = s.Sharing with { PublicAddress = publicAddress, HomeAddress = homeAddress, ServerName = serverName, Port = port },
                });
                return _s.Settings.Load().Sharing;
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report(e, "Couldn't save the connection settings.");
            ConnectionMessage = ActionFailed;
            return;
        }

        PublicAddress = publicAddress ?? "";
        HomeAddress = homeAddress ?? "";
        ServerName = serverName ?? "";
        PortText = port.ToString(CultureInfo.InvariantCulture);
        RouterSteps = StepsFor(port);
        ApplyStatus();
        ConnectionMessage = SavedMessage(_s.Controller.Port is { } running && running != port, nameChanged && _s.Controller.Status == SharingStatus.On);
    }

    // The server took its port and name as it started.
    private static string SavedMessage(bool newPort, bool newName) => (newPort, newName) switch
    {
        (true, true) => "Saved. The new port and library name apply the next time sharing starts.",
        (true, false) => "Saved. The new port applies the next time sharing starts.",
        (false, true) => "Saved. The new library name applies the next time sharing starts.",
        _ => "Saved",
    };

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string[] StepsFor(int port) =>
    [
        "On your router, give this PC a fixed address. Routers often call this a DHCP reservation.",
        string.Create(CultureInfo.InvariantCulture, $"Forward TCP port {port} to this PC."),
        "Enter your public address above. A dynamic DNS name keeps working if the address changes.",
    ];

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CheckConnectionAsync()
    {
        if (_checking) return;
        if (_s.Controller.Status != SharingStatus.On)
        {
            CheckResult = SwitchedOnButNotRunning ? "Sharing isn't running, so there's nothing to check." : "Turn sharing on first.";
            return;
        }

        // The check goes to the saved address, so it would say nothing about one that's only typed.
        if (!string.Equals(NullIfBlank(PublicAddress), _saved.PublicAddress, StringComparison.Ordinal) ||
            !string.Equals(PortText.Trim(), _saved.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            CheckResult = "Save first.";
            return;
        }

        var checkedAddress = PublicAddress;
        var checkedPort = PortText;
        _checking = true;
        CheckResult = Checking;
        string? result;
        try
        {
            result = await Task.Run(() => _s.Controller.CheckConnectionAsync(CancellationToken.None)) switch
            {
                Sharing.CheckResult.Reachable => "Reached this PC. The port forward looks right.",
                Sharing.CheckResult.NoAddress => "Enter a public address first.",
                _ => "No answer, which proves little: many routers can't loop back to themselves. If a person's last seen updates, it works.",
            };
        }
        catch (Exception e)
        {
            Report(e, "Check connection failed.");
            result = ActionFailed;
        }

        _checking = false;
        var sameFields = string.Equals(PublicAddress, checkedAddress, StringComparison.Ordinal) && string.Equals(PortText, checkedPort, StringComparison.Ordinal);
        CheckResult = sameFields ? result : null;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ReplaceCertificateAsync() => ActAsync(null, async () =>
    {
        ConnectionMessage = null;
        if (!await _dialogs.Confirm(
                "Replace the certificate? Every device stops connecting and everyone needs a new invite. Invites not used yet stop working.",
                "Replace",
                true))
        {
            return;
        }

        // One turn, so an invite made meanwhile can't carry the old fingerprint or be swept up with the old ones.
        await _turn.WaitAsync();
        try
        {
            if (!await _s.Controller.ReplaceCertificateAsync()) return;

            await Task.Run(CancelPendingInvites);
        }
        finally
        {
            _turn.Release();
        }

        ConnectionMessage = "Certificate replaced. Everyone needs a new invite.";
    });

    // Thread pool. These invites carry the old fingerprint, so they would fail the client's pin.
    private void CancelPendingInvites()
    {
        foreach (var account in _s.People.All()) CancelInvitesOf(account.Id);
    }

    // ---- Watching now ----

    // Any thread, and as often as players report.
    private void OnStreamsChanged()
    {
        if (_open) _watching.Request();
    }

    private void RefreshWatching()
    {
        if (!_open) return;

        var sessions = _s.Streams.Current;
        var shown = Watching.ToDictionary(row => row.Id);
        var rows = sessions.Select(session =>
        {
            var row = shown.GetValueOrDefault(session.Id) ?? new WatchingRow(session);
            row.Apply(session);
            return row;
        }).ToList();
        var gone = shown.Count > rows.Count(row => shown.ContainsKey(row.Id));

        CollectionSync.Sync(Watching, rows);
        OnPropertyChanged(nameof(IsNobodyWatching));

        // Rates move with no event, so while there are rows the next refresh is always due.
        if (rows.Count > 0) _watching.Request();
        if (gone) RowsRemoved?.Invoke(null);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StopAsync(WatchingRow? row)
    {
        if (row is null || row.Stopping) return;

        row.Stopping = true;
        try
        {
            await Task.Run(() =>
            {
                if (_s.Streams.Stop(row.Id)) _s.Log.Add(SharingEvent.StreamStopped, row.UserId, row.Title);
            });
        }
        catch (Exception e)
        {
            Report(e, "Couldn't stop the stream.");
        }

        RefreshWatching();
        await RefreshAsync();
    }

    // ---- People ----

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleRowAsync(PersonRow? row)
    {
        if (row is null) return Task.CompletedTask;

        row.IsOpen = !row.IsOpen;
        row.Detail = row.IsOpen ? NewDetail(row) : null;
        if (!row.IsOpen) row.Message = null;
        return row.IsOpen ? RefreshAsync() : Task.CompletedTask;
    }

    private PersonDetail NewDetail(PersonRow row)
    {
        var detail = new PersonDetail(row, OnFoldersChosen);
        if (row.Account is { } account) detail.ApplyFolders(account, _folders);
        return detail;
    }

    // Applied at once, on each tick. A reload already under way read the old choice, so it's dropped.
    private void OnFoldersChosen(PersonDetail detail)
    {
        _query.Discard();
        _folderWrites++;
        _ = WriteFoldersAsync(detail.Row, detail.ChosenFolders);
    }

    private async Task WriteFoldersAsync(PersonRow row, IReadOnlyCollection<int>? folders)
    {
        try
        {
            await InTurnAsync(() => _s.Accounts.SetFolders(row.Id, folders));
        }
        catch (Exception e)
        {
            Report(e, "Couldn't change the folders.");
            row.Message = ActionFailed;
        }

        if (--_folderWrites == 0) await RefreshAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task AddPersonAsync() => ActAsync(null, async () =>
    {
        if (!HasAddress)
        {
            PointAtConnection();
            return;
        }

        var panel = new SharingPanel();
        var folders = _folders.Select(folder => new FolderChoice(folder, false)).ToList();
        panel.Step = new AddPersonStep(folders, step => CreatePersonAsync(panel, step));
        await ShowAsync(panel);
    });

    private async Task CreatePersonAsync(SharingPanel panel, AddPersonStep step)
    {
        var name = step.Name.Trim();
        var folders = step.ChosenFolders;
        step.Message = null;

        Account account;
        try
        {
            account = await InTurnAsync(() => _s.Accounts.AddPerson(name, folders));
        }
        catch (DuplicateNameException)
        {
            step.Message = $"{name} is already on the list.";
            return;
        }
        catch (Exception e)
        {
            Report(e, "Couldn't add the person.");
            step.Message = ActionFailed;
            return;
        }

        // The person exists now, so asking again could only say the name is taken.
        var invite = await TryInviteAsync(account.Id, why => PeopleMessage = $"{name} is added, but no invite was made. {why}");
        if (invite is null)
        {
            panel.Finish();
            return;
        }

        panel.Step = InviteFor(name, isOwner: false, invite);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task NewInviteAsync(PersonRow? row) => row is not { CanInvite: true } ? Task.CompletedTask : ActAsync(row, async () =>
    {
        if (!HasAddress)
        {
            PointAtConnection();
            return;
        }

        if (await TryInviteAsync(row.Id, why => row.Message = $"No invite was made. {why}") is { } invite) await ShowInviteAsync(row, invite);
    });

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ResetPasswordAsync(PersonRow? row) => row is not { CanResetPassword: true } ? Task.CompletedTask : ActAsync(row, async () =>
    {
        if (!HasAddress)
        {
            PointAtConnection();
            return;
        }

        var question = $"Reset {row.Name}'s password? Their devices sign out, and they'll need the new invite to get back in.";
        if (!await _dialogs.Confirm(question, "Reset", true)) return;

        await InTurnAsync(() => _s.Accounts.ResetPassword(row.Id));

        // After the reset, which deletes invites.
        if (await TryInviteAsync(row.Id, why => row.Message = $"The password is reset, but no invite was made. {why}") is { } invite)
            await ShowInviteAsync(row, invite);
    });

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SetPasswordAsync(PersonRow? row) => row is not { IsOwner: true } ? Task.CompletedTask : ActAsync(row, async () =>
    {
        var panel = new SharingPanel();
        panel.Step = new SetPasswordStep(step => SavePasswordAsync(panel, row, step));
        await ShowAsync(panel);
    });

    private async Task SavePasswordAsync(SharingPanel panel, PersonRow row, SetPasswordStep step)
    {
        var password = step.Password;
        if (!string.Equals(password, step.Repeat, StringComparison.Ordinal))
        {
            step.Message = "The two passwords don't match.";
            return;
        }

        step.Message = null;
        try
        {
            await InTurnAsync(() => _s.Accounts.SetOwnerPassword(password));
        }
        catch (PasswordRefusedException)
        {
            step.Message = string.Create(CultureInfo.InvariantCulture, $"A password needs at least {Passwords.MinLength} characters.");
            return;
        }
        catch (Exception e)
        {
            Report(e, "Couldn't save the password.");
            step.Message = ActionFailed;
            return;
        }

        row.Message = "Password set.";
        panel.Finish();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleDisabledAsync(PersonRow? row) => row is not { CanManage: true } ? Task.CompletedTask : ActAsync(row, async () =>
    {
        var disable = !row.IsDisabled;
        var question = $"Disable {row.Name}? Their devices sign out and their streams stop. After you enable them, they'll need to sign in again. Nothing is deleted.";
        if (disable && !await _dialogs.Confirm(question, "Disable", true)) return;

        await InTurnAsync(() => _s.Accounts.Disable(row.Id, disable));
    });

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RemoveAsync(PersonRow? row) => row is not { CanManage: true } ? Task.CompletedTask : ActAsync(row, async () =>
    {
        var question = $"Remove {row.Name}? Their watch progress and devices are deleted, and this can't be undone.";
        if (await _dialogs.Confirm(question, "Remove", true)) await InTurnAsync(() => _s.Accounts.Remove(row.Id));
    });

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SignOutAsync(DeviceRow? device) =>
        device is null ? Task.CompletedTask : ActAsync(People.FirstOrDefault(row => row.Id == device.UserId), () => InTurnAsync(() => _s.Accounts.SignOutDevice(device.Id)));

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task CancelInvitesAsync(PersonRow? row) =>
        row is null ? Task.CompletedTask : ActAsync(row, () => InTurnAsync(() => CancelInvitesOf(row.Id)));

    // Thread pool.
    private void CancelInvitesOf(int userId)
    {
        foreach (var invite in _s.Invites.Pending(userId)) _s.Invites.Cancel(invite.Id);
    }

    private void PointAtConnection()
    {
        IsConnectionOpen = true;
        ConnectionMessage = NoAddressPrompt;
        ConnectionNeeded?.Invoke();
    }

    /// <returns>The invite, or null after saying through <paramref name="say"/> why there is none.</returns>
    private async Task<string?> TryInviteAsync(int userId, Action<string> say)
    {
        try
        {
            return await InTurnAsync(() => InviteReplacingOlder(userId));
        }
        catch (InvalidOperationException)
        {
            say("The address can't be used. Fix it under Connection, then use New invite.");
            PointAtConnection();
        }
        catch (CryptographicException)
        {
            say("The sharing certificate can't be read. Replace it under Connection, then use New invite.");
            IsConnectionOpen = true;
        }
        catch (Exception e)
        {
            Report(e, "Couldn't make the invite.");
            say(ActionFailed);
        }

        return null;
    }

    // Thread pool. The older ones go only once the new one exists, so a failure leaves them as they were.
    private string InviteReplacingOlder(int userId)
    {
        var older = _s.Invites.Pending(userId);
        var invite = _s.Controller.CreateInvite(userId);
        foreach (var pending in older) _s.Invites.Cancel(pending.Id);
        return invite;
    }

    private async Task ShowInviteAsync(PersonRow row, string invite)
    {
        var panel = new SharingPanel { Step = InviteFor(row.Name, row.IsOwner, invite) };
        await ShowAsync(panel);
    }

    private InviteStep InviteFor(string name, bool isOwner, string invite)
    {
        var life = "It works once, within 24 hours. You won't see it again, but you can make another.";
        if (SwitchedOnButNotRunning) life += " Sharing isn't running, so it won't work until sharing starts.";
        else if (_s.Controller.Status != SharingStatus.On) life += " Sharing is off, so it won't work until you turn it on.";
        return isOwner
            ? new InviteStep("Invite for your other PC", $"Paste this into MediaBoard on your other PC. {life}", invite, _dialogs.Copy)
            : new InviteStep($"Invite for {name}", $"Send this to {name}. {life}", invite, _dialogs.Copy);
    }

    // An invite can't be read back, so it goes when the panel does.
    private async Task ShowAsync(SharingPanel panel)
    {
        try
        {
            await _dialogs.ShowPanel(panel);
        }
        finally
        {
            panel.Step = null;
        }
    }

    // ---- Activity ----

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ShowAllActivityAsync() => ActAsync(null, async () =>
    {
        var rows = await InTurnAsync(() => ReadActivity(_s.People.All(), SharingLog.Cap).Select(e => ActivityRow(e.Entry, e.Name)).ToList());
        await ShowAsync(new SharingPanel { Step = new ActivityStep(rows) });
    });

    private SharingActivityRow ActivityRow(SharingLogEntry entry, string? name) => new(entry.Id, WhenText(entry.At), Words(entry, name));

    private static string? FailuresLine(IReadOnlyList<FailedSignIns> failures) => failures.Count == 0
        ? null
        : "Wrong passwords in the last hour: "
          + string.Join(", ", failures.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Count} from {f.Address}{(f.Locked ? " (blocked)" : "")}")));

    private static string Words(SharingLogEntry entry, string? name)
    {
        var who = name ?? "Someone";
        var whose = name is null ? "Someone's" : $"{name}'s";
        return entry.Kind switch
        {
            SharingEvent.Joined => $"{who} joined on {entry.Detail}",
            SharingEvent.SignedIn => $"{who} signed in on {entry.Detail}",
            SharingEvent.SignInLocked when name is null => "A sign-in was locked after too many tries",
            SharingEvent.SignInLocked => $"{whose} sign-in was locked after too many tries",
            SharingEvent.StartedWatching => $"{who} started {entry.Detail}",
            SharingEvent.FinishedWatching => $"{who} finished {entry.Detail}",
            SharingEvent.StreamStopped => $"{whose} stream was stopped: {entry.Detail}",
            SharingEvent.SignedOut => $"{whose} {entry.Detail} was signed out",
            SharingEvent.NewAddress => $"{whose} {entry.Detail}",
            SharingEvent.Disabled => $"{who} was disabled",
            SharingEvent.PasswordReset => $"{whose} password was reset",
            SharingEvent.Removed => $"{entry.Detail} was removed",
            _ => who,
        };
    }

    // ---- Times ----

    private DateTime Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _s.Time.LocalTimeZone);

    private string WhenText(DateTime utc)
    {
        var when = Local(utc);
        var today = Local(_s.Time.GetUtcNow().UtcDateTime).Date;
        return when.ToString(when.Date == today ? "HH:mm" : "d MMM HH:mm", CultureInfo.InvariantCulture);
    }

    private string SeenText(Account account) => account switch
    {
        { LastSeen: { } at } => SeenAgo(at),
        { Role: AccountRole.Owner } => "This PC",
        { HasPassword: false } => "Hasn't joined yet",
        _ => "Not seen yet",
    };

    private string SeenAgo(DateTime utc)
    {
        var ago = _s.Time.GetUtcNow().UtcDateTime - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (ago < TimeSpan.FromMinutes(1)) return "Seen just now";
        if (ago < TimeSpan.FromHours(1)) return string.Create(CultureInfo.InvariantCulture, $"Seen {(int)ago.TotalMinutes} min ago");
        if (ago < TimeSpan.FromDays(1)) return string.Create(CultureInfo.InvariantCulture, $"Seen {(int)ago.TotalHours} h ago");
        return "Seen " + Local(utc).ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    private string DeviceText(Device device) =>
        device.LastAddress is { } address ? $"{DeviceSeen(device)} · {address}" : DeviceSeen(device);

    private string DeviceSeen(Device device) => device.LastSeen is { } at ? SeenAgo(at) : "Not seen yet";

    // ---- Running actions ----

    // One button action at a time: a second press while the first is on its way does nothing. The buttons stay enabled
    // meanwhile, since disabling the focused one would drop the focus.
    private async Task ActAsync(PersonRow? row, Func<Task> action)
    {
        if (_acting) return;

        _acting = true;
        PeopleMessage = null;
        if (row is not null) row.Message = null;
        try
        {
            await action();
        }
        catch (Exception e)
        {
            Report(e, "A sharing action failed.");
            if (row is not null) row.Message = ActionFailed;
            else PeopleMessage = ActionFailed;
        }
        finally
        {
            _acting = false;
        }

        await RefreshAsync();
    }

    // The owner's writes run off the UI thread in the order they were asked for: folder ticks must land in tick order,
    // and an invite must follow the reset before it.
    private async Task<T> InTurnAsync<T>(Func<T> work)
    {
        await _turn.WaitAsync();
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            _turn.Release();
        }
    }

    private Task InTurnAsync(Action work) => InTurnAsync<object?>(() =>
    {
        work();
        return null;
    });

    // The type alone: a message can hold a path.
    private void Report(Exception e, string what)
    {
        Trace.TraceWarning($"{what} {e.GetType().Name}");
        _s.Hub.Log(ActivityLevel.Error, what, BackgroundQuery.FailureHint);
    }

    /// <param name="Row">Where the focus can go if it went with the row.</param>
    private readonly record struct RemovedRow(PersonRow? Row);

    private sealed record PageData(
        IReadOnlyList<Account> Accounts,
        IReadOnlyList<FolderRow> Folders,
        IReadOnlyList<string> Locked,
        IReadOnlyList<FailedSignIns> Failures,
        IReadOnlyDictionary<int, int> PendingInvites,
        List<(SharingLogEntry Entry, string? Name)> Activity,
        Dictionary<int, IReadOnlyList<Device>> Devices,
        SharingSettings Sharing);
}
