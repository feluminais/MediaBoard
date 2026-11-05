using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using MediaBoard.Desktop.Sharing;
using MediaBoard.Server;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop.Tests.Support;

/// <summary>
/// The real composition on a temp data folder and a free port, listening on loopback only so no firewall prompt comes
/// up. The Run key, the sleep state and the network are all stand-ins, so a test leaves nothing on the PC.
/// </summary>
internal sealed class SharingWorld : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mediaboard-sharing-").FullName;
    private readonly List<IDisposable> _disposables = [];

    public SharingWorld()
    {
        Services = Composition.Build(Paths, services =>
        {
            services.AddSingleton(_ => new HttpClient(new NoNetwork()));
            services.AddSingleton(sp => new Connectivity(
                _ => Task.FromResult(false), TimeProvider.System, sp.GetRequiredService<ActivityHub>()));
            services.AddSingleton<IRunKey>(RunKey);
            services.AddSingleton(_ => new StreamRegistry(TimeProvider.System, new NoSleepBlock()));
            services.AddSingleton(sp => new SharingController(
                sp.GetRequiredService<SettingsStore>(),
                sp.GetRequiredService<CertificateStore>(),
                sp.GetRequiredService<InviteStore>(),
                sp.GetRequiredService<StreamRegistry>(),
                sp.GetRequiredService<ActivityHub>(),
                sp.GetRequiredService<ViewerSearches>(),
                () =>
                {
                    Interlocked.Increment(ref _serversBuilt);
                    return ServerFailure is { } failure ? throw failure : sp.GetRequiredService<SharingServer>();
                },
                () => NetworkFailure is { } failure ? throw failure : OnPublicNetwork,
                loopbackOnly: true));
        });
        Settings.Update(s => s with { Sharing = s.Sharing with { Port = Port } });
    }

    private int _serversBuilt;

    public int ServersBuilt => Volatile.Read(ref _serversBuilt);

    public int Port { get; } = FreePort();

    public bool OnPublicNetwork { get; set; }

    /// <summary>What the network check throws, when set.</summary>
    public Exception? NetworkFailure { get; set; }

    /// <summary>What building the server throws, when set: a failure that has nothing to do with binding.</summary>
    public Exception? ServerFailure { get; set; }

    public FakeRunKey RunKey { get; } = new();

    public ServiceProvider Services { get; }

    public DataPaths Paths => new(Path.Combine(_dir, "library.db"), Path.Combine(_dir, "settings.json"), Path.Combine(_dir, "images"));

    public SettingsStore Settings => Services.GetRequiredService<SettingsStore>();

    public SharingController Sharing => Services.GetRequiredService<SharingController>();

    public StreamRegistry Streams => Services.GetRequiredService<StreamRegistry>();

    public ActivityHub Hub => Services.GetRequiredService<ActivityHub>();

    public AccountService Accounts => Services.GetRequiredService<AccountService>();

    public ViewerSearches Searches => Services.GetRequiredService<ViewerSearches>();

    public Uri Ping => new($"https://127.0.0.1:{Port}/api/v1/ping");

    /// <summary>The SHA-256 of the certificate the last request through <see cref="TlsClient"/> saw.</summary>
    public string? ServedFingerprint { get; private set; }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void OpenDatabase() => Services.GetRequiredService<LibraryDatabase>().Open().Dispose();

    public void SetSharing(Func<SharingSettings, SharingSettings> change) =>
        Settings.Update(s => s with { Sharing = change(s.Sharing) });

    public async Task AssertRefusedAsync()
    {
        using var client = new TcpClient();
        var thrown = await Assert.ThrowsAnyAsync<SocketException>(
            () => client.ConnectAsync(IPAddress.Loopback, Port, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(SocketError.ConnectionRefused, thrown.SocketErrorCode);
    }

    public HttpClient TlsClient()
    {
#pragma warning disable MA0039, CA5359 // The certificate is self-signed, and these tests read it instead of pinning it.
        var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                ServedFingerprint = certificate!.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
                return true;
            },
        });
#pragma warning restore MA0039, CA5359
        _disposables.Add(client);
        return client;
    }

    public async Task<PingReply> PingAsync()
    {
        var reply = await TlsClient().GetFromJsonAsync<PingReply>(Ping, ApiJson.Options, TestContext.Current.CancellationToken);
        return reply!;
    }

    /// <summary>A person with a device, signed in as a real client would be.</summary>
    public IssuedDevice NewDevice(string name = "Anna")
    {
        var account = Services.GetRequiredService<AccountService>().AddPerson(name, null);
        return Services.GetRequiredService<DeviceStore>().Issue(account.Id, "Phone", "198.51.100.1");
    }

    /// <summary>A stream in the registry with nothing behind it, for what only counts them.</summary>
    public StreamLease OpenStream()
    {
        var device = NewDevice().Device;
        var accounts = Services.GetRequiredService<AccountStore>();
        var account = accounts.Get(device.UserId)!;
        var who = new RequestViewer(accounts.ViewerOf(account), account, device);
        var lease = Streams.Open(who, new ReachableFile(1, 1, "unused", "Movie.mkv", "Movie", null, null, false), CancellationToken.None, out _);
        _disposables.Add(lease);
        return lease;
    }

    /// <summary>A movie far bigger than a socket holds, so its stream stays open until something ends it.</summary>
    /// <returns>The file's id.</returns>
    public async Task<int> AddLongMovieAsync()
    {
        var path = Directory.CreateDirectory(Path.Combine(_dir, "library")).FullName;
        await using (var movie = File.Create(Path.Combine(path, "Movie.mkv"))) movie.SetLength(256L * 1024 * 1024);

        var folder = Services.GetRequiredService<FolderStore>().Add(path);
        await Services.GetRequiredService<LibraryScanner>().ScanAsync(folder, TestContext.Current.CancellationToken);
        var titles = Services.GetRequiredService<TitleStore>();
        var titleId = titles.Upsert(new TitleDetails(1, TitleKind.Movie, "Movie", 2020, null, "About.", 7.0, "/poster.jpg", 100, []));
        var file = Services.GetRequiredService<FileStore>().ByFolder(folder.Id).Single();
        Assert.True(new MatchStore(Services.GetRequiredService<LibraryDatabase>()).SetMatch(file.Id, titleId, titles.EpisodeIds(titleId, []), 1.0, "auto"));
        return file.Id;
    }

    /// <summary>Opens a long response and leaves it open.</summary>
    public async Task<Stream> OpenBodyAsync(string route, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://127.0.0.1:{Port}{route}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await TlsClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        _disposables.Add(response);
        return await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Streams.CloseAll();
        await Sharing.StopForExitAsync();
        foreach (var disposable in _disposables) disposable.Dispose();
        await Services.DisposeAsync();

        // Pooled connections keep library.db open, and a request may still be finishing.
        for (var attempt = 1; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(20);
            }
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Tests don't use the network.");
    }

    private sealed class NoSleepBlock : ISleepBlocker
    {
        public void Hold()
        {
        }

        public void Release()
        {
        }
    }
}

/// <summary>Stands in for the HKCU Run key, so no test writes to the registry.</summary>
internal sealed class FakeRunKey : IRunKey
{
    public string? Command { get; private set; }

    public bool Refuses { get; set; }

    public void Set(string? command)
    {
        if (Refuses) throw new UnauthorizedAccessException("The key is locked.");
        Command = command;
    }
}

/// <summary>A reason a test switches by hand.</summary>
internal sealed class FakeTrayReason : ITrayReason
{
    public event Action? Changed;

    public bool Applies { get; private set; }

    public string Status { get; set; } = "Busy";

    public string? QuitQuestion { get; set; }

    public void Set(bool applies)
    {
        Applies = applies;
        Changed?.Invoke();
    }
}
