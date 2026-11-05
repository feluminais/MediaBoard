using System.Diagnostics;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Settings;
using MediaBoard.Server;

namespace MediaBoard.Desktop.Sharing;

public enum SharingStatus { Off, Starting, On, Failed }

/// <summary>What <see cref="SharingController.CheckConnectionAsync"/> found.</summary>
public enum CheckResult { Reachable, NotConclusive, NoAddress }

/// <summary>
/// Turns the sharing server on and off. Nothing listens, and the server isn't even built, until the first
/// <see cref="SetOnAsync"/>. A start that fails ends in <see cref="SharingStatus.Failed"/> with a <see cref="Problem"/>
/// in plain words, never an exception. One change runs at a time, on the thread pool, so the UI thread can call any of this.
/// </summary>
/// <param name="settings">Holds the port, the addresses and whether sharing comes back after a restart.</param>
/// <param name="certificates">The server's certificate, which devices pin.</param>
/// <param name="invites">Where <see cref="CreateInvite"/> gets its codes.</param>
/// <param name="streams">Who is watching.</param>
/// <param name="hub">Where the status bar reads from, and where failures are logged.</param>
/// <param name="searches">What the server keeps per viewer, dropped when it stops.</param>
/// <param name="server">Builds the server. Called on the first start.</param>
/// <param name="isPublicNetwork">Whether Windows treats this PC's connection as Public, or null to ask Windows.</param>
/// <param name="loopbackOnly">For tests: see <see cref="SharingOptions.LoopbackOnly"/>.</param>
public sealed class SharingController(
    SettingsStore settings,
    CertificateStore certificates,
    InviteStore invites,
    StreamRegistry streams,
    ActivityHub hub,
    ViewerSearches searches,
    Func<SharingServer> server,
    Func<bool>? isPublicNetwork = null,
    bool loopbackOnly = false)
{
    private const string PublicNetworkWarning = "This PC's network is set to Public, so Windows will block connections. Change it to Private in Windows settings.";
    private const string CertificateProblem = "Can't open the sharing certificate. It may belong to another Windows account. If you replace it, everyone needs a new invite.";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<bool> _isPublicNetwork = isPublicNetwork ?? NetworkProfile.IsPublic;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile State _state = new(SharingStatus.Off);
    private volatile string? _fingerprint;
    private SharingServer? _server;
    private X509Certificate2? _certificate;
    private bool _listening;

    /// <summary>Raised on any thread when the status changes or the number watching does.</summary>
    public event Action? Changed;

    public SharingStatus Status => _state.Status;

    /// <summary>Why sharing is <see cref="SharingStatus.Failed"/> or a certificate replace failed, and what to do about it.</summary>
    public string? Problem => _state.Problem;

    /// <summary>Sharing is on, but something will get in the way of connections.</summary>
    public string? Warning => _state.Warning;

    /// <summary>
    /// A failed start with the setting still on, as after a failed resume: the next launch tries again unless the owner
    /// turns sharing off.
    /// </summary>
    public bool ComesBackAtLaunch => _state.ComesBackAtLaunch;

    /// <summary>The port in use while on. The saved one can differ: a change waits for the next start.</summary>
    public int? Port => _state.Port;

    /// <summary>Open streams, the same count as the rows of Watching now.</summary>
    public int Watching => streams.Current.Count;

    /// <summary>"Sharing: 2 watching" while sharing is on, or null. The status bar and the tray both show it.</summary>
    public string? Summary => Status != SharingStatus.On ? null : Watching switch
    {
        0 => "Sharing on",
        var watching => string.Create(CultureInfo.InvariantCulture, $"Sharing: {watching} watching"),
    };

    /// <summary>Starts or stops the server and remembers the choice. A start that fails isn't remembered.</summary>
    public Task SetOnAsync(bool on) => InTurnAsync(async () =>
    {
        if (on)
        {
            if (Status != SharingStatus.On) await StartAsync().ConfigureAwait(false);
            return;
        }

        await StopAsync().ConfigureAwait(false);
        Remember(on: false);
        Set(new State(SharingStatus.Off));
    });

    /// <summary>Brings sharing back at startup if it was on. A failure leaves the setting, so the next start tries again.</summary>
    public Task ResumeAsync() => settings.Load().Sharing.On ? SetOnAsync(true) : Task.CompletedTask;

    /// <summary>Stops the server as the app closes. Sharing stays on in the settings, so it resumes next time.</summary>
    public Task StopForExitAsync() => InTurnAsync(async () =>
    {
        await StopAsync().ConfigureAwait(false);
        Set(new State(SharingStatus.Off));
    });

    /// <summary>An invite with the addresses and fingerprint in force now. Slow on first use, so call it off the UI thread.</summary>
    /// <exception cref="InvalidOperationException">No address is set, so nobody could use it.</exception>
    /// <exception cref="CryptographicException">The certificate can't be read.</exception>
    public string CreateInvite(int userId)
    {
        var sharing = settings.Load().Sharing;
        string[] addresses = [.. new[] { sharing.HomeAddress, sharing.PublicAddress }.Select(a => ConnectAddress(a, sharing.Port)).OfType<string>()];
        if (addresses.Length == 0) throw new InvalidOperationException("Set an address before creating an invite.");

        var fingerprint = CurrentFingerprint();
        return InviteString.Encode(new InvitePayload(NameOf(sharing), addresses, fingerprint, invites.Create(userId)));
    }

    /// <summary>
    /// Asks the public address for <c>ping</c>, trusting only this server's certificate. Failure proves nothing, since
    /// many routers can't loop back to themselves. With sharing off nothing goes out, because nothing could answer.
    /// With no port in the address it tries the port in use, since a saved change waits for the next start.
    /// </summary>
    public async Task<CheckResult> CheckConnectionAsync(CancellationToken ct)
    {
        var sharing = settings.Load().Sharing;
        if (ConnectAddress(sharing.PublicAddress, Port ?? sharing.Port) is not { } address) return CheckResult.NoAddress;
        if (_fingerprint is not { } fingerprint) return CheckResult.NotConclusive;

        using var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is X509Certificate2 served && CertificateStore.Fingerprint(served) == fingerprint,
            },
        };
        using var client = new HttpClient(handler) { Timeout = CheckTimeout };
        try
        {
            using var response = await client.GetAsync(new Uri($"https://{address}/api/v1/ping"), ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? CheckResult.Reachable : CheckResult.NotConclusive;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return CheckResult.NotConclusive;
        }
    }

    /// <summary>
    /// Makes a new certificate, so every device needs a new invite. The server comes back on the new one if it was on,
    /// or if it was failing, since the old certificate may be why. The new one is made first: if that fails the status
    /// stays as it was, streams play on, and <see cref="Problem"/> says what happened.
    /// </summary>
    /// <returns>Whether the certificate changed, which is so even if the server then failed to come back.</returns>
    public async Task<bool> ReplaceCertificateAsync()
    {
        var replaced = false;
        await InTurnAsync(async () => replaced = await ReplaceInTurnAsync().ConfigureAwait(false)).ConfigureAwait(false);
        return replaced;
    }

    /// <summary>Whether <see cref="CreateInvite"/> could use what the owner typed as an address.</summary>
    public static bool IsUsableAddress(string typed) => ConnectAddress(typed, 1) is not null;

    private static string NameOf(SharingSettings sharing) => sharing.ServerName ?? $"{Environment.UserName}'s library";

    // Called in turn.
    private async Task<bool> ReplaceInTurnAsync()
    {
        try
        {
            certificates.Replace().Dispose();
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            const string problem = "Couldn't replace the sharing certificate. The old one is still in place.";
            Trace.TraceWarning($"Replacing the certificate failed: {e.GetType().Name}");
            hub.Log(ActivityLevel.Error, problem);
            Set(_state with { Problem = problem });
            return false;
        }

        var restart = Status != SharingStatus.Off;
        await StopAsync().ConfigureAwait(false);
        if (restart) await StartAsync().ConfigureAwait(false);
        else Set(new State(SharingStatus.Off));
        return true;
    }

    /// <summary>The host and port a device connects to, from what the owner typed, or null if nothing usable was typed.</summary>
    private static string? ConnectAddress(string? typed, int port)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;

        var text = typed.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;

        // Uri can't tell a typed :443 from no port at all, and a router can forward 443 just as well as any other.
        var hostAndPort = text.Split('/')[2];
        var typedPort = hostAndPort.EndsWith(string.Create(CultureInfo.InvariantCulture, $":{uri.Port}"), StringComparison.Ordinal);
        return string.Create(CultureInfo.InvariantCulture, $"{uri.Host}:{(typedPort ? uri.Port : port)}");
    }

    private static string StartProblem(Exception failure, int port) => failure switch
    {
        PortInUseException => string.Create(CultureInfo.InvariantCulture, $"Another program is using port {port}. Close it or choose another port."),
        IOException or SocketException or UnauthorizedAccessException or ArgumentOutOfRangeException =>
            string.Create(CultureInfo.InvariantCulture, $"Sharing couldn't start on port {port}. Check the port and try again."),
        _ => "Sharing couldn't start. Restart MediaBoard to try again.",
    };

    // The turn is taken here on the caller's thread, so changes run in the order they were asked for.
    private async Task InTurnAsync(Func<Task> change)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(change).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Two loads of one PFX can share a key, and disposing either deletes it, so the live certificate is the one to read.
    private string CurrentFingerprint()
    {
        _gate.Wait();
        try
        {
            if (_certificate is { } live) return CertificateStore.Fingerprint(live);

            using var certificate = certificates.LoadOrCreate();
            return CertificateStore.Fingerprint(certificate);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Called in turn.
    private async Task StartAsync()
    {
        var sharing = settings.Load().Sharing;
        Set(new State(SharingStatus.Starting));
        var warning = OnPublicNetwork() ? PublicNetworkWarning : null;

        X509Certificate2 certificate;
        try
        {
            certificate = certificates.LoadOrCreate();
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Opening the certificate failed: {e.GetType().Name}");
            Fail(CertificateProblem);
            return;
        }

        try
        {
            _server ??= server();
            var options = new SharingOptions(sharing.Port, certificate) { LoopbackOnly = loopbackOnly };
            await _server.StartAsync(options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            certificate.Dispose();
            // The type alone: a message can hold a path.
            Trace.TraceWarning($"Sharing didn't start: {e.GetType().Name}");
            Fail(StartProblem(e, sharing.Port));
            return;
        }

        _certificate = certificate;
        _fingerprint = CertificateStore.Fingerprint(certificate);
        _listening = true;
        streams.Changed += OnStreamsChanged;
        Remember(on: true);
        Set(new State(SharingStatus.On, Warning: warning, Port: sharing.Port));
    }

    // Called in turn. The server closes its own streams and events as it stops.
    private async Task StopAsync()
    {
        _fingerprint = null;
        streams.Changed -= OnStreamsChanged;
        try
        {
            if (_listening) await _server!.StopAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // It clears its host before stopping it, so it's stopped either way.
            Trace.TraceWarning($"Sharing didn't stop cleanly: {e.GetType().Name}");
            hub.Log(ActivityLevel.Warning, "Sharing didn't stop cleanly.");
        }

        _listening = false;
        _certificate?.Dispose();
        _certificate = null;
        searches.Clear();
    }

    // Only a warning hangs on this, so nothing it does may get in the way of starting.
    private bool OnPublicNetwork()
    {
        try
        {
            return _isPublicNetwork();
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"Reading the network profile failed: {e.GetType().Name}");
            return false;
        }
    }

    private void Fail(string problem)
    {
        hub.Log(ActivityLevel.Error, problem);
        Set(new State(SharingStatus.Failed, problem, ComesBackAtLaunch: settings.Load().Sharing.On));
    }

    private void Remember(bool on)
    {
        try
        {
            if (settings.Load().Sharing.On != on) settings.Update(s => s with { Sharing = s.Sharing with { On = on } });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            hub.Log(ActivityLevel.Warning, "Couldn't save the sharing setting.", "MediaBoard may not remember it after a restart.");
        }
    }

    private void Set(State state)
    {
        _state = state;
        Publish();
    }

    // Raised for every position a player reports. Only a change of count concerns anyone here.
    private void OnStreamsChanged()
    {
        if (Summary != hub.Current.Sharing) Publish();
    }

    private void Publish()
    {
        hub.SetSharing(Summary);
        Changed?.Invoke();
    }

    /// <param name="Problem">See <see cref="SharingController.Problem"/>.</param>
    /// <param name="Warning">See <see cref="SharingController.Warning"/>.</param>
    /// <param name="Port">See <see cref="SharingController.Port"/>.</param>
    /// <param name="ComesBackAtLaunch">See <see cref="SharingController.ComesBackAtLaunch"/>.</param>
    private sealed record State(SharingStatus Status, string? Problem = null, string? Warning = null, int? Port = null, bool ComesBackAtLaunch = false);
}
