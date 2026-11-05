using System.Diagnostics;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Remote;

namespace MediaBoard.Desktop.Remote;

/// <summary>
/// Runs a viewer's background work, the counterpart of <see cref="AppHost"/>. <see cref="Start"/> returns at once: the
/// monitor connects on the thread pool, and the first connection loads the pages. Everything that can end a stream ends
/// the player here, because a player left without its stream goes on to report the file as finished.
/// </summary>
public sealed class RemoteHost(
    ActivityHub hub,
    ConnectionMonitor monitor,
    RemoteLibrary library,
    RemotePlaybackSource source,
    PlaybackService playback,
    StreamProxy proxy,
    ProgressOutbox outbox)
{
    private const string StreamStoppedEvent = "stream-stopped";

    private readonly CancellationTokenSource _stopping = new();
    private Task _watching = Task.CompletedTask;

    /// <summary>Call once, from the UI thread, after the window was created.</summary>
    public void Start()
    {
        // TMDB answers through the owner's key, so the status bar has nothing to ask a viewer for.
        hub.SetKeyState(KeyState.Ok);

        source.StreamLost += OnStreamLost;
        monitor.EventReceived += OnEvent;
        monitor.Changed += OnConnectionChanged;
        playback.ProgressChanged += OnProgressChanged;

        outbox.Start();
        _watching = Task.Run(() => monitor.RunAsync(_stopping.Token));
    }

    /// <summary>
    /// Saves the position and closes the player while its stream still works, since a player that sees a drop saves the
    /// file's whole length. Then the proxy, the monitor, and last the outbox with that position. A failed stream is still
    /// heard until the proxy has gone. The caller bounds the wait.
    /// </summary>
    public async Task StopAsync()
    {
        playback.ProgressChanged -= OnProgressChanged;
        await playback.DisposeAsync().ConfigureAwait(false);
        await proxy.DisposeAsync().ConfigureAwait(false);

        source.StreamLost -= OnStreamLost;
        monitor.EventReceived -= OnEvent;
        monitor.Changed -= OnConnectionChanged;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _watching.ConfigureAwait(false); // never throws: the monitor catches everything

        await outbox.DisposeAsync().ConfigureAwait(false);
    }

    // On the raiser's thread, not the UI's: the session must stop listening before the stream's failure can reach the
    // player, or its claim to have finished the file gets in first.
    private void OnStreamLost(int fileId, string url, string reason) => _ = playback.AbandonAsync(fileId, url, reason);

    // Paused players don't read, so without this one hears nothing until they press play.
    private void OnEvent(string name)
    {
        if (name == StreamStoppedEvent) _ = playback.AbandonCurrentAsync(source.ReasonFor(ApiErrorCode.StreamStopped));
    }

    // Only the monitor's own state counts here. A slow request doesn't change it, so it can't cost a viewer the player.
    private void OnConnectionChanged()
    {
        var state = monitor.State;
        hub.SetOnline(state == ConnectionState.Connected);
        if (state == ConnectionState.Unreachable) _ = playback.AbandonCurrentAsync(source.ReasonFor(ApiErrorCode.Unreachable));
        else if (state == ConnectionState.Connected) _ = Task.Run(SendProgressAsync);
    }

    private void OnProgressChanged() => _ = Task.Run(SendProgressAsync);

    /// <summary>The pages show what the server says, so they reload only once it has what this PC was holding.</summary>
    private async Task SendProgressAsync()
    {
        try
        {
            await outbox.FlushAsync(_stopping.Token).ConfigureAwait(false);
            if (monitor.State == ConnectionState.Connected) library.NotifyChanged();
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            Debug.Assert(_stopping.IsCancellationRequested, "Only stopping ends a send this way.");
        }
    }
}
