using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CozyCouch.Core.Accounts;

namespace CozyCouch.Desktop.ViewModels;

/// <summary>
/// The modal dialog of the Sharing page. <see cref="Step"/> is what it shows: adding a person moves on from the question
/// to the invite in the same dialog. An invite exists nowhere else, so the page clears the step when the dialog closes.
/// </summary>
public sealed partial class SharingPanel : ObservableObject
{
    private bool _finished;

    /// <summary>Raised when a step is done, or the owner pressed a closing button.</summary>
    public event Action? CloseRequested;

    /// <summary>An <see cref="AddPersonStep"/>, <see cref="SetPasswordStep"/>, <see cref="InviteStep"/> or <see cref="ActivityStep"/>.</summary>
    [ObservableProperty]
    public partial object? Step { get; set; }

    /// <summary>
    /// Whether the owner's Close, Escape or X may go ahead. While a step runs the answer is no, since its result would be
    /// lost. An invite not copied gets one warning first, since it can't be shown again.
    /// </summary>
    public bool MayClose() => _finished || Step switch
    {
        AddPersonStep { IsBusy: true } or SetPasswordStep { IsBusy: true } => false,
        InviteStep invite => invite.MayClose(),
        _ => true,
    };

    /// <summary>The step is done, so nothing holds the panel open.</summary>
    public void Finish()
    {
        _finished = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    internal void Close() => CloseRequested?.Invoke();
}

/// <summary>Asks for a name and folders.</summary>
/// <param name="folders">The library's folders, none of them ticked.</param>
/// <param name="create">Makes the person. Says why not through <see cref="Message"/>.</param>
public sealed partial class AddPersonStep(IReadOnlyList<FolderChoice> folders, Func<AddPersonStep, Task> create) : ObservableObject
{
    public IReadOnlyList<FolderChoice> Folders { get; } = folders;

    public string AddLabel => IsBusy ? "Adding…" : "Add";

    public bool HasNoFolders => !AllFolders && Folders.Count == 0;

    /// <summary>The person is being added. The panel stays open meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddLabel))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string Name { get; set; } = "";

    /// <summary>On, the person also gets folders added later.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFolders))]
    public partial bool AllFolders { get; set; } = true;

    /// <summary>Why the last try did nothing; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    public bool HasMessage => Message is not null;

    /// <summary>Null for all folders, or the ticked ones.</summary>
    internal IReadOnlyCollection<int>? ChosenFolders =>
        AllFolders ? null : Folders.Where(f => f.IsChecked).Select(f => f.Id).ToList();

    private bool CanCreate => !string.IsNullOrWhiteSpace(Name);

    partial void OnNameChanged(string value) => Message = null;

    // Stays pressable while it runs, since a disabled button would drop the focus. A second press does nothing.
    [RelayCommand(CanExecute = nameof(CanCreate), AllowConcurrentExecutions = true)]
    private async Task CreateAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        try
        {
            await create(this);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Asks for the owner's password, for signing in from another PC.</summary>
/// <param name="save">Saves it, and says why not through <see cref="Message"/>.</param>
public sealed partial class SetPasswordStep(Func<SetPasswordStep, Task> save) : ObservableObject
{
    public string Hint { get; } = string.Create(CultureInfo.InvariantCulture, $"For signing in from another PC. At least {Passwords.MinLength} characters.");

    public string SaveLabel => IsBusy ? "Saving…" : "Save";

    /// <summary>The password is being saved, which takes a moment. The panel stays open meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveLabel))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string Repeat { get; set; } = "";

    /// <summary>Why the last try did nothing; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    public bool HasMessage => Message is not null;

    // Stays pressable while it runs, since a disabled button would drop the focus. A second press does nothing.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SaveAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        try
        {
            await save(this);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Shows an invite, the one time it can be seen.</summary>
/// <param name="heading">Who it's for.</param>
/// <param name="note">What to do with it.</param>
/// <param name="invite">The invite.</param>
/// <param name="copy">Puts text on the clipboard.</param>
public sealed partial class InviteStep(string heading, string note, string invite, Func<string, Task> copy) : ObservableObject
{
    private bool _copied;
    private bool _warned;

    public string Heading { get; } = heading;

    public string Note { get; } = note;

    public string Invite { get; } = invite;

    [ObservableProperty]
    public partial string CopyLabel { get; private set; } = "Copy";

    [ObservableProperty]
    public partial string DoneLabel { get; private set; } = "Done";

    /// <summary>Why copying failed, or that the invite wasn't copied; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    public bool HasMessage => Message is not null;

    /// <summary>The first try to close an invite that wasn't copied says so, and the next goes ahead.</summary>
    internal bool MayClose()
    {
        if (_copied || _warned) return true;

        _warned = true;
        Message = "You haven't copied this invite, and you won't see it again.";
        DoneLabel = "Close anyway";
        return false;
    }

    // The button stays pressable while this runs, since a disabled button would drop the focus.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CopyAsync()
    {
        Message = null;
        try
        {
            await copy(Invite);
            _copied = true;
            CopyLabel = "Copied";
            DoneLabel = "Done";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Another program can hold the clipboard, and the text is right there to select.
            CopyLabel = "Copy";
            Message = "Couldn't copy. Select the text and press Ctrl+C.";
        }
    }
}

/// <summary>The full log.</summary>
/// <param name="Rows">Up to about 5,000 lines, newest first.</param>
public sealed record ActivityStep(IReadOnlyList<SharingActivityRow> Rows);
