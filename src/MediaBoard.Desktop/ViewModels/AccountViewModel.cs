using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Remote;
using MediaBoard.Desktop.Remote;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>
/// The account section of a viewer's Settings: who they are here, changing the password, and the two ways out. A password
/// stays in its field until the server has answered, and goes nowhere else.
/// </summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private readonly ViewerAccount _account;
    private readonly ModeSwitch _mode;
    private readonly ActivityHub _hub;
    private readonly Func<string, string, Task<bool>> _confirm;

    /// <param name="account">This PC's place on the server.</param>
    /// <param name="mode">Goes to this PC's library.</param>
    /// <param name="hub">Says whether a file plays.</param>
    /// <param name="confirm">Asks a question before leaving, with the label of the button that goes ahead; true when it's pressed.</param>
    public AccountViewModel(ViewerAccount account, ModeSwitch mode, ActivityHub hub, Func<string, string, Task<bool>> confirm)
    {
        _account = account;
        _mode = mode;
        _hub = hub;
        _confirm = confirm;
    }

    public string ServerName => _account.ServerName;

    public string AccountName => _account.AccountName;

    [ObservableProperty]
    public partial bool IsChangingPassword { get; private set; }

    [ObservableProperty]
    public partial string CurrentPassword { get; set; } = "";

    [ObservableProperty]
    public partial string NewPassword { get; set; } = "";

    /// <summary>The new password again.</summary>
    [ObservableProperty]
    public partial string RepeatPassword { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPasswordMessage))]
    public partial string? PasswordMessage { get; private set; }

    /// <summary>Set when leaving couldn't restart the app.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLeaveMessage))]
    public partial string? LeaveMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePasswordCommand), nameof(CancelChangePasswordCommand), nameof(SignOutCommand), nameof(UseThisPcCommand))]
    public partial bool IsBusy { get; private set; }

    public bool HasPasswordMessage => PasswordMessage is not null;

    public bool HasLeaveMessage => LeaveMessage is not null;

    private static string TooShort => string.Create(CultureInfo.InvariantCulture, $"Use at least {Passwords.MinLength} characters.");

    private bool CanAct => !IsBusy;

    private string PlayerClosing => _hub.Current.PlayerName is null ? "" : " The player closes.";

    private static string RefusalText(ApiErrorCode code) => code switch
    {
        ApiErrorCode.WrongPassword => "Wrong password. Try again.",
        ApiErrorCode.Locked => FailureText.Locked,
        ApiErrorCode.PasswordTooShort => TooShort,
        _ => "The server turned this down. Try again.",
    };

    [RelayCommand]
    private void BeginChangePassword()
    {
        PasswordMessage = null;
        IsChangingPassword = true;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void CancelChangePassword()
    {
        ClearPasswords();
        PasswordMessage = null;
        IsChangingPassword = false;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SavePasswordAsync()
    {
        if (NewPassword.Length < Passwords.MinLength)
        {
            PasswordMessage = TooShort;
            return;
        }

        if (!string.Equals(NewPassword, RepeatPassword, StringComparison.Ordinal))
        {
            RepeatPassword = "";
            PasswordMessage = "The passwords don't match.";
            return;
        }

        PasswordMessage = null;
        IsBusy = true;
        try
        {
            await _account.ChangePasswordAsync(CurrentPassword, NewPassword, CancellationToken.None);
            IsChangingPassword = false;
            PasswordMessage = "Password changed.";
        }
        catch (ApiException e)
        {
            PasswordMessage = RefusalText(e.Code);
        }
        catch (Exception e) when (e is ServerUnreachableException or IdentityChangedException)
        {
            PasswordMessage = "Can't reach the server. Try again when it's back.";
        }
        finally
        {
            ClearPasswords();
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SignOutAsync()
    {
        if (!await _confirm($"Sign out of {ServerName}? You'll need your password to come back.{PlayerClosing}", "Sign out")) return;

        LeaveMessage = null;
        IsBusy = true;
        try
        {
            if (!await _account.SignOutAsync(CancellationToken.None)) LeaveMessage = "You're signed out. Restart MediaBoard to sign in again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task UseThisPcAsync()
    {
        if (PlayerClosing.Length > 0 && !await _confirm($"Use this PC's library?{PlayerClosing}", "Use this PC")) return;

        LeaveMessage = _mode.UseThisPc() ? null : "Restart MediaBoard to open this PC's library.";
    }

    private void ClearPasswords()
    {
        CurrentPassword = "";
        NewPassword = "";
        RepeatPassword = "";
    }
}
