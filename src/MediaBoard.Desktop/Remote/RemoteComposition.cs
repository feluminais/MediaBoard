using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Players;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Settings;
using MediaBoard.Desktop.Sharing;
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
    /// <param name="restart">How this run starts over, or null when it can't.</param>
    public static ServiceProvider Build(DataPaths paths, StoredConnection connection, Action<IServiceCollection>? overrides = null, AppRestart? restart = null)
    {
        var services = new ServiceCollection();

        Composition.AddBasics(services, paths, restart);

        services.AddSingleton(sp => Link.Open(connection, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => sp.GetRequiredService<Link>().Client);
        services.AddSingleton(sp => sp.GetRequiredService<Link>().Monitor);
        services.AddSingleton(sp => sp.GetRequiredService<Link>().Token);

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

        services.AddSingleton(sp => AppCapabilities.Remote(sp.GetRequiredService<ConnectionMonitor>()));
        services.AddSingleton(new ShellParts(() => Task.CompletedTask, RescanAll: null, Folders: null, Sharing: null));
        services.AddSingleton<RemoteHost>();
        services.AddSingleton<ITrayReason>(sp => new PlaybackTrayReason(sp.GetRequiredService<ActivityHub>(), sp.GetRequiredService<PlaybackService>()));

        services.AddSingleton(sp => new ViewerAccount(
            connection,
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<DeviceToken>(),
            sp.GetRequiredService<ConnectionMonitor>(),
            sp.GetRequiredService<IPlaybackService>(),
            sp.GetRequiredService<ProgressOutbox>(),
            sp.GetRequiredService<AppRestart>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ModeSwitch(
            sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<AppRestart>(), sp.GetRequiredService<ActivityHub>()));
        services.AddSingleton(sp =>
        {
            var dialogs = sp.GetRequiredService<Dialogs>();
            return new AccountViewModel(
                sp.GetRequiredService<ViewerAccount>(),
                sp.GetRequiredService<ModeSwitch>(),
                sp.GetRequiredService<ActivityHub>(),
                (question, yes) => dialogs.ConfirmAsync(question, yes));
        });
        services.AddSingleton(sp => new ConnectionPanelViewModel(
            sp.GetRequiredService<ConnectionMonitor>(), connection.Endpoint.ServerName, sp.GetRequiredService<ViewerAccount>().ReturnToConnect));

        // PlayerExitPrompt stays out: leaving a player running would stop the proxy under it.
        Composition.AddViewModels(services);
        overrides?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>The client reports to the monitor, which listens through the client, so the two are made together.</summary>
    private sealed record Link(ApiClient Client, ConnectionMonitor Monitor, DeviceToken Token)
    {
        public static Link Open(StoredConnection connection, TimeProvider time)
        {
            var token = new DeviceToken(connection.ProtectedToken);
            ConnectionMonitor? monitor = null;
            var client = new ApiClient(connection.Endpoint, token.Read, happened => monitor?.Report(happened), time);
            monitor = new ConnectionMonitor(client, time);
            return new Link(client, monitor, token);
        }
    }
}
