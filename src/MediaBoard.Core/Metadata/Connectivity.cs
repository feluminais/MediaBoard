using System.Net.NetworkInformation;
using MediaBoard.Core.Activity;

namespace MediaBoard.Core.Metadata;

/// <summary>
/// Whether TMDB can be reached. It starts out online and goes offline when <see cref="ReportOffline"/> is
/// called or the network drops. While offline it probes every minute, and whenever Windows reports a
/// network again, until the probe gets an answer.
/// </summary>
public sealed class Connectivity : IDisposable
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    public static readonly Uri ProbeUri = new("https://api.themoviedb.org/3/configuration");

    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly TimeProvider _time;
    private readonly ActivityHub _hub;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private bool _online = true;
    private bool _retrying;
    private bool _subscribed;
    private bool _disposed;

    public Connectivity(Func<CancellationToken, Task<bool>> probe, TimeProvider time, ActivityHub hub)
    {
        _probe = probe;
        _time = time;
        _hub = hub;
    }

    /// <summary>Raised from a background thread when the state flips; not raised for a repeat of the same state.</summary>
    public event Action<bool>? Changed;

    public bool IsOnline
    {
        get { lock (_gate) return _online; }
    }

    /// <summary>
    /// The usual probe: a HEAD request to TMDB, with no key. Any answer, even a 401, means the host is
    /// reachable; only a failed or timed-out request means offline.
    /// </summary>
    public static Func<CancellationToken, Task<bool>> TmdbProbe(HttpClient http) => async ct =>
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, ProbeUri);
            using var _ = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    };

    /// <summary>Starts watching for network changes and checks the connection once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _subscribed) return;
            _subscribed = true;
        }

        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _ = Task.Run(() => CheckAsync(_lifetime.Token));
    }

    /// <summary>Called by whoever got no answer from TMDB. Starts the retry loop when not already offline.</summary>
    public void ReportOffline() => SetState(false);

    /// <summary>Probes now and applies the answer.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        if (await ProbeAsync(ct).ConfigureAwait(false)) SetState(true);
        else SetState(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) _ = Task.Run(() => CheckAsync(_lifetime.Token));
        else SetState(false);
    }

    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        try
        {
            return await _probe(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SetState(bool online)
    {
        bool changed;
        bool startLoop;
        lock (_gate)
        {
            if (_disposed) return;
            changed = _online != online;
            _online = online;
            startLoop = !online && !_retrying;
            if (startLoop) _retrying = true;
        }

        _hub.SetOnline(online);
        if (startLoop) _ = Task.Run(() => RetryAsync(_lifetime.Token));
        if (changed) Changed?.Invoke(online);
    }

    private async Task RetryAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(RetryInterval, _time, ct).ConfigureAwait(false);
                if (!await ProbeAsync(ct).ConfigureAwait(false)) continue;

                lock (_gate) _retrying = false;
                SetState(true);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }
}
