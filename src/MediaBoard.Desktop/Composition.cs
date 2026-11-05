using System.Net;
using System.Net.Http.Headers;
using MediaBoard.Core;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Identification;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Metadata.Tmdb;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Players;
using MediaBoard.Core.Probing;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using MediaBoard.Desktop.Sharing;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Server;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop;

/// <summary>Where the app keeps its data. <see cref="Default"/> follows <see cref="AppPaths"/> (and so MEDIABOARD_DATA_DIR).</summary>
/// <param name="ErrorLog">Where unhandled exceptions go; next to the settings file when not given.</param>
public sealed record DataPaths(string Database, string Settings, string Images, string? ErrorLog = null)
{
    public static DataPaths Default => new(AppPaths.DatabasePath, AppPaths.SettingsPath, AppPaths.ImagesDir, AppPaths.ErrorLogPath);

    /// <summary>The sharing server's certificate, next to the settings file.</summary>
    public string Certificate => Path.Combine(Path.GetDirectoryName(Settings) ?? ".", "certificate.bin");
}

/// <summary>
/// Builds the service graph. Every Core service is a singleton; building and resolving starts nothing (no scan, no
/// network): <see cref="AppHost.Start"/> does that. Dispose the provider with <c>DisposeAsync</c>, because
/// <see cref="PlaybackService"/> is only async-disposable.
/// </summary>
public static class Composition
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    public static ServiceProvider Build() => Build(DataPaths.Default);

    /// <param name="overrides">Runs last, so a test can replace a registration (for instance a Connectivity that never
    /// touches the network).</param>
    public static ServiceProvider Build(DataPaths paths, Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new ActivityHub(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(_ => new SettingsStore(paths.Settings));
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new ErrorReporter(
                sp.GetRequiredService<ActivityHub>(),
                paths.ErrorLog ?? Path.Combine(Path.GetDirectoryName(paths.Settings) ?? ".", "error.log"),
                () => settings.Load().TmdbKey);
        });
        services.AddSingleton(_ => new LibraryDatabase(paths.Database));

        // One client for TMDB, the connectivity probe and images. It is created by hand, not through
        // IHttpClientFactory, whose default logging would write request URLs (with api_key) to the log.
        services.AddSingleton(_ => CreateHttpClient());

        services.AddSingleton(sp => new Connectivity(
            Connectivity.TmdbProbe(sp.GetRequiredService<HttpClient>()),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ActivityHub>()));

        // One TmdbClient: its request pacing is per instance.
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new TmdbClient(
                sp.GetRequiredService<HttpClient>(),
                () => settings.Load().TmdbKey,
                sp.GetRequiredService<ActivityHub>(),
                sp.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton<IMetadataProvider>(sp => sp.GetRequiredService<TmdbClient>());
        services.AddSingleton(sp => new ImageCache(
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<Connectivity>(), paths.Images));

        services.AddSingleton(sp =>
        {
            var images = sp.GetRequiredService<ImageCache>();
            return new PosterLoader(images.GetAsync);
        });

        services.AddSingleton(sp => new FolderStore(sp.GetRequiredService<LibraryDatabase>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new FileStore(sp.GetRequiredService<LibraryDatabase>()));
        services.AddSingleton(sp => new ProgressStore(sp.GetRequiredService<LibraryDatabase>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new TitleStore(sp.GetRequiredService<LibraryDatabase>(), sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton(sp => new LibraryScanner(
            sp.GetRequiredService<LibraryDatabase>(), sp.GetRequiredService<ActivityHub>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new LibraryWatcher(
            sp.GetRequiredService<LibraryScanner>(), sp.GetRequiredService<FolderStore>(), sp.GetRequiredService<ActivityHub>()));

        services.AddSingleton<IDurationProbe>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new FfprobeDurationProbe(() => settings.Load().FfprobePath, sp.GetRequiredService<ActivityHub>());
        });
        services.AddSingleton(sp => new IdentificationService(
            sp.GetRequiredService<LibraryDatabase>(),
            sp.GetRequiredService<IMetadataProvider>(),
            sp.GetRequiredService<IDurationProbe>(),
            sp.GetRequiredService<Connectivity>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ActivityHub>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new MetadataRefresher(
            sp.GetRequiredService<IMetadataProvider>(),
            sp.GetRequiredService<TitleStore>(),
            sp.GetRequiredService<Connectivity>(),
            sp.GetRequiredService<ActivityHub>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton(sp => new LocalLibrary(
            sp.GetRequiredService<LibraryDatabase>(),
            sp.GetRequiredService<IMetadataProvider>(),
            timeProvider: sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ILibrary>(sp => sp.GetRequiredService<LocalLibrary>());
        services.AddSingleton<ISearchService>(sp => new SearchService(
            sp.GetRequiredService<ILibrary>(),
            sp.GetRequiredService<IMetadataProvider>(),
            sp.GetRequiredService<Connectivity>(),
            sp.GetRequiredService<TitleStore>()));

        services.AddSingleton(sp => PlayerDetector.CreateDefault(sp.GetRequiredService<ActivityHub>()));
        services.AddSingleton(sp => new LocalPlaybackSource(
            sp.GetRequiredService<FileStore>(),
            sp.GetRequiredService<ProgressStore>()));
        services.AddSingleton(sp => new PlaybackService(
            sp.GetRequiredService<LocalPlaybackSource>(),
            sp.GetRequiredService<LocalPlaybackSource>(),
            sp.GetRequiredService<PlayerDetector>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ActivityHub>(),
            PlayerSessionFactory.Create));
        services.AddSingleton<IPlaybackService>(sp => sp.GetRequiredService<PlaybackService>());
        services.AddSingleton(sp =>
        {
            var playback = sp.GetRequiredService<PlaybackService>();
            return new PlayerWatcher(sp.GetRequiredService<SettingsStore>(), playback.AttachAsync, () => playback.CurrentPlayer);
        });

        AddSharing(services, paths);

        services.AddSingleton(FolderRecheck.Default);
        services.AddSingleton<AppHost>();
        services.AddSingleton(sp => new FolderActions(
            sp.GetRequiredService<FolderStore>(),
            sp.GetRequiredService<LibraryWatcher>(),
            sp.GetRequiredService<AppHost>(),
            sp.GetRequiredService<LocalLibrary>(),
            sp.GetRequiredService<ActivityHub>()));
        services.AddSingleton<IFolderActions>(sp => sp.GetRequiredService<FolderActions>());

        services.AddSingleton<Dialogs>();
        services.AddSingleton(sp =>
        {
            var dialogs = sp.GetRequiredService<Dialogs>();
            return new FoldersViewModel(
                sp.GetRequiredService<ILibrary>(),
                sp.GetRequiredService<IFolderActions>(),
                sp.GetRequiredService<ActivityHub>(),
                dialogs.PickFolderAsync,
                question => dialogs.ConfirmAsync(question, "Remove", danger: true));
        });
        services.AddSingleton(sp =>
        {
            var dialogs = sp.GetRequiredService<Dialogs>();
            return new SettingsViewModel(
                sp.GetRequiredService<SettingsStore>(),
                sp.GetRequiredService<PlayerDetector>(),
                sp.GetRequiredService<ActivityHub>(),
                dialogs.PickProgramAsync,
                dialogs.OpenUrl);
        });

        services.AddSingleton(sp =>
        {
            var dialogs = sp.GetRequiredService<Dialogs>();
            return new SharingViewModel(
                new SharingServices(
                    sp.GetRequiredService<SharingController>(),
                    sp.GetRequiredService<AccountService>(),
                    sp.GetRequiredService<AccountStore>(),
                    sp.GetRequiredService<DeviceStore>(),
                    sp.GetRequiredService<InviteStore>(),
                    sp.GetRequiredService<StreamRegistry>(),
                    sp.GetRequiredService<SharingLog>(),
                    sp.GetRequiredService<SignInGuard>(),
                    sp.GetRequiredService<ILibrary>(),
                    sp.GetRequiredService<SettingsStore>(),
                    sp.GetRequiredService<StartWithWindows>(),
                    sp.GetRequiredService<ActivityHub>(),
                    sp.GetRequiredService<TimeProvider>()),
                new SharingDialogs(dialogs.ConfirmAsync, dialogs.ShowSharingPanelAsync, dialogs.CopyAsync));
        });

        services.AddSingleton(sp => new PlayerExitPrompt(
            sp.GetRequiredService<IPlaybackService>(), sp.GetRequiredService<Dialogs>().AskClosePlayerAsync));
        services.AddSingleton(sp =>
        {
            var dialogs = sp.GetRequiredService<Dialogs>();
            return new PlaybackLauncher(sp.GetRequiredService<IPlaybackService>(), sp.GetRequiredService<ActivityHub>(), dialogs.AskFinishedAsync);
        });
        services.AddSingleton(sp =>
        {
            var dialogs = sp.GetRequiredService<Dialogs>();
            var detector = sp.GetRequiredService<PlayerDetector>();
            var settings = sp.GetRequiredService<SettingsStore>();
            var host = sp.GetRequiredService<AppHost>();
            return new TitleServices(
                sp.GetRequiredService<ILibrary>(),
                sp.GetRequiredService<ISearchService>(),
                sp.GetRequiredService<ActivityHub>(),
                sp.GetRequiredService<PosterLoader>(),
                sp.GetRequiredService<PlaybackLauncher>(),
                () => detector.Resolve(settings.Load())?.Name,
                dialogs.ConfirmDeleteAsync,
                name => dialogs.ConfirmAsync(
                    $"Mark \"{name}\" as not a movie or show? It leaves the library and isn't identified again. Your files aren't touched.", "Mark"),
                () => Task.Run(host.IdentifyPendingAsync));
        });

        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<ActivityPanelViewModel>();
        services.AddSingleton(sp => new MainWindowViewModel(
            sp.GetRequiredService<ILibrary>(),
            sp.GetRequiredService<StatusBarViewModel>(),
            sp.GetRequiredService<ActivityPanelViewModel>(),
            sp.GetRequiredService<ActivityHub>(),
            sp.GetRequiredService<FoldersViewModel>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<PosterLoader>(),
            sp.GetRequiredService<TitleServices>(),
            sp.GetRequiredService<FolderActions>().RescanAllAsync,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<SharingViewModel>()));
        overrides?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    // The controller asks for the server only when sharing first comes on, so a single-user run never builds it.
    private static void AddSharing(ServiceCollection services, DataPaths paths)
    {
        services.AddSingleton(sp => new AccountStore(sp.GetRequiredService<LibraryDatabase>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new DeviceStore(sp.GetRequiredService<LibraryDatabase>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new InviteStore(sp.GetRequiredService<LibraryDatabase>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new SharingLog(sp.GetRequiredService<LibraryDatabase>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new SignInGuard(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new AccountService(
            sp.GetRequiredService<LibraryDatabase>(),
            sp.GetRequiredService<AccountStore>(),
            sp.GetRequiredService<DeviceStore>(),
            sp.GetRequiredService<InviteStore>(),
            sp.GetRequiredService<SignInGuard>(),
            sp.GetRequiredService<SharingLog>()));

        services.AddSingleton(_ => new CertificateStore(paths.Certificate));
        services.AddSingleton(sp => new StreamRegistry(sp.GetRequiredService<TimeProvider>(), new SleepBlocker()));
        services.AddSingleton(sp => new ViewerAccess(sp.GetRequiredService<LibraryDatabase>()));
        services.AddSingleton(sp =>
        {
            var owner = sp.GetRequiredService<LocalLibrary>();
            var provider = sp.GetRequiredService<IMetadataProvider>();
            var connectivity = sp.GetRequiredService<Connectivity>();
            var titles = sp.GetRequiredService<TitleStore>();
            var access = sp.GetRequiredService<ViewerAccess>();
            var searches = new ViewerSearches(viewer => new SearchService(owner.ForViewer(viewer), provider, connectivity, titles, access, viewer));
            sp.GetRequiredService<AccountService>().AccountClosed += searches.Forget;
            return searches;
        });
        services.AddSingleton(sp => new SharedLibrary(
            sp.GetRequiredService<LocalLibrary>(),
            sp.GetRequiredService<ViewerAccess>(),
            sp.GetRequiredService<FileStore>(),
            sp.GetRequiredService<ProgressStore>(),
            sp.GetRequiredService<ViewerSearches>().For,
            sp.GetRequiredService<ImageCache>()));
        services.AddSingleton(sp => new SharingServer(
            sp.GetRequiredService<AccountService>(),
            sp.GetRequiredService<AccountStore>(),
            sp.GetRequiredService<DeviceStore>(),
            sp.GetRequiredService<SharedLibrary>(),
            sp.GetRequiredService<StreamRegistry>(),
            sp.GetRequiredService<SharingLog>(),
            time: sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new SharingController(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<CertificateStore>(),
            sp.GetRequiredService<InviteStore>(),
            sp.GetRequiredService<StreamRegistry>(),
            sp.GetRequiredService<ActivityHub>(),
            sp.GetRequiredService<ViewerSearches>(),
            sp.GetRequiredService<SharingServer>));
        services.AddSingleton<ITrayReason>(sp => new SharingTrayReason(sp.GetRequiredService<SharingController>()));

        services.AddSingleton<IRunKey, RegistryRunKey>();
        services.AddSingleton(sp => new StartWithWindows(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<IRunKey>(),
            Environment.ProcessPath ?? "",
            sp.GetRequiredService<ActivityHub>()));
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = HttpTimeout,
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MediaBoard", "1.0"));
        return http;
    }
}
