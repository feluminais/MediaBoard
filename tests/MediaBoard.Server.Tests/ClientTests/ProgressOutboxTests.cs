using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

namespace MediaBoard.Server.Tests.ClientTests;

/// <summary>The outbox against a <see cref="FakeApi"/>, on a clock the test moves. No real player is involved.</summary>
public sealed class ProgressOutboxTests
{
    private const int FileId = 7;
    private const int OtherFile = 8;

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-outbox-" + Guid.NewGuid().ToString("N"));
        private readonly ServerFixture _fixture = new();
        private readonly List<ProgressOutbox> _outboxes = [];
        private FakeApi _api = null!;

        public World() => Directory.CreateDirectory(_dir);

        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

        /// <summary>What the server was sent, in order, each with the route it came on.</summary>
        public ConcurrentQueue<(string Route, ProgressEntry? Entry, WatchedRequest? Watched)> Received { get; } = new();

        /// <summary>Replaces the normal answer of 204; returns true when it answered.</summary>
        public Func<HttpContext, Task<bool>>? Override { get; set; }

        public ApiClient Client { get; private set; } = null!;

        public ProgressOutbox Outbox { get; private set; } = null!;

        public string PathOnDisk => Path.Combine(_dir, "data", "pending-progress.json");

        public static async Task<World> StartAsync()
        {
            var world = new World();
            await world.StartApiAsync();
            world.Client = new ApiClient(
                new ServerEndpoint("Library", [world._api.Address], CertificateStore.Fingerprint(world._fixture.NewCertificate())), () => "token", _ => { });
            world.Outbox = world.NewOutbox();
            return world;
        }

        public ProgressOutbox NewOutbox()
        {
            var outbox = new ProgressOutbox(Client, PathOnDisk, Clock);
            _outboxes.Add(outbox);
            return outbox;
        }

        public Task StopApiAsync() => _api.DisposeAsync().AsTask();

        public async Task StartApiAsync()
        {
            _api = await FakeApi.StartAsync(_fixture.NewCertificate(), AnswerAsync, _api?.Port);
        }

        public async Task WaitForAsync(int count)
        {
            using var timeout = new CancellationTokenSource(Within);
            while (Received.Count < count) await Task.Delay(10, timeout.Token);
        }

        /// <summary>Long enough for a send that shouldn't happen to have happened.</summary>
        public static Task SettleAsync() => Task.Delay(200, Ct);

        public async ValueTask DisposeAsync()
        {
            await _api.DisposeAsync();
            foreach (var outbox in _outboxes) await outbox.DisposeAsync();
            Client.Dispose();
            await _fixture.DisposeAsync();
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        private async Task AnswerAsync(HttpContext context)
        {
            if (Override is { } first && await first(context)) return;

            var route = context.Request.Path.Value!;
            if (route.EndsWith("/progress", StringComparison.Ordinal))
            {
                Received.Enqueue((route, await context.Request.ReadFromJsonAsync<ProgressEntry>(ApiJson.Options), null));
            }
            else
            {
                Received.Enqueue((route, null, await context.Request.ReadFromJsonAsync<WatchedRequest>(ApiJson.Options)));
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
    }

    private static string Progress(int fileId) => $"/api/v1/files/{fileId}/progress";

    private sealed class TraceCapture : TraceListener
    {
        public TraceCapture() => Trace.Listeners.Add(this);

        public List<string> Lines { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            lock (Lines) Lines.Add(message ?? "");
        }

        protected override void Dispose(bool disposing)
        {
            Trace.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task Save_ReturnsWithoutNetwork()
    {
        await using var world = await World.StartAsync();
        world.Override = async context =>
        {
            await FakeApi.HangAsync(context);
            return true;
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        world.Outbox.Save(FileId, 10, 100);
        var flush = world.Outbox.FlushAsync(stop.Token);

        // The flush is stuck on a server that doesn't answer, and neither call can wait for it.
        world.Outbox.Save(FileId, 20, 100);
        world.Outbox.SetWatched(OtherFile, true);

        Assert.False(flush.IsCompleted);
        Assert.Equal(20, world.Outbox.Waiting(FileId)?.PositionS);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush.WaitAsync(Within, Ct));
    }

    [Fact]
    public async Task KeepsNewestPerFile()
    {
        await using var world = await World.StartAsync();
        world.Outbox.Save(FileId, 10, 100);
        world.Outbox.Save(FileId, 20, 100);
        world.Outbox.Save(OtherFile, 5, 50);

        await world.Outbox.FlushAsync(Ct);

        Assert.Equal(2, world.Received.Count);
        Assert.Equal(20, Assert.Single(world.Received, sent => sent.Route == Progress(FileId)).Entry?.PositionS);
        Assert.Equal(5, Assert.Single(world.Received, sent => sent.Route == Progress(OtherFile)).Entry?.PositionS);
        Assert.Null(world.Outbox.Waiting(FileId));
    }

    [Fact]
    public async Task Entry_CarriesItsTime()
    {
        await using var world = await World.StartAsync();
        var made = world.Clock.GetUtcNow();
        world.Outbox.Save(FileId, 10, 100);
        world.Clock.Advance(TimeSpan.FromMinutes(1));

        await world.Outbox.FlushAsync(Ct);

        var entry = Assert.Single(world.Received).Entry!;
        Assert.Equal(made, entry.At);
        Assert.Equal(100, entry.DurationS);
    }

    [Fact]
    public async Task SurvivesRestart()
    {
        await using var world = await World.StartAsync();
        world.Outbox.Save(FileId, 10, 100);
        world.Outbox.SetWatched(FileId, true);

        var afterRestart = world.NewOutbox();
        await afterRestart.FlushAsync(Ct);

        var entry = Assert.Single(world.Received).Entry!;
        Assert.Equal(10, entry.PositionS);
        Assert.True(entry.Watched);
    }

    [Fact]
    public async Task WritesThroughATempFile_LeavesOnlyTheFile()
    {
        await using var world = await World.StartAsync();

        world.Outbox.Save(FileId, 10, 100);

        var file = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(world.PathOnDisk)!));
        Assert.Equal(world.PathOnDisk, file);
    }

    [Fact]
    public async Task WatchedMark_DuringOutage_ReachesServerLater()
    {
        await using var world = await World.StartAsync();
        await world.StopApiAsync();
        world.Outbox.Save(FileId, 95, 100);
        world.Outbox.SetWatched(FileId, true);

        await world.Outbox.FlushAsync(Ct);

        Assert.True(world.Outbox.Waiting(FileId)?.Watched);
        await world.StartApiAsync();

        await world.Outbox.FlushAsync(Ct);

        var entry = Assert.Single(world.Received).Entry!;
        Assert.Equal(95, entry.PositionS);
        Assert.True(entry.Watched);
        Assert.Null(world.Outbox.Waiting(FileId));
    }

    [Fact]
    public async Task WatchedAlone_NeverInventsAPosition()
    {
        await using var world = await World.StartAsync();

        world.Outbox.SetWatched(FileId, true);
        await world.Outbox.FlushAsync(Ct);

        var (route, entry, watched) = Assert.Single(world.Received);
        Assert.Equal($"/api/v1/files/{FileId}/watched", route);
        Assert.Null(entry);
        Assert.True(watched?.Watched);
    }

    [Fact]
    public async Task Flushes_Every10s_AndOnPauseEndClose()
    {
        await using var world = await World.StartAsync();
        world.Outbox.Start();
        world.Outbox.Save(FileId, 10, 100);

        world.Clock.Advance(Every - TimeSpan.FromMilliseconds(1));
        await World.SettleAsync();
        Assert.Empty(world.Received);

        world.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await world.WaitForAsync(1);

        // None of these moves the clock.
        world.Outbox.Save(FileId, 20, 100);
        world.Outbox.Paused(FileId);
        await world.WaitForAsync(2);

        world.Outbox.Save(FileId, 30, 100);
        world.Outbox.Finished(FileId);
        await world.WaitForAsync(3);

        world.Outbox.Save(FileId, 40, 100);
        await world.Outbox.DisposeAsync();

        Assert.Equal([10d, 20d, 30d, 40d], world.Received.Select(sent => sent.Entry!.PositionS));
    }

    [Fact]
    public async Task Paused_ResendsLastPositionEvery10s_WhileStreamOpen()
    {
        await using var world = await World.StartAsync();
        world.Outbox.Start();
        world.Outbox.Save(FileId, 10, 100);
        world.Outbox.Paused(FileId);
        await world.WaitForAsync(1);

        world.Clock.Advance(Every);
        await world.WaitForAsync(2);
        world.Clock.Advance(Every);
        await world.WaitForAsync(3);

        // The same entry each time: a new time would beat what another device saved in the meantime.
        Assert.Single(world.Received.Select(sent => sent.Entry).Distinct());

        world.Outbox.Finished(FileId);
        world.Clock.Advance(Every);
        await World.SettleAsync();

        Assert.Equal(3, world.Received.Count);
    }

    [Fact]
    public async Task NotPaused_NothingNew_SendsNothing()
    {
        await using var world = await World.StartAsync();
        world.Outbox.Start();
        world.Outbox.Save(FileId, 10, 100);
        world.Clock.Advance(Every);
        await world.WaitForAsync(1);

        world.Clock.Advance(Every);
        await World.SettleAsync();

        Assert.Single(world.Received);
    }

    [Fact]
    public async Task ForbiddenFile_DropsEntry()
    {
        await using var world = await World.StartAsync();
        world.Override = async context =>
        {
            await Results.Json(new ApiErrorBody(ApiErrorCode.NotFound), ApiJson.Options, statusCode: StatusCodes.Status404NotFound).ExecuteAsync(context);
            return true;
        };
        world.Outbox.Save(FileId, 10, 100);

        await world.Outbox.FlushAsync(Ct);

        Assert.Null(world.Outbox.Waiting(FileId));
        Assert.Null(world.NewOutbox().Waiting(FileId));
    }

    [Fact]
    public async Task ServerRefusingToAnswer_KeepsEntries()
    {
        await using var world = await World.StartAsync();
        world.Override = context =>
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return Task.FromResult(true);
        };
        world.Outbox.Save(FileId, 10, 100);

        await world.Outbox.FlushAsync(Ct);

        Assert.Equal(10, world.Outbox.Waiting(FileId)?.PositionS);
    }

    [Fact]
    public async Task NewerSave_DuringASend_IsNotForgotten()
    {
        await using var world = await World.StartAsync();
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Override = async _ =>
        {
            sending.TrySetResult();
            await release.Task;
            return false;
        };
        world.Outbox.Save(FileId, 10, 100);

        var flush = world.Outbox.FlushAsync(Ct);
        await sending.Task.WaitAsync(Within, Ct);
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        world.Outbox.Save(FileId, 20, 100);
        release.SetResult();
        await flush.WaitAsync(Within, Ct);

        Assert.Equal(20, world.Outbox.Waiting(FileId)?.PositionS);
    }

    [Fact]
    public async Task Flushes_DoNotOverlap()
    {
        await using var world = await World.StartAsync();
        var running = 0;
        var most = 0;
        world.Override = async _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            await Task.Delay(100, Ct);
            Interlocked.Decrement(ref running);
            return false;
        };
        world.Outbox.Save(FileId, 10, 100);

        var flushes = Enumerable.Range(0, 4).Select(_ => world.Outbox.FlushAsync(Ct)).ToList();
        await Task.WhenAll(flushes).WaitAsync(Within, Ct);

        Assert.Equal(1, most);
        Assert.Single(world.Received);

        static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while (value > (seen = Volatile.Read(ref target))) Interlocked.CompareExchange(ref target, value, seen);
        }
    }

    [Fact]
    public async Task CorruptFile_StartsEmpty_LogsOnce()
    {
        await using var world = await World.StartAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(world.PathOnDisk)!);
        await File.WriteAllTextAsync(world.PathOnDisk, "{ this is not", Ct);
        using var trace = new TraceCapture();

        var outbox = world.NewOutbox();
        await outbox.FlushAsync(Ct);
        await outbox.FlushAsync(Ct);
        outbox.Save(FileId, 10, 100);

        Assert.Single(trace.Lines, line => line.Contains("progress", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(trace.Lines, line => line.Contains(world.PathOnDisk, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(world.Received);
        Assert.Equal(10, world.NewOutbox().Waiting(FileId)?.PositionS);
    }
}
