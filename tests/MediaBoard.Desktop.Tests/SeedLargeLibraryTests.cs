using System.Globalization;
using MediaBoard.Core;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Storage;

namespace MediaBoard.Desktop.Tests;

/// <summary>
/// Not part of a normal run. With MEDIABOARD_SEED=1 and MEDIABOARD_DATA_DIR pointing at a fresh directory, fills
/// that directory's library with 5,000 titles (movies, plus shows with a few episodes) and no posters, so the app
/// started with the same MEDIABOARD_DATA_DIR can be scrolled to check that the grids virtualize.
/// <code>
/// $env:MEDIABOARD_SEED = "1"; $env:MEDIABOARD_DATA_DIR = "C:\some\fresh\dir"
/// dotnet test --project tests/MediaBoard.Desktop.Tests -- --filter-trait "Category=Seed"
/// </code>
/// The video files are empty files under &lt;data dir&gt;\seed-library, so the startup scan finds them and keeps them.
/// The titles carry made-up TMDB ids, so opening one in the app is not expected to fetch anything real.
/// </summary>
[Trait("Category", "Seed")]
public class SeedLargeLibraryTests
{
    private const string SeedVariable = "MEDIABOARD_SEED";
    private const int MovieCount = 4_250;
    private const int ShowCount = 750;
    private const int EpisodesPerShow = 3;
    private const int FakeTmdbBase = 900_000_000;

    private static readonly string[] Adjectives =
        ["Quiet", "Last", "Silent", "Golden", "Broken", "Hidden", "Crimson", "Distant", "Electric", "Paper"];

    private static readonly string[] Nouns =
        ["River", "Harbor", "Garden", "Signal", "Empire", "Letter", "Mirror", "Orchard", "Station", "Winter"];

    [Fact]
    public async Task SeedLibrary_FillsTheDataDirWithFiveThousandTitles()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(SeedVariable) == "1",
            $"Seeding is off: set {SeedVariable}=1 and {AppPaths.DataDirEnvVar} to a fresh directory to run it.");
        // AppPaths would fall back to the real %LOCALAPPDATA%\MediaBoard, which this must never touch.
        Assert.False(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataDirEnvVar)),
            $"{AppPaths.DataDirEnvVar} is not set; refusing to seed the real library.");

        var ct = TestContext.Current.CancellationToken;
        var db = new LibraryDatabase(AppPaths.DatabasePath);
        var titles = new TitleStore(db);
        var matches = new MatchStore(db);
        var files = new FileStore(db);
        var folders = new FolderStore(db);
        Assert.True(folders.All().Count == 0, $"{AppPaths.DatabasePath} already has a library; seed into a fresh {AppPaths.DataDirEnvVar}.");

        var root = Path.Combine(AppPaths.DataDir, "seed-library");
        var plan = Plan();
        foreach (var relPath in plan.SelectMany(t => t.Files))
        {
            var path = Path.Combine(root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Create(path).Dispose();
        }

        // The real scanner reads the files, so the stored size, time and parse are what the app's own scan expects
        // and the startup scan changes nothing.
        var folder = folders.Add(root);
        var scan = await new LibraryScanner(db, new ActivityHub()).ScanAsync(folder, ct);
        Assert.Equal(plan.Sum(t => t.Files.Count), scan.Added);

        var fileIds = files.ByFolder(folder.Id).ToDictionary(f => f.RelPath, f => f.Id, StringComparer.OrdinalIgnoreCase);
        for (var n = 0; n < plan.Count; n++)
        {
            var item = plan[n];
            var titleId = titles.Upsert(new TitleDetails(
                FakeTmdbBase + n, item.Kind, item.Name, item.Year, null, null, 4.0 + n % 60 / 10.0, null, null, []));
            var episodeIds = new List<int>();
            if (item.Kind == TitleKind.Show)
            {
                titles.UpsertEpisodes(titleId, new SeasonDetails(1, item.Files
                    .Select((_, i) => new EpisodeDetails(1, i + 1, $"Episode {i + 1}", 45, null)).ToList()));
                episodeIds.AddRange(titles.EpisodeIds(titleId, item.Files.Select((_, i) => (1, i + 1))));
            }

            for (var i = 0; i < item.Files.Count; i++)
            {
                IReadOnlyList<int> episodes = item.Kind == TitleKind.Show ? [episodeIds[i]] : [];
                Assert.True(matches.SetMatch(fileIds[item.Files[i]], titleId, episodes, 1.0, "auto"));
            }
        }

        var library = new LocalLibrary(db, null!);
        var movies = library.Titles(TitleKind.Movie, TitleSort.Name, includeHidden: false);
        var shows = library.Titles(TitleKind.Show, TitleSort.Name, includeHidden: false);
        Assert.Equal(MovieCount, movies.Count);
        Assert.Equal(ShowCount, shows.Count);
        Assert.All(movies.Concat(shows), c => Assert.Null(c.PosterPath));
        Assert.DoesNotContain(movies.Concat(shows), c => c.Missing);
    }

    private sealed record PlannedTitle(TitleKind Kind, string Name, int Year, IReadOnlyList<string> Files);

    private static List<PlannedTitle> Plan()
    {
        var plan = new List<PlannedTitle>(MovieCount + ShowCount);
        for (var i = 1; i <= MovieCount; i++)
        {
            var name = $"Seed Movie {i:D4} {Words(i)}";
            var year = 1950 + i * 37 % 76;
            plan.Add(new PlannedTitle(TitleKind.Movie, name, year, [$"{name} ({year}).mkv"]));
        }

        for (var i = 1; i <= ShowCount; i++)
        {
            var name = $"Seed Show {i:D4} {Words(i * 7)}";
            var year = 1980 + i * 13 % 46;
            var files = Enumerable.Range(1, EpisodesPerShow)
                .Select(e => Path.Combine($"{name} ({year})", "Season 01", string.Create(CultureInfo.InvariantCulture, $"{name} S01E{e:D2}.mkv")))
                .ToList();
            plan.Add(new PlannedTitle(TitleKind.Show, name, year, files));
        }

        return plan;
    }

    private static string Words(int i) => $"{Adjectives[i % Adjectives.Length]} {Nouns[i / Adjectives.Length % Nouns.Length]}";
}
