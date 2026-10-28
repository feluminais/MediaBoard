using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Metadata.Tmdb;
using MediaBoard.Core.Playback;
using MediaBoard.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop.Tests;

public sealed class CompositionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mediaboard-composition-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DataPaths Paths => new(Path.Combine(_dir, "library.db"), Path.Combine(_dir, "settings.json"), Path.Combine(_dir, "images"));

    [Fact]
    public async Task Composition_ResolvesTheShell_WithoutStartingAnything()
    {
        var services = Composition.Build(Paths);
        await using var _ = services;

        Assert.NotNull(services.GetRequiredService<MainWindowViewModel>());
        Assert.NotNull(services.GetRequiredService<AppHost>());
        Assert.NotNull(services.GetRequiredService<ISearchService>());
        Assert.NotNull(services.GetRequiredService<IPlaybackService>());

        // One TMDB client for everyone, so its request pacing holds app-wide.
        Assert.Same(services.GetRequiredService<TmdbClient>(), services.GetRequiredService<IMetadataProvider>());
        Assert.Same(services.GetRequiredService<LocalLibrary>(), services.GetRequiredService<ILibrary>());

        // Building and resolving opens nothing: the database is created by AppHost.Start, in the background.
        Assert.False(File.Exists(Paths.Database));
    }
}
