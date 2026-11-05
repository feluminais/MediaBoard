using System.Diagnostics;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;

namespace MediaBoard.Desktop.Remote;

/// <summary>
/// This PC's place on the server: who it's signed in as, and what ends that. The saved token goes as soon as anything
/// says it's dead, and the server's addresses stay, so the next start lands on Connect with that server filled in. A
/// token that can't be opened here ends the same way, since the server answers it with a sign-out.
/// </summary>
public sealed class ViewerAccount
{
    private readonly StoredConnection _connection;
    private readonly SettingsStore _settings;
    private readonly ApiClient _client;
    private readonly ConnectionMonitor _monitor;
    private readonly AppRestart _restart;

    /// <param name="connection">The connection this run started on.</param>
    /// <param name="settings">Where it's saved.</param>
    /// <param name="client">The client it runs through.</param>
    /// <param name="monitor">Says when the server has ended it.</param>
    /// <param name="restart">Starts the app over, into the Connect screen.</param>
    public ViewerAccount(StoredConnection connection, SettingsStore settings, ApiClient client, ConnectionMonitor monitor, AppRestart restart)
    {
        _connection = connection;
        _settings = settings;
        _client = client;
        _monitor = monitor;
        _restart = restart;
        monitor.Changed += OnConnectionChanged;
    }

    public string ServerName => _connection.Endpoint.ServerName;

    public string AccountName => _connection.AccountName;

    /// <exception cref="ApiException">The server refused.</exception>
    /// <exception cref="ServerUnreachableException">No answer.</exception>
    /// <exception cref="IdentityChangedException">The server's certificate changed.</exception>
    public Task ChangePasswordAsync(string current, string next, CancellationToken ct) =>
        _client.SendAsync(HttpMethod.Put, "me/password", new ChangePasswordRequest { Current = current, Next = next }, ct);

    /// <summary>Ends this device's session. A server that can't be told keeps its side of it, which the owner can end.</summary>
    /// <returns>False when the app couldn't restart, and this run carries on signed out.</returns>
    public async Task<bool> SignOutAsync(CancellationToken ct)
    {
        if (_monitor.State == ConnectionState.Connected)
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

        ForgetToken();
        return _restart.Run();
    }

    /// <summary>For a viewer the server has already signed out.</summary>
    /// <returns>False when the app couldn't restart.</returns>
    public bool ReturnToConnect() => _restart.Run();

    // On whichever thread noticed.
    private void OnConnectionChanged()
    {
        if (_monitor.State == ConnectionState.SignedOut) ForgetToken();
    }

    // A file that won't take this costs one more sign-out at the next start, which comes back here.
    private void ForgetToken()
    {
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
