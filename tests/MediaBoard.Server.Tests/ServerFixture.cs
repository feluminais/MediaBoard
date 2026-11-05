using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaBoard.Server.Tests;

/// <summary>A library on disk and a <see cref="SharingServer"/> over it, through TestHost or a real socket.</summary>
public sealed class ServerFixture : IAsyncDisposable
{
    public const string ServerName = "Test library";
    public const string Password = "correct horse battery";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];
    private WebApplication? _app;

    public ServerFixture(ILoggerProvider? logs = null)
    {
        Db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        Accounts = new AccountStore(Db);
        Devices = new DeviceStore(Db);
        Invites = new InviteStore(Db);
        Guard = new SignInGuard(TimeProvider.System);
        Service = new AccountService(Db, Accounts, Devices, Invites, Guard, new SharingLog(Db));

        Folders = new FolderStore(Db);
        Titles = new TitleStore(Db);
        Matches = new MatchStore(Db);
        Progress = new ProgressStore(Db);
        Provider = new FakeMetadataProvider();
        // Never probes: a test flips it with ReportOffline.
        Connectivity = new Connectivity(_ => Task.FromResult(false), TimeProvider.System, new ActivityHub());
        _disposables.Add(Connectivity);
        var http = new HttpClient(new NotFoundHandler());
        _disposables.Add(http);
        ImagesDir = Path.Combine(_dir, "images");
        Owner = new LocalLibrary(Db, Provider);
        Access = new ViewerAccess(Db);
        var shared = new SharedLibrary(
            Owner,
            Access,
            viewer => new SearchService(Owner.ForViewer(viewer), Provider, Connectivity, Titles, Access, viewer),
            new ImageCache(http, Connectivity, ImagesDir));
        Server = new SharingServer(Service, Accounts, Devices, shared, logs) { ServerName = ServerName };
    }

    public LibraryDatabase Db { get; }

    public AccountStore Accounts { get; }

    public DeviceStore Devices { get; }

    public InviteStore Invites { get; }

    public SignInGuard Guard { get; }

    public AccountService Service { get; }

    public SharingServer Server { get; }

    public FolderStore Folders { get; }

    public TitleStore Titles { get; }

    public MatchStore Matches { get; }

    public ProgressStore Progress { get; }

    public FakeMetadataProvider Provider { get; }

    public Connectivity Connectivity { get; }

    public LocalLibrary Owner { get; }

    public ViewerAccess Access { get; }

    public string ImagesDir { get; }

    /// <summary>TestHost has no socket, so each request takes its remote address from here.</summary>
    public IPAddress RemoteAddress { get; set; } = IPAddress.Parse("203.0.113.5");

    public WebApplication App => _app ?? throw new InvalidOperationException("Call StartTestHostAsync first.");

    public HttpClient Client { get; private set; } = null!;

    /// <summary>A fixture with the server mapped onto TestHost, with no socket.</summary>
    public static async Task<ServerFixture> StartTestHostAsync()
    {
        var fixture = new ServerFixture();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRoutingCore();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = fixture.RemoteAddress;
            return next(context);
        });
        fixture.Server.MapTo(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        fixture._app = app;
        fixture.Client = app.GetTestClient();
        return fixture;
    }

    public X509Certificate2 NewCertificate()
    {
        var certificate = new CertificateStore(Path.Combine(_dir, "certificate.bin")).LoadOrCreate();
        _disposables.Add(certificate);
        return certificate;
    }

    /// <summary>A client that talks to a real socket on this PC and accepts the self-signed certificate.</summary>
    public HttpClient TlsClient()
    {
#pragma warning disable MA0039, CA5359 // The test certificate is self-signed, as the real one is.
        var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
#pragma warning restore MA0039, CA5359
        _disposables.Add(client);
        return client;
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A library folder on disk, registered.</summary>
    public LibraryFolder AddFolder(string name)
    {
        var path = Path.Combine(_dir, "library", name);
        Directory.CreateDirectory(path);
        return Folders.Add(path);
    }

    public int Movie(string name, int tmdbId) => Titles.Upsert(new TitleDetails(
        tmdbId, TitleKind.Movie, name, 2020, null, $"About {name}.", 7.0, "/poster.jpg", 100, []));

    public int Show(string name, int tmdbId, int episodes)
    {
        var id = Titles.Upsert(new TitleDetails(tmdbId, TitleKind.Show, name, 2015, null, "About.", 8.0, "/poster.jpg", 45, [1]));
        Titles.UpsertEpisodes(id, new SeasonDetails(
            1, Enumerable.Range(1, episodes).Select(n => new EpisodeDetails(1, n, $"{name} 1x{n}", 45, null)).ToList()));
        return id;
    }

    /// <summary>Puts a file on disk in the folder, scans it and matches it to the title.</summary>
    public async Task<int> AddFileAsync(LibraryFolder folder, string relPath, int titleId, params (int Season, int Number)[] episodes)
    {
        var path = Path.Combine(folder.Path, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[4096], TestContext.Current.CancellationToken);
        await new LibraryScanner(Db, new ActivityHub()).ScanAsync(folder, TestContext.Current.CancellationToken);
        var file = new FileStore(Db).ByFolder(folder.Id).Single(r => r.RelPath == relPath);
        Assert.True(Matches.SetMatch(file.Id, titleId, Titles.EpisodeIds(titleId, episodes), 1.0, "auto"));
        return file.Id;
    }

    public Account NewPerson(string name = "Anna", IReadOnlyCollection<int>? folderIds = null) => Service.AddPerson(name, folderIds);

    /// <summary>Puts a password on an account the way a person does, through an invite.</summary>
    public IssuedDevice Join(Account account, string password = Password) =>
        Service.Redeem(Invites.Create(account.Id), password, "Laptop", "198.51.100.1").Issued!;

    public HttpRequestMessage Request(string method, string route, string? token = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Flags the account disabled and nothing else. <see cref="AccountService.Disable"/> also revokes the tokens, which
    /// would hide whether the server checks the flag itself.
    /// </summary>
    public void FlagDisabled(int userId)
    {
        using var connection = Db.Open();
        connection.Execute("UPDATE users SET disabled = 1 WHERE id = @userId", new { userId });
    }

    public static async Task<ApiErrorCode> CodeOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ApiErrorBody>(ApiJson.Options, TestContext.Current.CancellationToken);
        return body!.Code;
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public async ValueTask DisposeAsync()
    {
        await Server.StopAsync();
        if (_app is not null) await _app.DisposeAsync();
        foreach (var disposable in _disposables) disposable.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
