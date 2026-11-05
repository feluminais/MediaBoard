using System.Globalization;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;

namespace MediaBoard.Desktop.ViewModels;

public enum ConnectStep { Invite, NewPassword, KnownPassword, SignIn }

/// <summary>
/// Joining a server with an invite, or signing a saved device back in. The requests go through an <see cref="ApiClient"/>
/// of this screen's own, one per server, since the app's composition doesn't exist yet. A password stays in its field
/// only until the server has answered, and goes nowhere else. Success saves the connection and restarts into it.
/// </summary>
public sealed partial class ConnectViewModel : ObservableObject, IDisposable
{
    private const string RedeemRoute = "invites/redeem";
    private const string SignInRoute = "sign-in";

    private readonly SettingsStore _settings;
    private readonly string _deviceName;
    private readonly Func<bool> _restart;
    private readonly StoredConnection? _saved;
    private readonly CancellationTokenSource _closing = new();
    private ServerEndpoint? _endpoint;
    private ApiClient? _client;
    private ServerEndpoint? _clientEndpoint;
    private string? _code;
    private string? _token;
    private string? _accountName;
    private bool _finished;

    /// <param name="settings">Where the connection goes.</param>
    /// <param name="deviceName">What the owner sees this PC called.</param>
    /// <param name="restart">Restarts the app into the saved connection. False when it couldn't.</param>
    /// <param name="saved">The server of a signed-out device, or null.</param>
    public ConnectViewModel(SettingsStore settings, string deviceName, Func<bool> restart, StoredConnection? saved = null)
    {
        _settings = settings;
        _deviceName = deviceName;
        _restart = restart;
        if (saved is null) return;

        if (!ModeGate.HasUsableEndpoint(saved))
        {
            Message = "The saved server details can't be used. Ask for a new invite.";
            return;
        }

        _saved = saved;
        _endpoint = saved.Endpoint;
        Name = saved.AccountName;
        Step = ConnectStep.SignIn;
    }

    /// <summary>Back was pressed on the first step.</summary>
    public event Action? Left;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(Note), nameof(ActionLabel), nameof(ShowInvite), nameof(ShowName), nameof(ShowPassword), nameof(ShowRepeat), nameof(ShowUseInvite))]
    public partial ConnectStep Step { get; private set; }

    [ObservableProperty]
    public partial string InviteText { get; set; } = "";

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    /// <summary>The new password again.</summary>
    [ObservableProperty]
    public partial string Repeat { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    /// <summary>The main button sends the same thing again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    public partial bool CanRetry { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(BackCommand), nameof(UseInviteCommand))]
    public partial bool IsBusy { get; private set; }

    public bool HasMessage => Message is not null;

    public bool ShowInvite => Step == ConnectStep.Invite;

    public bool ShowName => Step == ConnectStep.SignIn;

    public bool ShowPassword => Step != ConnectStep.Invite;

    public bool ShowRepeat => Step == ConnectStep.NewPassword;

    public bool ShowUseInvite => Step == ConnectStep.SignIn;

    public string Heading => Step switch
    {
        ConnectStep.NewPassword => "Choose a password",
        ConnectStep.KnownPassword => "Enter your password",
        ConnectStep.SignIn => "Sign in",
        _ => "Connect to a server",
    };

    public string Note => Step switch
    {
        ConnectStep.NewPassword => $"For {ServerName}. {TooShortText}",
        ConnectStep.KnownPassword => $"You already have an account on {ServerName}.",
        ConnectStep.SignIn => ServerName,
        _ => "Paste the invite you were sent.",
    };

    public string ActionLabel
    {
        get
        {
            if (CanRetry) return "Retry";
            return Step == ConnectStep.Invite ? "Continue" : "Connect";
        }
    }

    private string ServerName => _endpoint?.ServerName ?? "";

    private static string TooShortText => string.Create(CultureInfo.InvariantCulture, $"Use at least {Passwords.MinLength} characters.");

    private bool CanAct => !IsBusy && !_finished;

    // A held token is a spent invite, so it can only be saved or left behind with the window.
    private bool CanLeave => CanAct && _token is null;

    public void Dispose()
    {
        _closing.Cancel();
        Forget();
        _client?.Dispose();
        _client = null;
        _clientEndpoint = null;
    }

    private static string RefusalText(ApiErrorCode code, ConnectStep step) => code switch
    {
        ApiErrorCode.InviteDead => "This invite no longer works. Ask for a new one.",
        ApiErrorCode.Locked => "Too many tries. Wait 15 minutes.",
        ApiErrorCode.Disabled => "This account is disabled. Ask the person who invited you.",
        ApiErrorCode.PasswordTooShort => TooShortText,
        ApiErrorCode.WrongPassword when step == ConnectStep.SignIn => "Wrong name or password.",
        ApiErrorCode.WrongPassword => "Wrong password. Try again.",
        _ => "The server turned this down. Try again.",
    };

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ContinueAsync()
    {
        Say(null);
        IsBusy = true;
        try
        {
            // With a token in hand the server has finished with the invite and the password, and neither can be sent twice.
            if (_token is null) await AskServerAsync();
            if (_token is not null) Finish();
        }
        catch (ApiException e)
        {
            ClearPasswords();
            if (e.Code == ApiErrorCode.InviteDead) Step = ConnectStep.Invite;
            Say(RefusalText(e.Code, Step));
        }
        catch (ServerUnreachableException)
        {
            Say($"Can't reach {ServerName}. Their PC may be off or asleep, or sharing is off.", canRetry: true);
        }
        catch (IdentityChangedException)
        {
            Say("This server's identity changed. Ask for a new invite.", canRetry: true);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException && _closing.IsCancellationRequested)
        {
            // The window closed, and a try that was waiting to move on to the next address met the disposed client.
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLeave))]
    private void Back()
    {
        Say(null);
        ClearPasswords();
        if (Step is ConnectStep.NewPassword or ConnectStep.KnownPassword)
        {
            Step = ConnectStep.Invite;
        }
        else if (Step == ConnectStep.Invite && _saved is not null)
        {
            _endpoint = _saved.Endpoint;
            Step = ConnectStep.SignIn;
        }
        else
        {
            Left?.Invoke();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLeave))]
    private void UseInvite()
    {
        Say(null);
        Step = ConnectStep.Invite;
    }

    private Task AskServerAsync() => Step switch
    {
        ConnectStep.NewPassword => SetPasswordAsync(),
        ConnectStep.KnownPassword => EnterPasswordAsync(),
        ConnectStep.SignIn => SignInAsync(),
        _ => ReadInviteAsync(),
    };

    /// <summary>A redeem with no password asks which form to show, and the server's answer is the refusal.</summary>
    private async Task ReadInviteAsync()
    {
        if (!InviteString.TryDecode(InviteText, out var invite))
        {
            Say("That doesn't look like an invite. Copy all of it, starting with mb1-.");
            return;
        }

        Use(new ServerEndpoint(invite.ServerName, invite.Addresses, invite.Fingerprint));
        _code = invite.Code;
        try
        {
            await RedeemAsync(null);
        }
        catch (ApiException e) when (e.Code is ApiErrorCode.PasswordTooShort)
        {
            Step = ConnectStep.NewPassword;
        }
        catch (ApiException e) when (e.Code is ApiErrorCode.WrongPassword)
        {
            Step = ConnectStep.KnownPassword;
        }
    }

    private async Task SetPasswordAsync()
    {
        if (Password.Length < Passwords.MinLength)
        {
            Say(TooShortText);
        }
        else if (!string.Equals(Password, Repeat, StringComparison.Ordinal))
        {
            Repeat = "";
            Say("The passwords don't match.");
        }
        else
        {
            await RedeemAsync(Password);
        }
    }

    private async Task EnterPasswordAsync()
    {
        if (Password.Length == 0) Say("Enter your password.");
        else await RedeemAsync(Password);
    }

    private async Task SignInAsync()
    {
        if (Name.Trim().Length == 0 || Password.Length == 0)
        {
            Say("Enter your name and password.");
            return;
        }

        Use(_endpoint!);
        var request = new SignInRequest { Name = Name.Trim(), Password = Password, DeviceName = _deviceName };
        Issued(await _client!.SendAsync<SessionReply>(HttpMethod.Post, SignInRoute, request, _closing.Token));
    }

    private async Task RedeemAsync(string? password)
    {
        var request = new RedeemRequest { Code = _code, Password = password, DeviceName = _deviceName };
        Issued(await _client!.SendAsync<SessionReply>(HttpMethod.Post, RedeemRoute, request, _closing.Token));
    }

    private void Issued(SessionReply reply)
    {
        _token = reply.Token;
        _accountName = reply.Name;
        ClearPasswords();
    }

    // Saved even when the window has just closed: the invite is spent, and this is all that's left of it.
    private void Finish()
    {
        if (!Save()) return;

        _finished = true;
        Forget();
        if (_closing.IsCancellationRequested) return;

        if (!_restart()) Say("You're connected. Restart MediaBoard to open the library.");
    }

    private bool Save()
    {
        try
        {
            var connection = new StoredConnection(_endpoint!, _accountName!, _deviceName, RemoteSession.Protect(_token!));
            _settings.Update(s => s with { Mode = AppMode.Server, Connection = connection });
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Say("Couldn't save this connection. Check that MediaBoard can write to its settings file, then try again.", canRetry: true);
            return false;
        }
    }

    // Compares with what the client was made for: the screen's endpoint moves with Back while the client stays.
    private void Use(ServerEndpoint endpoint)
    {
        _endpoint = endpoint;
        if (_client is not null && ReferenceEquals(_clientEndpoint, endpoint)) return;

        _client?.Dispose();
        _clientEndpoint = endpoint;

        // Nothing here listens to the connection: a failure comes back from the request that met it. Requests here are
        // sign-ins, so none carries a token.
        _client = new ApiClient(endpoint, () => null, _ => { });
    }

    private void Say(string? message, bool canRetry = false)
    {
        Message = message;
        CanRetry = canRetry;
    }

    private void ClearPasswords()
    {
        Password = "";
        Repeat = "";
    }

    private void Forget()
    {
        ClearPasswords();
        InviteText = "";
        _code = null;
        _token = null;
        _accountName = null;
    }

    partial void OnStepChanged(ConnectStep value) => ClearPasswords();

    partial void OnInviteTextChanged(string value) => Typed();

    partial void OnNameChanged(string value) => Typed();

    partial void OnPasswordChanged(string value) => Typed();

    partial void OnRepeatChanged(string value) => Typed();

    // What the message was about has changed. A field emptied by code leaves its message alone: the code says it next.
    private void Typed()
    {
        if (!IsBusy) Say(null);
    }
}
