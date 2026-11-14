using CozyCouch.Core.Activity;
using CozyCouch.Core.Library;
using CozyCouch.Core.Metadata;
using CozyCouch.Core.Scanning;
using CozyCouch.Core.Settings;
using CozyCouch.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CozyCouch.Desktop.Tests.Support;

/// <summary>
/// The real services on a temp data folder, never started: no startup scan, no watcher, no network (HTTP throws and
/// the probe says offline). Library folders for a test are made with <see cref="MakeLibrary"/>.
/// </summary>
internal sealed class ShellStack : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cozycouch-shell-").FullName;

    public ShellStack(Action<IServiceCollection>? overrides = null)
    {
        Services = Composition.Build(
            new DataPaths(Path.Combine(_dir, "library.db"), SettingsPath, Path.Combine(_dir, "images")),
            services =>
            {
                services.AddSingleton(_ => new HttpClient(new NoNetwork()));
                services.AddSingleton(sp => new Connectivity(
                    _ => Task.FromResult(false), TimeProvider.System, sp.GetRequiredService<ActivityHub>()));
                overrides?.Invoke(services);
            });
    }

    public ServiceProvider Services { get; }

    public string SettingsPath => Path.Combine(_dir, "settings.json");

    public ActivityHub Hub => Services.GetRequiredService<ActivityHub>();

    public ILibrary Library => Services.GetRequiredService<ILibrary>();

    public SettingsStore Settings => Services.GetRequiredService<SettingsStore>();

    public LibraryWatcher Watcher => Services.GetRequiredService<LibraryWatcher>();

    public IFolderActions Actions => Services.GetRequiredService<IFolderActions>();

    /// <summary>Creates a folder with the given (empty) files, outside the data folder's own files. Names may hold subfolders.</summary>
    public string MakeLibrary(string name, params string[] files)
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "libraries", name)).FullName;
        foreach (var file in files)
        {
            var path = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }

        return root;
    }

    /// <summary>Gives the scanned file a movie title, as a finished identification would.</summary>
    public void LinkMovie(string folderPath, string relPath, string name, int tmdbId)
    {
        var folder = Services.GetRequiredService<FolderStore>().All().Single(f => f.Path == folderPath);
        var file = Services.GetRequiredService<FileStore>().ByFolder(folder.Id).Single(f => f.RelPath == relPath);
        var title = Services.GetRequiredService<TitleStore>().Upsert(
            new TitleDetails(tmdbId, TitleKind.Movie, name, 1995, null, "About.", 7.0, $"/{tmdbId}.jpg", 100, []));
        Assert.True(new MatchStore(Services.GetRequiredService<LibraryDatabase>()).SetMatch(file.Id, title, [], 1.0, "auto"));
    }

    public void Dispose()
    {
        Services.DisposeAsync().AsTask().GetAwaiter().GetResult();

        // Pooled connections keep library.db open, and a scan may still be finishing.
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
                Thread.Sleep(20);
            }
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Tests don't use the network.");
    }
}
