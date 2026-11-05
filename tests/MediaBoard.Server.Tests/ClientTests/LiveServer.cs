using MediaBoard.Core.Accounts;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;

namespace MediaBoard.Server.Tests.ClientTests;

/// <summary>The real server on a real TLS socket, with one signed-in viewer, for a client that needs the whole way through.</summary>
internal sealed class LiveServer : IAsyncDisposable
{
    private LibraryFolder? _folder;
    private int _lastTmdbId = 100;

    private LiveServer(ServerFixture fixture, int port)
    {
        Fixture = fixture;
        Port = port;
        Fingerprint = CertificateStore.Fingerprint(fixture.NewCertificate());
    }

    public ServerFixture Fixture { get; }

    public int Port { get; }

    public string Fingerprint { get; }

    public Account Sam { get; private set; } = null!;

    public IssuedDevice Phone { get; private set; } = null!;

    public string OwnerName => Fixture.Accounts.Get(LibraryDatabase.OwnerId)!.Name;

    public static async Task<LiveServer> StartAsync(CancellationToken ct)
    {
        var fixture = new ServerFixture();
        var port = ServerFixture.FreePort();
        await fixture.Server.StartAsync(new SharingOptions(port, fixture.NewCertificate()) { LoopbackOnly = true }, ct);
        var live = new LiveServer(fixture, port) { Sam = fixture.NewPerson("Sam") };
        live.Phone = fixture.Join(live.Sam);
        return live;
    }

    public ApiClient NewClient() =>
        new(new ServerEndpoint("Library", [$"127.0.0.1:{Port}"], Fingerprint), () => Phone.Token, _ => { });

    /// <summary>Adds a film of this many bytes. The file is sparse, so a big one costs nothing.</summary>
    public async Task<(int Id, string Path)> AddFilmAsync(string fileName, long bytes)
    {
        _folder ??= Fixture.AddFolder("films");
        var id = await Fixture.AddFileAsync(_folder, fileName, Fixture.Movie("Heat", ++_lastTmdbId));
        var path = Path.Combine(_folder.Path, fileName);
        await using var file = File.OpenWrite(path);
        file.SetLength(bytes);
        return (id, path);
    }

    public ValueTask DisposeAsync() => Fixture.DisposeAsync();
}
