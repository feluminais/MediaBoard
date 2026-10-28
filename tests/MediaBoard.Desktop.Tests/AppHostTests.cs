using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Storage;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop.Tests;

/// <summary>AppHost with the real services on a temp data folder. Nothing reaches the network: HTTP throws and the probe says offline.</summary>
public sealed class AppHostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mediaboard-host-").FullName;

    public void Dispose()
    {
        // Pooled connections keep library.db open, and a query the shell started may still be finishing.
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

    private ServiceProvider Build(string databasePath, TimeProvider? folderClock = null) => Composition.Build(
        new DataPaths(databasePath, Path.Combine(_dir, "settings.json"), Path.Combine(_dir, "images")),
        services =>
        {
            if (folderClock is not null) services.AddSingleton(new FolderRecheck(folderClock, FolderRecheck.DefaultInterval));
            services.AddSingleton(_ => new HttpClient(new NoNetwork()));
            services.AddSingleton(sp => new Connectivity(
                _ => Task.FromResult(false), TimeProvider.System, sp.GetRequiredService<ActivityHub>()));
        });

    private static async Task RunAsync(ServiceProvider services, Func<MainWindowViewModel, Task> check)
    {
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var host = services.GetRequiredService<AppHost>();
        host.Start();
        try
        {
            await host.Startup.WaitAsync(TimeSpan.FromSeconds(30));
            Dispatcher.UIThread.RunJobs();
            await check(vm);
        }
        finally
        {
            await host.StopAsync();
            await services.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task UnopenableDatabase_ShowsOnStatusBarAndHome_AndOpensActivity()
    {
        // A directory where library.db should be: SQLite can't open it.
        var database = Directory.CreateDirectory(Path.Combine(_dir, "library.db")).FullName;

        await RunAsync(Build(database), vm =>
        {
            Assert.Equal("Can't open the library", vm.StatusBar.LibraryProblem);
            Assert.Null(vm.StatusBar.Progress); // not "Up to date"
            var home = Assert.IsType<HomeViewModel>(vm.CurrentPage);
            Assert.True(home.ShowError);
            Assert.Contains(database, home.LibraryError);
            Assert.True(vm.IsActivityOpen);
            Assert.Contains(vm.Activity.Entries, e => e.IsError && e.Message.StartsWith("Can't open the library", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task SavedProgress_TellsThePagesToReload()
    {
        var database = Path.Combine(_dir, "library.db");
        var library = Directory.CreateDirectory(Path.Combine(_dir, "Movies")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(library, "Movie.2020.mkv"), new byte[4096]);
        var services = Build(database);
        services.GetRequiredService<FolderStore>().Add(library);

        await RunAsync(services, _ =>
        {
            int fileId;
            using (var connection = services.GetRequiredService<LibraryDatabase>().Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id FROM files";
                fileId = Convert.ToInt32(command.ExecuteScalar());
            }

            var changes = 0;
            services.GetRequiredService<ILibrary>().Changed += () => Interlocked.Increment(ref changes);

            services.GetRequiredService<IPlaybackService>().AnswerFinished(fileId, finished: true);

            Assert.Equal(1, changes);
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task FolderThatComesBack_IsScannedAndWatchedAgain()
    {
        var clock = new ManualClock();
        var movies = Path.Combine(_dir, "Movies"); // not there yet: the drive is unplugged
        var services = Build(Path.Combine(_dir, "library.db"), clock);
        services.GetRequiredService<FolderStore>().Add(movies);

        await RunAsync(services, async vm =>
        {
            var hub = services.GetRequiredService<ActivityHub>();
            var files = services.GetRequiredService<FileStore>();
            var folderId = services.GetRequiredService<FolderStore>().All().Single().Id;
            Assert.Equal(["Movies"], hub.Current.UnavailableFolders);

            Directory.CreateDirectory(movies);
            await File.WriteAllBytesAsync(Path.Combine(movies, "Heat.1995.mkv"), new byte[4096]);
            clock.Advance(FolderRecheck.DefaultInterval);

            await Ui.WaitUntil(() => hub.Current.UnavailableFolders.Count == 0 && files.ByFolder(folderId).Count == 1);

            // The watcher picked the folder up too.
            await File.WriteAllBytesAsync(Path.Combine(movies, "Alien.1979.mkv"), new byte[4096]);
            await Ui.WaitUntil(() => files.ByFolder(folderId).Count == 2);
        });
    }

    [AvaloniaFact]
    public async Task FailedScan_ShowsOnStatusBar()
    {
        var database = Path.Combine(_dir, "library.db");
        var library = Directory.CreateDirectory(Path.Combine(_dir, "Movies")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(library, "Movie.2020.mkv"), new byte[4096]);
        var services = Build(database);

        services.GetRequiredService<FolderStore>().Add(library);
        // Every insert into files fails, as a full disk would make it.
        using (var connection = services.GetRequiredService<LibraryDatabase>().Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER fail_scan BEFORE INSERT ON files BEGIN SELECT RAISE(ABORT, 'disk I/O error'); END;";
            command.ExecuteNonQuery();
        }

        await RunAsync(services, vm =>
        {
            Assert.Equal("Scan failed: Movies", vm.StatusBar.FailedScans);
            Assert.Null(vm.StatusBar.Progress);
            Assert.Null(vm.StatusBar.LibraryProblem);
            return Task.CompletedTask;
        });
    }
}
