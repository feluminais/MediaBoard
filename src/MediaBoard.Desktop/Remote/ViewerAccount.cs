using System.Diagnostics;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;

namespace MediaBoard.Desktop.Remote;

/// <summary>The token this run's requests carry, opened on first use.</summary>
/// <param name="stored">The token as <see cref="RemoteSession.Protect"/> left it, or null.</param>
public sealed class DeviceToken(string? stored)
{
    private readonly Lazy<string?> _opened = new(() => RemoteSession.TryUnprotect(stored));
    private volatile bool _forgotten;

    public string? Read() => _forgotten ? null : _opened.Value;

    /// <summary>From here on this run has no session, whether or not the server heard about it.</summary>
    public void Forget() => _forgotten = true;
}

/// <summary>
/// This PC's place on the server: who it's signed in as, and what ends that. The saved token goes as soon as anything
/// says it's dead, and the server's addresses stay, so the next start lands on Connect with that server filled in. A
/// token that can't be opened here ends the same way, since the server answers it with a sign-out.
/// </summary>
public sealed class ViewerAccount
{
    private static readonly TimeSpan LastSendLimit = TimeSpan.FromSeconds(3);

    private readonly StoredConnection _connection;
    private readonly SettingsStore _settings;
    private readonly ApiClient _client;
    private readonly DeviceToken _token;
    private readonly ConnectionMonitor _monitor;
    private readonly IPlaybackService _playback;
    private readonly ProgressOutbox _outbox;
    private readonly AppRestart _restart;
    private readonly TimeProvider _time;

    /// <param name="connection">The connection this run started on.</param>
    /// <param name="settings">Where it's saved.</param>
    /// <param name="client">The client it runs through.</param>
    /// <param name="token">The token that client sends.</param>
    /// <param name="monitor">Says when the server has ended it.</param>
    /// <param name="playback">Closes the player before the session goes.</param>
    /// <param name="outbox">Holds the progress that must reach the server first.</param>
    /// <param name="restart">Starts the app over, into the Connect screen.</param>
    /// <param name="time">Times the last send.</param>
    public ViewerAccount(
        StoredConnection connection,
        SettingsStore settings,
        ApiClient client,
        DeviceToken token,
        ConnectionMonitor monitor,
        IPlaybackService playback,
        ProgressOutbox outbox,
        AppRestart restart,
        TimeProvider time)
    {
        _connection = connection;
        _settings = settings;
        _client = client;
        _token = token;
        _monitor = monitor;
        _playback = playback;
        _outbox = outbox;
        _restart = restart;
        _time = time;
        monitor.Changed += OnConnectionChanged;
    }

    public string ServerName => _connection.Endpoint.ServerName;

    public string AccountName => _connection.AccountName;

    /// <exception cref="ApiException">The server refused.</exception>
    /// <exception cref="ServerUnreachableException">No answer.</exception>
    /// <exception cref="IdentityChangedException">The server's certificate changed.</exception>
    public Task ChangePasswordAsync(string current, string next, CancellationToken ct) =>
        _client.SendAsync(HttpMethod.Put, "me/password", new ChangePasswordRequest { Current = current, Next = next }, ct);

    /// <summary>
    /// Ends this device's session. The position goes first, since the token that carries it dies with the sign-out. A server
    /// that can't be told keeps its side of the session, which the owner can end.
    /// </summary>
    /// <returns>False when the app couldn't restart. This run then has no session and stops trying the server.</returns>
    public async Task<bool> SignOutAsync(CancellationToken ct)
    {
        var mayHear = !ConnectionMonitor.IsFinal(_monitor.State);
        await CloseThePlayerAsync();
        if (mayHear)
        {
            // Nothing would get through, and the wait is one the viewer sits out.
            if (_monitor.State != ConnectionState.Unreachable) await SendWhatsWaitingAsync();
            await TellTheServerAsync(ct);
        }

        ForgetToken();
        if (_restart.Run()) return true;

        _monitor.Report(ConnectionEvent.SignedOut);
        return false;
    }

    /// <summary>For a viewer the server has already signed out.</summary>
    /// <returns>False when the app couldn't restart.</returns>
    public bool ReturnToConnect() => _restart.Run();

    private async Task CloseThePlayerAsync()
    {
        using var limit = new CancellationTokenSource(LastSendLimit, _time);
        try
        {
            await _playback.StopAsync(closePlayer: true, limit.Token);
        }
        catch (OperationCanceledException)
        {
            // The exit path closes whatever is left.
        }
    }

    private async Task SendWhatsWaitingAsync()
    {
        using var limit = new CancellationTokenSource(LastSendLimit, _time);
        try
        {
            await _outbox.FlushAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            // It stays on disk for the next sign-in as this account.
        }
    }

    private async Task TellTheServerAsync(CancellationToken ct)
    {
        try
        {
            await _client.SendAsync(HttpMethod.Post, "sign-out", null, ct);
        }
        catch (Exception e) when (e is ApiException or ServerUnreachableException or IdentityChangedException)
        {
            Trace.TraceWarning($"Signing out on the server failed: {e.GetType().Name}");
        }
    }

    // On whichever thread noticed.
    private void OnConnectionChanged()
    {
        if (_monitor.State == ConnectionState.SignedOut) ForgetToken();
    }

    // A file that won't take this costs one more sign-out at the next start, which comes back here.
    private void ForgetToken()
    {
        _token.Forget();
        try
        {
            _settings.Update(s => s with { Connection = s.Connection is { } saved ? saved with { ProtectedToken = null } : null });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"The ended session stayed saved: {e.GetType().Name}");
        }
    }
}
