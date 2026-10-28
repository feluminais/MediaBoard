using System.Collections.Concurrent;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Players;
using MediaBoard.Core.Players.MpcHc;

namespace MediaBoard.Core.Tests.Players;

/// <summary>
/// Drives the real MPC-HC. Skipped unless MPC-HC is installed and <c>MEDIABOARD_TEST_VIDEO</c> points to a video
/// of at least 20 seconds (for example one made with
/// <c>ffmpeg -f lavfi -i testsrc=duration=30:size=320x240:rate=25 -c:v libx264 test.mp4</c>).
/// The player window shows up on screen while the tests run and is closed at the end.
/// </summary>
public class MpcHcIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (PlayerInfo Player, string Video) Requirements()
    {
        var video = Environment.GetEnvironmentVariable("MEDIABOARD_TEST_VIDEO");
        Assert.SkipUnless(!string.IsNullOrEmpty(video) && File.Exists(video), "MEDIABOARD_TEST_VIDEO is not set to a video file");
        var player = PlayerDetector.CreateDefault(new ActivityHub()).Detect().FirstOrDefault(p => p.Id == "mpc-hc");
        Assert.SkipUnless(player is not null, "MPC-HC is not installed");
        return (player, video!);
    }

    [Fact]
    public async Task PlaysSeeksAndCloses()
    {
        var (player, video) = Requirements();
        var events = new Events();
        await using var session = MpcHcSession.Create(player, pollInterval: TimeSpan.FromMilliseconds(500));
        session.Event += events.Add;

        await session.StartAsync(video, null, Ct);
        await events.WaitFor<PlaybackEvent.Loaded>(Timeout);

        session.Seek(TimeSpan.FromSeconds(10));
        await events.WaitFor<PlaybackEvent.Position>(Timeout, p => p.At >= TimeSpan.FromSeconds(10));
        var position = await session.GetPositionAsync(Ct);
        Assert.NotNull(position);
        Assert.True(position >= TimeSpan.FromSeconds(10), $"position {position}");

        session.Close();
        await events.WaitFor<PlaybackEvent.Closed>(Timeout);
        Assert.Empty(events.OfType<PlaybackEvent.Failed>());
    }

    [Fact]
    public async Task PausesClosesAndResumes()
    {
        var (player, video) = Requirements();

        TimeSpan saved;
        var first = new Events();
        await using (var session = MpcHcSession.Create(player, pollInterval: TimeSpan.FromMilliseconds(500)))
        {
            session.Event += first.Add;
            await session.StartAsync(video, null, Ct);
            await first.WaitFor<PlaybackEvent.Loaded>(Timeout);
            await first.WaitFor<PlaybackEvent.Playing>(Timeout);

            session.Seek(TimeSpan.FromSeconds(12));
            await first.WaitFor<PlaybackEvent.Position>(Timeout, p => p.At >= TimeSpan.FromSeconds(12));
            session.Pause();
            await first.WaitFor<PlaybackEvent.Paused>(Timeout);
            saved = await session.GetPositionAsync(Ct) ?? throw new InvalidOperationException("no position while paused");

            session.Close();
            await first.WaitFor<PlaybackEvent.Closed>(Timeout);
        }

        var second = new Events();
        await using (var session = MpcHcSession.Create(player, pollInterval: TimeSpan.FromMilliseconds(500)))
        {
            session.Event += second.Add;
            await session.StartAsync(video, saved, Ct);
            await second.WaitFor<PlaybackEvent.Loaded>(Timeout);
            await Task.Delay(1000, Ct);

            var resumed = await session.GetPositionAsync(Ct);
            Assert.NotNull(resumed);
            Assert.InRange(resumed.Value, saved - TimeSpan.FromSeconds(1), saved + TimeSpan.FromSeconds(4));
            TestContext.Current.TestOutputHelper?.WriteLine($"saved {saved}, resumed at {resumed}");
        }

        Assert.Empty(first.OfType<PlaybackEvent.Failed>());
        Assert.Empty(second.OfType<PlaybackEvent.Failed>());
    }

    private sealed class Events
    {
        private readonly ConcurrentQueue<PlaybackEvent> _events = new();

        private readonly ITestOutputHelper? _output = TestContext.Current.TestOutputHelper;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        public void Add(PlaybackEvent e)
        {
            _events.Enqueue(e);
            _output?.WriteLine($"{_clock.Elapsed.TotalSeconds,6:0.000}s  {e}");
        }

        public List<T> OfType<T>() => _events.OfType<T>().ToList();

        public async Task<T> WaitFor<T>(TimeSpan timeout, Func<T, bool>? match = null) where T : PlaybackEvent
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var found = _events.OfType<T>().FirstOrDefault(e => match?.Invoke(e) ?? true);
                if (found is not null) return found;
                if (DateTime.UtcNow > deadline) Assert.Fail($"No matching {typeof(T).Name} in time; got: {string.Join(", ", _events)}");
                await Task.Delay(50, Ct);
            }
        }
    }
}
