using Avalonia.Headless.XUnit;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Players;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using MediaBoard.Desktop.Remote;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;
using MediaBoard.Server;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop.Tests;

public sealed class RemoteCompositionTests
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Player : IPlayerSession
    {
        public bool TracksProgress => true;

        public event Action<PlaybackEvent>? Event;

        public string Locator { get; private set; } = "";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action? OnClose { get; set; }

        public void Raise(PlaybackEvent e) => Event?.Invoke(e);

        public Task StartAsync(string filePath, TimeSpan? startAt, CancellationToken ct)
        {
            Locator = filePath;
            Started.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<TimeSpan?> GetPositionAsync(CancellationToken ct) => Task.FromResult<TimeSpan?>(null);

        public void Detach() => throw new InvalidOperationException("A viewer's player is never left running without the app.");

        public ValueTask DisposeAsync()
        {
            OnClose?.Invoke();
            Closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A viewer's PC with its own data folder, joined to a real server on loopback.</summary>
    private sealed class Viewer : IAsyncDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("mediaboard-viewer-").FullName;
        private bool _stopped;

        private Viewer(SharingWorld world, StoredConnection connection, string token, Player player)
        {
            World = world;
            Connection = connection;
            Token = token;
            this.Player = player;
            new SettingsStore(Paths.Settings).Save(new AppSettings { Mode = AppMode.Server, DefaultPlayerId = "mpv", Connection = connection });
            Services = RemoteComposition.Build(Paths, connection, services =>
            {
                services.AddSingleton(sp => new PlaybackService(
                    sp.GetRequiredService<RemotePlaybackSource>(),
                    sp.GetRequiredService<ProgressOutbox>(),
                    new PlayerDetector(_ => true, () => [], sp.GetRequiredService<ActivityHub>()),
                    sp.GetRequiredService<SettingsStore>(),
                    sp.GetRequiredService<ActivityHub>(),
                    _ => player));

                // A clock that stands still: nothing sends on the outbox's timer, so what's sent, the host sent.
                services.AddSingleton(sp => new ProgressOutbox(
                    sp.GetRequiredService<ApiClient>(),
                    Paths.Outbox(connection.Endpoint.Fingerprint, connection.AccountName),
                    new ManualClock()));
            });
        }

        public SharingWorld World { get; }

        public StoredConnection Connection { get; }

        /// <summary>The device token in the clear, for a test that asks the server itself.</summary>
        public string Token { get; }

        public Player Player { get; }

        public DataPaths Paths => new(Path.Combine(_dir, "library.db"), Path.Combine(_dir, "settings.json"), Path.Combine(_dir, "images"));

        public ServiceProvider Services { get; }

        public RemoteHost Host => Services.GetRequiredService<RemoteHost>();

        public ConnectionMonitor Monitor => Services.GetRequiredService<ConnectionMonitor>();

        public PlaybackService Playback => Services.GetRequiredService<PlaybackService>();

        public ActivityHub Hub => Services.GetRequiredService<ActivityHub>();

        public static async Task<Viewer> JoinAsync(SharingWorld world, bool signIn = true)
        {
            world.OpenDatabase();
            world.SetSharing(s => s with { HomeAddress = "127.0.0.1" });
            await world.Sharing.SetOnAsync(true);
            var account = world.Accounts.AddPerson("Sam", null);
            Assert.True(InviteString.TryDecode(world.Sharing.CreateInvite(account.Id), out var invite));
            var token = world.Services.GetRequiredService<DeviceStore>().Issue(account.Id, "Laptop", "198.51.100.1").Token;
            var stored = signIn ? RemoteSession.Protect(token) : "this was never protected";
            var endpoint = new ServerEndpoint(invite.ServerName, invite.Addresses, invite.Fingerprint);
            return new Viewer(world, new StoredConnection(endpoint, "Sam", "Laptop", stored), token, new Player());
        }

        public async Task ConnectedAsync()
        {
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Monitor.Changed += OnChanged;
            OnChanged();
            await connected.Task.WaitAsync(Within, Ct);
            Monitor.Changed -= OnChanged;

            void OnChanged()
            {
                if (Monitor.State == ConnectionState.Connected) connected.TrySetResult();
            }
        }

        public async Task StopAsync()
        {
            if (_stopped) return;

            _stopped = true;
            await Host.StopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await Services.DisposeAsync();
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Within);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [AvaloniaFact]
    public async Task RemoteComposition_Resolves_MainWindow()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);

        var window = new MainWindow { DataContext = viewer.Services.GetRequiredService<MainWindowViewModel>() };
        window.Show();
        window.Close();

        Assert.NotNull(viewer.Services.GetRequiredService<ISearchService>());
        Assert.IsType<RemoteLibrary>(viewer.Services.GetRequiredService<ILibrary>());
    }

    [AvaloniaFact]
    public async Task RemoteComposition_NoDatabaseFile_Created()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);

        viewer.Services.GetRequiredService<MainWindowViewModel>();
        viewer.Host.Start();
        await viewer.ConnectedAsync();

        Assert.False(File.Exists(viewer.Paths.Database));
    }

    [Fact]
    public async Task RemoteComposition_HasNoScanner()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);

        Assert.Null(viewer.Services.GetService<LibraryScanner>());
        Assert.Null(viewer.Services.GetService<LibraryDatabase>());
        Assert.Null(viewer.Services.GetService<LibraryWatcher>());
        Assert.Null(viewer.Services.GetService<PlayerWatcher>());
        Assert.Null(viewer.Services.GetService<SharingServer>());
        Assert.Null(viewer.Services.GetService<SharingViewModel>());
        Assert.Null(viewer.Services.GetService<AppHost>());

        // Leaving a player running would stop the proxy under it.
        Assert.Null(viewer.Services.GetService<PlayerExitPrompt>());
    }

    [AvaloniaFact]
    public async Task RemoteComposition_HasNoSharingPage()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);

        var shell = viewer.Services.GetRequiredService<MainWindowViewModel>();

        Assert.DoesNotContain(shell.NavItems, item => item.Key == MainWindowViewModel.SharingPage);
    }

    [AvaloniaFact]
    public async Task Local_Composition_Unchanged()
    {
        await using var world = new SharingWorld();

        var shell = world.Services.GetRequiredService<MainWindowViewModel>();

        Assert.Contains(shell.NavItems, item => item.Key == MainWindowViewModel.FoldersPage);
        Assert.Contains(shell.NavItems, item => item.Key == MainWindowViewModel.SharingPage);
        Assert.NotNull(world.Services.GetRequiredService<AppHost>());
        Assert.NotNull(world.Services.GetRequiredService<LibraryScanner>());
        Assert.NotNull(world.Services.GetRequiredService<PlayerExitPrompt>());
        Assert.IsType<LocalLibrary>(world.Services.GetRequiredService<ILibrary>());
        Assert.Null(world.Services.GetService<RemoteHost>());
        Assert.Null(world.Services.GetService<ConnectionMonitor>());
        Assert.Equal(AppMode.Unset, world.Settings.Load().Mode);
    }

    [Fact]
    public async Task RemoteHost_TriggersFirstLoad()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var changes = 0;
        viewer.Services.GetRequiredService<ILibrary>().Changed += () => Interlocked.Increment(ref changes);

        viewer.Host.Start();

        await WaitUntilAsync(() => Volatile.Read(ref changes) > 0);
    }

    [Fact]
    public async Task RemoteHost_AViewerHasNoKeyToAskFor()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);

        viewer.Host.Start();

        Assert.Equal(KeyState.Ok, viewer.Hub.Current.KeyState);
    }

    [Fact]
    public async Task RemoteHost_StatusFollowsTheConnection()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);

        viewer.Host.Start();
        await viewer.ConnectedAsync();
        await WaitUntilAsync(() => viewer.Hub.Current.Online);

        viewer.Monitor.Report(ConnectionEvent.Unreachable);

        await WaitUntilAsync(() => !viewer.Hub.Current.Online);
    }

    [Fact]
    public async Task UndecryptableToken_EndsSignedOut()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world, signIn: false);

        viewer.Host.Start();

        await WaitUntilAsync(() => viewer.Monitor.State == ConnectionState.SignedOut);
    }

    [Fact]
    public async Task IdentityChanged_StaysUntilRetry_ThenConnects()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        viewer.Host.Start();
        await viewer.ConnectedAsync();

        viewer.Monitor.Report(ConnectionEvent.IdentityChanged);

        Assert.Equal(ConnectionState.IdentityChanged, viewer.Monitor.State);

        viewer.Monitor.Retry();

        await viewer.ConnectedAsync();
    }

    [Fact]
    public async Task Posters_ComeThroughRemoteImages()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var kept = Directory.CreateDirectory(Path.Combine(viewer.Paths.Images, "remote")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(kept, "w342abc.jpg"), [1], Ct);

        var loaded = await viewer.Services.GetRequiredService<PosterLoader>().LoadAsync("/abc.jpg", Ct);

        Assert.Equal(Path.Combine(kept, "w342abc.jpg"), loaded);
    }

    [Fact]
    public void Outbox_IsPerServerAndAccount()
    {
        var paths = new DataPaths(@"C:\data\library.db", @"C:\data\settings.json", @"C:\data\images");
        var endpoint = new ServerEndpoint("Library", ["host:1"], new string('A', 64));
        var mine = paths.Outbox(endpoint.Fingerprint, "Sam");

        Assert.Equal(mine, paths.Outbox(endpoint.Fingerprint.ToLowerInvariant(), "Sam"));
        Assert.NotEqual(mine, paths.Outbox(endpoint.Fingerprint, "Anna"));
        Assert.NotEqual(mine, paths.Outbox(new string('B', 64), "Sam"));
        Assert.Equal(@"C:\data", Path.GetDirectoryName(mine));
        Assert.Matches("^pending-progress-[0-9a-f]+\\.json$", Path.GetFileName(mine));
    }

    [Fact]
    public async Task Outbox_WritesItsFileUnderItsOwnName()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);

        viewer.Services.GetRequiredService<ProgressOutbox>().Save(1, 10, 100);

        Assert.True(File.Exists(viewer.Paths.Outbox(viewer.Connection.Endpoint.Fingerprint, "Sam")));
    }

    [Fact]
    public async Task Reconnecting_FlushesTheOutbox()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var file = await world.AddLongMovieAsync();
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        var outbox = viewer.Services.GetRequiredService<ProgressOutbox>();
        await world.Sharing.SetOnAsync(false);
        await WaitUntilAsync(() => viewer.Monitor.State == ConnectionState.Unreachable);
        outbox.Save(file, 60, 100);

        await world.Sharing.SetOnAsync(true);

        await WaitUntilAsync(() => outbox.Waiting(file) is null);
    }

    [Fact]
    public async Task Playing_ThenTheOwnerStops_ThePausedViewerHearsAtOnce()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var file = await world.AddLongMovieAsync();
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        await viewer.Playback.PlayAsync(file, fromStart: true, Ct);

        // Opened past the proxy, whose copy of a cut stream would speak up: as with a paused player, only the event can.
        await using var body = await world.OpenBodyAsync($"/api/v1/files/{file}/stream", viewer.Token);
        await WaitUntilAsync(() => world.Streams.Current.Count == 1);

        Assert.True(world.Streams.Stop(world.Streams.Current[0].Id));

        await viewer.Player.Closed.Task.WaitAsync(Within, Ct);
        var owner = world.Services.GetRequiredService<AccountStore>().Get(LibraryDatabase.OwnerId)!.Name;
        await WaitUntilAsync(() => viewer.Hub.Recent.Any(entry => entry.Message == $"{owner} stopped this stream"));
        Assert.Null(viewer.Playback.CurrentPlayer);
    }

    // The monitor saying so is the real thing; only then may a viewer lose the player.
    [Fact]
    public async Task Playing_ThenTheServerGoesSilent_AbandonsAtOnce()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var file = await world.AddLongMovieAsync();
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        await viewer.Playback.PlayAsync(file, fromStart: true, Ct);
        viewer.Player.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1000)));
        viewer.Player.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(115)));
        viewer.Player.Raise(new PlaybackEvent.Paused());

        viewer.Monitor.Report(ConnectionEvent.Unreachable);

        // Synchronous, as the guarantee needs: by the time the report returns, the player's claim counts for nothing.
        viewer.Player.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(1000)));
        viewer.Player.Raise(new PlaybackEvent.Ended());
        await viewer.Player.Closed.Task.WaitAsync(Within, Ct);
        var outbox = viewer.Services.GetRequiredService<ProgressOutbox>();
        Assert.Equal(115, outbox.Waiting(file)?.PositionS);
        Assert.NotEqual(true, outbox.Waiting(file)?.Watched);
        Assert.Contains(viewer.Hub.Recent, entry => entry.Message == "Can't reach the server");
    }

    [Fact]
    public async Task Playing_ConnectionStaysUp_NothingIsAbandoned()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var file = await world.AddLongMovieAsync();
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        await viewer.Playback.PlayAsync(file, fromStart: true, Ct);

        viewer.Monitor.Report(ConnectionEvent.Listening);

        Assert.False(viewer.Player.Closed.Task.IsCompleted);
        Assert.NotNull(viewer.Playback.CurrentPlayer);
    }

    // The file goes while the connection stays up, so only the failed stream can say so. The proxy answers the player
    // after raising, and by then the session must have ended: this is the order the false-watched guarantee rests on.
    [Fact]
    public async Task AStreamThatFails_EndsTheSessionBeforeThePlayerHearsOfIt()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var file = await world.AddLongMovieAsync();
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        await viewer.Playback.PlayAsync(file, fromStart: true, Ct);
        File.Delete(Path.Combine(Path.GetDirectoryName(world.Paths.Database)!, "library", "Movie.mkv"));

        using var player = new HttpClient();
        using var answer = await player.GetAsync(viewer.Player.Locator, HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadGateway, answer.StatusCode);
        Assert.Null(viewer.Playback.CurrentPlayer);
        Assert.Contains(viewer.Hub.Recent, entry => entry.Message.StartsWith("File missing", StringComparison.Ordinal));
        Assert.Equal(ConnectionState.Connected, viewer.Monitor.State);
    }

    [Fact]
    public async Task Stopping_ClosesThePlayerWhileTheProxyStillWorks()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var file = await world.AddLongMovieAsync();
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        await viewer.Playback.PlayAsync(file, fromStart: true, Ct);
        var proxy = viewer.Services.GetRequiredService<StreamProxy>();
        var proxyUp = false;
        viewer.Player.OnClose = () =>
        {
            try
            {
                proxy.StartAsync().GetAwaiter().GetResult();
                proxyUp = true;
            }
            catch (StreamUnavailableException)
            {
                proxyUp = false;
            }
        };

        await viewer.StopAsync();

        Assert.True(proxyUp);
        Assert.True(viewer.Player.Closed.Task.IsCompleted);
    }

    [Fact]
    public async Task Stopping_SavesThePositionForTheServer()
    {
        await using var world = new SharingWorld();
        await using var viewer = await Viewer.JoinAsync(world);
        var file = await world.AddLongMovieAsync();
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        await viewer.Playback.PlayAsync(file, fromStart: true, Ct);
        viewer.Player.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(1000)));
        viewer.Player.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(115)));
        var progress = world.Services.GetRequiredService<ProgressStore>();

        await viewer.StopAsync();
        await viewer.Services.GetRequiredService<ProgressOutbox>().DisposeAsync();

        var account = world.Services.GetRequiredService<AccountStore>().All().Single(a => a.Name == "Sam");
        Assert.Equal(115, progress.Get(account.Id, file)?.PositionS);
    }

    [Fact]
    public async Task Replaced_TheLibraryStopsListening()
    {
        await using var world = new SharingWorld();
        var viewer = await Viewer.JoinAsync(world);
        var library = viewer.Services.GetRequiredService<RemoteLibrary>();
        var monitor = viewer.Monitor;
        var changes = 0;
        library.Changed += () => changes++;
        viewer.Host.Start();
        await viewer.ConnectedAsync();
        await viewer.DisposeAsync();
        changes = 0;

        monitor.Report(ConnectionEvent.Listening);
        monitor.Report(ConnectionEvent.Unreachable);
        monitor.Report(ConnectionEvent.Listening);

        Assert.Equal(0, changes);
    }
}
