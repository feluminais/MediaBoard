using Dapper;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Identification;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Metadata.Tmdb;
using MediaBoard.Core.Parsing;
using MediaBoard.Core.Probing;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Corpus;

/// <summary>
/// Scans the owner's library (as sparse files at the real sizes, from <see cref="CorpusSnapshot"/>) and
/// identifies it with a real <see cref="TmdbClient"/> over whatever HTTP handler the caller supplies: a
/// replaying or a recording <see cref="FixtureHandler"/>. Returns the result as reference items.
/// </summary>
public static class CorpusPipeline
{
    /// <param name="tmdbTime">Clock for the client's request pacing: the real one when talking to TMDB, a fake one otherwise.</param>
    public static async Task<IReadOnlyList<ReferenceItem>> IdentifyAsync(
        HttpMessageHandler handler, string tmdbKey, TimeProvider tmdbTime, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mediaboard-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var library = new TempLibrary();
        try
        {
            foreach (var (path, size) in CorpusSnapshot.Load())
                library.File(path, size);

            var clock = new FakeClock();
            var hub = new ActivityHub(clock);
            var db = new LibraryDatabase(Path.Combine(scratch, "library.db"));
            var settings = new SettingsStore(Path.Combine(scratch, "settings.json"));
            settings.Save(new AppSettings { TmdbKey = tmdbKey });
            using var http = new HttpClient(handler, disposeHandler: false);
            var tmdb = new TmdbClient(http, () => tmdbKey, hub, tmdbTime);
            using var connectivity = new Connectivity(_ => Task.FromResult(true), clock, hub);
            using var service = new IdentificationService(db, tmdb, new NoDuration(), connectivity, settings, hub, clock);

            var folder = new FolderStore(db, clock).Add(library.Root);
            await new LibraryScanner(db, hub, clock).ScanAsync(folder, ct);
            await service.RunPendingAsync(ct);

            return Group(Read(db, folder.Id));
        }
        finally
        {
            // Pooled connections keep the database file locked on Windows.
            SqliteConnection.ClearAllPools();
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>What identification stored for one file.</summary>
    public sealed record IdentifiedFile(string RelPath, MediaKind ParsedKind, string Status, TitleRow? Title);

    private static IReadOnlyList<IdentifiedFile> Read(LibraryDatabase db, int folderId)
    {
        var files = new FileStore(db).ByFolder(folderId).Where(f => !f.Missing && f.Status != "not_media").ToList();
        var titles = new TitleStore(db);
        using var connection = db.Open();
        var links = connection
            .Query<(long FileId, long TitleId)>("SELECT file_id, title_id FROM file_titles ORDER BY confidence DESC, title_id")
            .GroupBy(l => l.FileId)
            .ToDictionary(g => g.Key, g => (int)g.First().TitleId);

        return files
            .Select(f => new IdentifiedFile(
                f.RelPath, f.Parsed.Kind, f.Status, links.TryGetValue(f.Id, out var titleId) ? titles.Get(titleId) : null))
            .ToList();
    }

    /// <summary>Turns per-file results into top-level library items (see <see cref="ReferenceItem"/>).</summary>
    public static IReadOnlyList<ReferenceItem> Group(IEnumerable<IdentifiedFile> files)
    {
        var items = new List<ReferenceItem>();
        foreach (var top in files.GroupBy(f => f.RelPath.Split('\\')[0], StringComparer.Ordinal))
        {
            var episodes = top.Where(f => f.ParsedKind == MediaKind.Episode).ToList();
            var movies = top.Where(f => f.ParsedKind != MediaKind.Episode).ToList();

            var shows = episodes.GroupBy(f => f.Title?.TmdbId?.ToString() ?? f.Title?.Name ?? "").ToList();
            foreach (var show in shows)
            {
                var path = shows.Count == 1 ? top.Key : $"{top.Key}#{show.Key}";
                items.Add(ItemFor(path, TitleKind.Show, show.ToList()));
            }

            // A folder with one movie is the item; otherwise each movie file is its own item.
            var wholeFolder = episodes.Count == 0 && movies.Count == 1;
            foreach (var movie in movies)
                items.Add(ItemFor(wholeFolder ? top.Key : movie.RelPath, TitleKind.Movie, [movie]));
        }

        return items.OrderBy(i => i.Path, StringComparer.Ordinal).ToList();
    }

    private static ReferenceItem ItemFor(string path, TitleKind fallbackKind, IReadOnlyList<IdentifiedFile> files)
    {
        var title = files[0].Title;
        var kind = title?.Kind ?? fallbackKind;
        var statuses = files.Select(f => f.Status).Distinct().ToList();
        var status = statuses.All(s => s == "matched")
            ? "matched"
            : new[] { "review", "offline", "pending" }.FirstOrDefault(statuses.Contains) ?? statuses.First(s => s != "matched");

        return new ReferenceItem(
            path, kind == TitleKind.Show ? "show" : "movie", title?.TmdbId, title?.Name, title?.Year, status);
    }

    /// <summary>Sparse files have no content for ffprobe to read.</summary>
    private sealed class NoDuration : IDurationProbe
    {
        public Task<double?> GetDurationSecondsAsync(string path, CancellationToken ct) => Task.FromResult<double?>(null);
    }
}
