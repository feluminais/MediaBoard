using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Players;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;
using MediaBoard.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop.Remote;

/// <summary>
/// The service graph of a viewer: the same shell over the server's library. No database, scanner, watcher or sharing
/// server exists here. Building and resolving starts nothing and reaches no network: <see cref="RemoteHost.Start"/> does
/// that. Dispose the provider with <c>DisposeAsync</c>. A change of server or account restarts the process, so one graph
/// keeps one token, and the provider disposing is what replaces the client and the library.
/// </summary>
public static class RemoteComposition
{
    /// <param name="paths">The data folder.</param>
    /// <param name="connection">The server. <see cref="ModeGate.HasUsableEndpoint"/> has passed its endpoint.</param>
    /// <param name="overrides">Runs last, so a test can replace a registration.</param>
    public static ServiceProvider Build(DataPaths paths, StoredConnection connection, Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection();

        Composition.AddBasics(services, paths);

        services.AddSingleton(sp => Link.Open(connection, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => sp.GetRequiredService<Link>().Client);
        services.AddSingleton(sp => sp.GetRequiredService<Link>().Monitor);

        services.AddSingleton(sp => new RemoteLibrary(sp.GetRequiredService<ApiClient>(), sp.GetRequiredService<ConnectionMonitor>()));
        services.AddSingleton<ILibrary>(sp => sp.GetRequiredService<RemoteLibrary>());
        services.AddSingleton<ISearchService>(sp => new RemoteSearchService(sp.GetRequiredService<ApiClient>()));
        services.AddSingleton(sp => new RemoteImages(sp.GetRequiredService<ApiClient>(), paths.Images, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new PosterLoader(sp.GetRequiredService<RemoteImages>().GetAsync));

        services.AddSingleton(sp => new StreamProxy(sp.GetRequiredService<ApiClient>()));
        services.AddSingleton(sp => new ProgressOutbox(
            sp.GetRequiredService<ApiClient>(),
            paths.Outbox(connection.Endpoint.Fingerprint, connection.AccountName),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new RemotePlaybackSource(
            sp.GetRequiredService<ApiClient>(), sp.GetRequiredService<StreamProxy>(), sp.GetRequiredService<ProgressOutbox>()));
        services.AddSingleton(sp => new PlaybackService(
            sp.GetRequiredService<RemotePlaybackSource>(),
            sp.GetRequiredService<ProgressOutbox>(),
            sp.GetRequiredService<PlayerDetector>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ActivityHub>(),
            PlayerSessionFactory.Create));
        services.AddSingleton<IPlaybackService>(sp => sp.GetRequiredService<PlaybackService>());

        services.AddSingleton<IFolderActions, NoFolders>();
        services.AddSingleton(new ShellParts(() => Task.CompletedTask, RescanAll: null, Sharing: null));
        services.AddSingleton<RemoteHost>();

        // PlayerExitPrompt stays out: leaving a player running would stop the proxy under it.
        Composition.AddViewModels(services);
        overrides?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>The client reports to the monitor, which listens through the client, so the two are made together.</summary>
    private sealed record Link(ApiClient Client, ConnectionMonitor Monitor)
    {
        public static Link Open(StoredConnection connection, TimeProvider time)
        {
            var token = new Lazy<string?>(() => RemoteSession.TryUnprotect(connection.ProtectedToken));
            ConnectionMonitor? monitor = null;
            var client = new ApiClient(connection.Endpoint, () => token.Value, happened => monitor?.Report(happened), time);
            monitor = new ConnectionMonitor(client, time);
            return new Link(client, monitor);
        }
    }

    /// <summary>The folders are on the owner's PC.</summary>
    private sealed class NoFolders : IFolderActions
    {
        private static readonly Task<FolderOutcome> Refused =
            Task.FromResult(FolderOutcome.Refused("Only the PC sharing this library can change its folders."));

        public Task<FolderOutcome> AddAsync(string path) => Refused;

        public Task<FolderOutcome> RemoveAsync(int folderId) => Refused;

        public Task<FolderOutcome> SetEnabledAsync(int folderId, bool enabled) => Refused;

        public Task<FolderOutcome> RepointAsync(int folderId, string newPath) => Refused;

        public Task WhenIdleAsync() => Task.CompletedTask;
    }
}
