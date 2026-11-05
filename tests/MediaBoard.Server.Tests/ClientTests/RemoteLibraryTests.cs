using System.Text.Json;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace MediaBoard.Server.Tests.ClientTests;

/// <summary>The viewer's library, search and images against the real server on a real TLS socket, over a seeded library.</summary>
public sealed class RemoteLibraryTests
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> AdminCalls => new()
    {
        "Rename",
        "SetHidden",
        "FixMatch",
        "MarkNotMedia",
        "PreviewDelete",
        "DeleteFromDisk",
    };

    private sealed class World : IAsyncDisposable
    {
        private readonly string _imagesDir = Path.Combine(Path.GetTempPath(), "mediaboard-remote-images-" + Guid.NewGuid().ToString("N"));
        private readonly List<IDisposable> _disposables = [];

        private World(ServerFixture fixture, int port)
        {
            Fixture = fixture;
            Port = port;
        }

        public ServerFixture Fixture { get; }

        public int Port { get; }

        public FakeTimeProvider Clock { get; private init; } = null!;

        public Account Sam { get; private set; } = null!;

        public IssuedDevice Phone { get; private set; } = null!;

        public int Heat { get; private set; }

        public int HeatFile { get; private set; }

        public int Up { get; private set; }

        public ConnectionMonitor Monitor { get; private set; } = null!;

        public ApiClient Client { get; private set; } = null!;

        public RemoteLibrary Library { get; private set; } = null!;

        public string ImagesDir => _imagesDir;

        public Viewer SamViewer => new(Sam.Id, null);

        public static async Task<World> StartAsync()
        {
            var clock = new FakeTimeProvider();
            var fixture = new ServerFixture(time: clock);
            var port = ServerFixture.FreePort();
            await fixture.Server.StartAsync(new SharingOptions(port, ServerFixture.ServerName, fixture.NewCertificate()) { LoopbackOnly = true }, Ct);
            var world = new World(fixture, port) { Clock = clock };
            var folder = fixture.AddFolder("films");
            world.Heat = fixture.Movie("Heat", 101);
            world.Up = fixture.Movie("Up", 102);
            world.HeatFile = await fixture.AddFileAsync(folder, @"Sub\Heat (1995).mkv", world.Heat);
            await fixture.AddFileAsync(folder, "Up (2009).mkv", world.Up);
            world.Sam = fixture.NewPerson("Sam");
            world.Phone = fixture.Join(world.Sam);

            var endpoint = new ServerEndpoint("Library", [$"127.0.0.1:{port}"], CertificateStore.Fingerprint(fixture.NewCertificate()));
            ConnectionMonitor monitor = null!;
            var client = new ApiClient(endpoint, () => world.Phone.Token, happened => monitor.Report(happened));
            monitor = new ConnectionMonitor(client);
            world.Client = client;
            world.Monitor = monitor;
            world.Library = new RemoteLibrary(client, monitor);
            world._disposables.Add(client);
            world._disposables.Add(world.Library);
            return world;
        }

        public RemoteImages NewImages() => new(Client, _imagesDir);

        /// <summary>A client for a port nothing listens on.</summary>
        public ApiClient DeadClient()
        {
            var endpoint = new ServerEndpoint("Gone", [$"127.0.0.1:{ServerFixture.FreePort()}"], CertificateStore.Fingerprint(Fixture.NewCertificate()));
            var client = new ApiClient(endpoint, () => Phone.Token, _ => { });
            _disposables.Add(client);
            return client;
        }

        public async Task<IAsyncDisposable> RunMonitorAsync()
        {
            var running = new Running(Monitor);
            await ReachAsync(ConnectionState.Connected);
            return running;
        }

        public async Task ReachAsync(ConnectionState state)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Check()
            {
                if (Monitor.State == state) reached.TrySetResult();
            }

            Monitor.Changed += Check;
            try
            {
                Check();
                await reached.Task.WaitAsync(Within, Ct);
            }
            finally
            {
                Monitor.Changed -= Check;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var disposable in _disposables) disposable.Dispose();
            await Fixture.DisposeAsync();
            if (Directory.Exists(_imagesDir)) Directory.Delete(_imagesDir, recursive: true);
        }
    }

    private sealed class Running : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        private readonly Task _run;

        public Running(ConnectionMonitor monitor) => _run = monitor.RunAsync(_stop.Token);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _run.WaitAsync(Within, Ct);
            _stop.Dispose();
        }
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ApiJson.Options);

    [Fact]
    public async Task Home_Lists_MatchServer()
    {
        await using var world = await World.StartAsync();
        world.Fixture.Progress.Save(world.Sam.Id, world.HeatFile, 60, 6000);
        var server = world.Fixture.Owner.ForViewer(world.SamViewer);

        var playing = world.Library.ContinueWatching(10);
        var recent = world.Library.RecentlyAdded(10);

        Assert.Equal(Json(server.ContinueWatching(10)), Json(playing));
        Assert.Equal(Json(server.RecentlyAdded(10)), Json(recent));
        Assert.Equal(Json(server.Titles(TitleKind.Movie, TitleSort.Name, includeHidden: true)), Json(world.Library.Titles(TitleKind.Movie, TitleSort.Name, includeHidden: true)));
        Assert.Single(playing);
        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public async Task Home_Lists_PassTheLimit()
    {
        await using var world = await World.StartAsync();

        Assert.Single(world.Library.RecentlyAdded(1));
        Assert.Equal(2, world.Library.RecentlyAdded(int.MaxValue).Count);
    }

    [Fact]
    public async Task Title_RoundTrips_AllFields()
    {
        await using var world = await World.StartAsync();
        var server = world.Fixture.Owner.ForViewer(world.SamViewer).Title(world.Heat)!;

        var title = world.Library.Title(world.Heat)!;

        // The server leaves the folders above a file out of RelPath, so it is the one thing that differs.
        var expected = server with { Files = [.. server.Files.Select(f => f with { RelPath = Path.GetFileName(f.RelPath) })] };
        Assert.Equal(Json(expected), Json(title));
        Assert.Equal("Heat", title.Name);
        Assert.Equal("Heat (1995).mkv", Assert.Single(title.Files).RelPath);
        Assert.Equal(world.HeatFile, world.Library.NextEpisodeFileId(world.Heat));
    }

    [Fact]
    public async Task Title_Unknown_IsNull_LikeTheLocalLibrary()
    {
        await using var world = await World.StartAsync();

        Assert.Null(world.Fixture.Owner.ForViewer(world.SamViewer).Title(9999));
        Assert.Null(world.Library.Title(9999));
        Assert.Null(world.Library.NextEpisodeFileId(9999));
    }

    [Fact]
    public async Task SetWatched_ReachesServer()
    {
        await using var world = await World.StartAsync();
        var changed = 0;
        world.Library.Changed += () => changed++;

        world.Library.SetWatched(world.HeatFile, true);

        Assert.True(world.Fixture.Progress.Get(world.Sam.Id, world.HeatFile)!.Watched);
        Assert.Equal(1, changed);
        world.Library.SetWatched(world.HeatFile, false);
        Assert.False(world.Fixture.Progress.Get(world.Sam.Id, world.HeatFile)!.Watched);
    }

    [Fact]
    public async Task Dismiss_ReachesServer()
    {
        await using var world = await World.StartAsync();
        world.Fixture.Progress.Save(world.Sam.Id, world.HeatFile, 60, 6000);
        var server = world.Fixture.Owner.ForViewer(world.SamViewer);
        Assert.Single(server.ContinueWatching(10));
        var changed = 0;
        world.Library.Changed += () => changed++;

        world.Library.DismissContinue(world.Heat);
        world.Library.DismissContinue(9999);

        Assert.Empty(server.ContinueWatching(10));
        Assert.Equal(1, changed);
        Assert.Equal(60, world.Fixture.Progress.Get(world.Sam.Id, world.HeatFile)!.PositionS);
    }

    [Theory]
    [MemberData(nameof(AdminCalls))]
    public async Task Admin_Methods_NotSupported(string call)
    {
        await using var world = await World.StartAsync();
        var library = world.Library;

        Action act = call switch
        {
            "Rename" => () => library.Rename(TargetKind.Title, 1, "x"),
            "SetHidden" => () => library.SetHidden(TargetKind.Title, 1, hidden: true),
            "FixMatch" => () => library.FixMatchAsync(1, 1, TitleKind.Movie, Ct).GetAwaiter().GetResult(),
            "MarkNotMedia" => () => library.MarkNotMedia(1),
            "PreviewDelete" => () => library.PreviewDelete(1),
            "DeleteFromDisk" => () => library.DeleteFromDiskAsync(new DeletePreview(1, "x", [])).GetAwaiter().GetResult(),
            _ => throw new ArgumentOutOfRangeException(nameof(call), call, null),
        };

        Assert.Throws<NotSupportedException>(act);
    }

    [Fact]
    public async Task NeedsReview_And_Folders_AreFixed()
    {
        await using var world = await World.StartAsync();

        Assert.Empty(world.Library.NeedsReview());
        Assert.Equal(0, world.Library.NeedsReviewCount());
        var folder = Assert.Single(world.Library.Folders());
        Assert.Equal(string.Empty, folder.Path);
        Assert.False(folder.Unavailable);
    }

    [Fact]
    public async Task Changed_FiresOnEvent()
    {
        await using var world = await World.StartAsync();
        await using var running = await world.RunMonitorAsync();
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Library.Changed += () => heard.TrySetResult();

        // The server sends one 5 s after the first change.
        world.Fixture.Owner.NotifyChanged();
        world.Clock.Advance(TimeSpan.FromSeconds(5));

        await heard.Task.WaitAsync(Within, Ct);
    }

    [Fact]
    public async Task Changed_FiresOnReconnect()
    {
        await using var world = await World.StartAsync();
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Library.Changed += () => heard.TrySetResult();

        await using var running = await world.RunMonitorAsync();

        await heard.Task.WaitAsync(Within, Ct);
    }

    [Fact]
    public async Task Search_ReturnsBothGroups()
    {
        await using var world = await World.StartAsync();
        world.Fixture.Provider.Hits.Add(new SearchCandidate(101, TitleKind.Movie, "Heat", null, 1995, 50, 8.2, "/heat.jpg", "A thief.", 10));
        var search = new RemoteSearchService(world.Client);

        var results = await search.SearchAsync(" heat ", Ct);

        Assert.Equal(OnlineState.Ok, results.State);
        Assert.Equal(["Heat"], results.Library.Select(c => c.Name));
        var hit = Assert.Single(results.Online);
        Assert.Equal(101, hit.Candidate.TmdbId);
        Assert.Equal(world.Heat, hit.LibraryTitleId);
    }

    [Fact]
    public async Task Search_TooShort_AsksNoOnlineGroup_AndServerGone_KeepsTheLibrary()
    {
        await using var world = await World.StartAsync();
        var search = new RemoteSearchService(world.Client);

        var one = await search.SearchAsync("h", Ct);
        Assert.Equal(OnlineState.TooShort, one.State);
        Assert.Equal(["Heat"], one.Library.Select(c => c.Name));

        Assert.Empty((await search.SearchAsync("", Ct)).Library);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.SearchAsync("heat", new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Search_OnlineDetails_AreNullWhenTheServerHasNone()
    {
        await using var world = await World.StartAsync();
        var details = new TitleDetails(101, TitleKind.Movie, "Heat", 1995, null, "A thief.", 8.2, "/heat.jpg", 170, []);
        world.Fixture.Provider.Titles[(101, TitleKind.Movie)] = details;
        world.Fixture.Provider.Seasons[(7, 1)] = new SeasonDetails(1, [new EpisodeDetails(1, 1, "Pilot", 45, null)]);
        var search = new RemoteSearchService(world.Client);

        Assert.Equal(Json(details), Json(await search.GetOnlineDetailsAsync(101, TitleKind.Movie, Ct)));
        Assert.Equal("Pilot", (await search.GetOnlineSeasonAsync(7, 1, Ct))!.Episodes.Single().Name);

        // A miss makes the server think TMDB is offline, so these come last.
        Assert.Null(await search.GetOnlineDetailsAsync(555, TitleKind.Movie, Ct));
        Assert.Null(await search.GetOnlineSeasonAsync(7, 2, Ct));
    }

    [Fact]
    public async Task Commands_LeaveOutAdminOnes()
    {
        await using var world = await World.StartAsync();
        var search = new RemoteSearchService(world.Client);

        Assert.Equal(["settings"], search.Commands("").Select(c => c.Id));
        Assert.Equal(["settings"], search.Commands("set").Select(c => c.Id));
        Assert.Empty(search.Commands("rescan"));
        Assert.Empty(search.Commands("hidden"));
    }

    [Fact]
    public async Task Images_CachedLocally_SecondCallSendsNothing()
    {
        await using var world = await World.StartAsync();
        Directory.CreateDirectory(world.Fixture.ImagesDir);
        await File.WriteAllBytesAsync(Path.Combine(world.Fixture.ImagesDir, "w342poster.jpg"), [1, 2, 3], Ct);
        var images = world.NewImages();

        var first = await images.GetAsync("/poster.jpg", 342, Ct);

        Assert.NotNull(first);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(first, Ct));
        Assert.StartsWith(Path.Combine(world.ImagesDir, "remote") + Path.DirectorySeparatorChar, first, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(first)!, "*.tmp"));

        // With the server gone, only the disk can answer.
        await world.Fixture.Server.StopAsync();
        Assert.Equal(first, await images.GetAsync("/poster.jpg", 342, Ct));
    }

    [Fact]
    public async Task Images_ServerHasNone_IsNull_AndServerGone_IsNull()
    {
        await using var world = await World.StartAsync();
        var images = world.NewImages();

        Assert.Null(await images.GetAsync("/missing.jpg", 342, Ct));
        await world.Fixture.Server.StopAsync();
        Assert.Null(await images.GetAsync("/other.jpg", 342, Ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => images.GetAsync("/third.jpg", 342, new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(Path.Combine(world.ImagesDir, "remote")) && Directory.GetFiles(Path.Combine(world.ImagesDir, "remote")).Length > 0);
    }

    [Theory]
    [InlineData("poster.jpg")]
    [InlineData("/")]
    [InlineData("/../poster.jpg")]
    [InlineData("/..\\poster.jpg")]
    [InlineData("/a/b.jpg")]
    [InlineData("//poster.jpg")]
    [InlineData("/a\\b.jpg")]
    [InlineData("/C:poster.jpg")]
    [InlineData("/poster.jpg\n")]
    [InlineData("/poster.gif")]
    [InlineData("/po..ster.jpg")]
    public async Task Images_UnsafePath_IsRefusedBeforeAnyFileIsMade(string posterPath)
    {
        await using var world = await World.StartAsync();
        var images = world.NewImages();

        Assert.Null(await images.GetAsync(posterPath, 342, Ct));
        Assert.False(Directory.Exists(world.ImagesDir));
    }

    [Fact]
    public async Task Images_WidthOutsideTheCache_Throws()
    {
        await using var world = await World.StartAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => world.NewImages().GetAsync("/poster.jpg", 500, Ct));
    }

    [Fact]
    public async Task Failures_Throw_ForBackgroundQuery()
    {
        await using var world = await World.StartAsync();
        var gone = new RemoteLibrary(world.DeadClient(), world.Monitor);

        Assert.Throws<ServerUnreachableException>(() => gone.ContinueWatching(10));
        Assert.Throws<ServerUnreachableException>(() => gone.RecentlyAdded(10));
        Assert.Throws<ServerUnreachableException>(() => gone.Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false));
        Assert.Throws<ServerUnreachableException>(() => gone.Title(1));
        Assert.Throws<ServerUnreachableException>(() => gone.DismissContinue(1));
        Assert.Throws<ServerUnreachableException>(() => gone.SetWatched(1, true));
        Assert.Throws<ApiException>(() => world.Library.SetWatched(9999, true));
        await Assert.ThrowsAsync<ServerUnreachableException>(() => new RemoteSearchService(world.DeadClient()).SearchAsync("heat", Ct));
    }
}
