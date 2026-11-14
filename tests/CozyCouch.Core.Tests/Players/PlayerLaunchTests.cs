using System.ComponentModel;
using System.Diagnostics;
using CozyCouch.Core.Players;

namespace CozyCouch.Core.Tests.Players;

/// <summary>Launching, classifying the exit of and closing a player, tested once here instead of once per backend.</summary>
public class PlayerLaunchTests
{
    private const string Exe = @"C:\Program Files\VideoLAN\VLC\vlc.exe";

    private readonly FakePlayerProcess _process = new();

    [Fact]
    public void TryStart_ReturnsTheProcess()
    {
        var started = PlayerLaunch.TryStart(_ => _process, new ProcessStartInfo(), "VLC", Exe, out var failure);

        Assert.Same(_process, started);
        Assert.Equal("", failure);
    }

    [Theory]
    [InlineData(2)] // file not found
    [InlineData(3)] // path not found
    public void TryStart_MissingProgram_IsAFailure_NotAnException(int nativeError)
    {
        var started = PlayerLaunch.TryStart(_ => throw new Win32Exception(nativeError), new ProcessStartInfo(), "VLC", Exe, out var failure);

        Assert.Null(started);
        Assert.Equal($"VLC not found: {Exe}", failure);
    }

    [Fact]
    public void TryStart_OtherWin32Error_IsAFailure_WithTheSystemsReason()
    {
        var started = PlayerLaunch.TryStart(_ => throw new Win32Exception(5, "Access is denied"), new ProcessStartInfo(), "VLC", Exe, out var failure);

        Assert.Null(started);
        Assert.Equal("VLC could not start: Access is denied", failure);
    }

    [Fact]
    public void TryStart_InvalidOperation_IsAFailure_WithItsMessage()
    {
        var started = PlayerLaunch.TryStart(_ => throw new InvalidOperationException("boom"), new ProcessStartInfo(), "VLC", Exe, out var failure);

        Assert.Null(started);
        Assert.Equal("VLC could not start: boom", failure);
    }

    [Fact]
    public void ExitCodeOrZero_ReadsTheCode_AndTreatsAMissingOrUnreadableOneAsZero()
    {
        _process.Exit(3);

        Assert.Equal(3, PlayerLaunch.ExitCodeOrZero(_process));
        Assert.Equal(0, PlayerLaunch.ExitCodeOrZero(null));
        Assert.Equal(0, PlayerLaunch.ExitCodeOrZero(new UnreadableExitCodeProcess()));
    }

    [Theory]
    [InlineData(false, 1.0, 0, "closed right after starting")] // never answered, quick, clean
    [InlineData(false, 2.9, 1, "closed right after starting")] // never answered, quick, crashed
    [InlineData(false, 3.0, 0, null)] // the early-exit window is over: a normal close
    [InlineData(false, 3.0, 1, "stopped unexpectedly")]
    [InlineData(true, 1.0, 0, null)] // it answered, so a quick clean exit is a normal close
    [InlineData(true, 600.0, 1, "stopped unexpectedly")]
    public void ExitEvent_ClassifiesTheExit(bool answered, double sinceStartS, int exitCode, string? failure)
    {
        var exit = PlayerLaunch.ExitEvent("VLC", exitCode, answered, TimeSpan.FromSeconds(sinceStartS));

        PlaybackEvent expected = failure is null ? new PlaybackEvent.Closed() : new PlaybackEvent.Failed($"VLC {failure}");
        Assert.Equal(expected, exit);
    }

    [Fact]
    public async Task CloseAsync_AsksFirst_ThenDisposes()
    {
        await PlayerLaunch.CloseAsync(_process, TimeSpan.FromSeconds(5));

        Assert.True(_process.CloseRequested);
        Assert.False(_process.Killed);
        Assert.True(_process.Disposed);
    }

    [Fact]
    public async Task CloseAsync_KillsAPlayerThatIgnoresClose()
    {
        _process.ExitOnClose = false;

        await PlayerLaunch.CloseAsync(_process, TimeSpan.FromMilliseconds(50));

        Assert.True(_process.CloseRequested);
        Assert.True(_process.Killed);
        Assert.True(_process.Disposed);
    }

    [Fact]
    public async Task CloseAsync_LeavesAnExitedPlayerAlone()
    {
        _process.Exit();

        await PlayerLaunch.CloseAsync(_process, TimeSpan.FromSeconds(5));

        Assert.False(_process.CloseRequested);
        Assert.False(_process.Killed);
        Assert.True(_process.Disposed);
    }

    [Fact]
    public async Task ReleaseAsync_Detached_LeavesThePlayerRunning()
    {
        await PlayerLaunch.ReleaseAsync(_process, TimeSpan.FromSeconds(5), detached: true);

        Assert.False(_process.CloseRequested);
        Assert.False(_process.Killed);
        Assert.True(_process.Disposed);
    }

    [Fact]
    public async Task ReleaseAsync_NotDetached_ClosesThePlayer()
    {
        await PlayerLaunch.ReleaseAsync(_process, TimeSpan.FromSeconds(5), detached: false);

        Assert.True(_process.CloseRequested);
        Assert.True(_process.Disposed);
    }

    /// <summary>A process whose exit code cannot be read, as <see cref="Process.ExitCode"/> before the process has exited.</summary>
    private sealed class UnreadableExitCodeProcess : IPlayerProcess
    {
        public Task Exited => Task.CompletedTask;
        public bool HasExited => true;
        public int ExitCode => throw new InvalidOperationException("No exit code yet");

        public void CloseMainWindow()
        {
        }

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }
}
