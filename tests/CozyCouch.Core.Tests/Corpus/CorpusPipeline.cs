using Dapper;
using CozyCouch.Core.Activity;
using CozyCouch.Core.Identification;
using CozyCouch.Core.Metadata;
using CozyCouch.Core.Metadata.Tmdb;
using CozyCouch.Core.Parsing;
using CozyCouch.Core.Probing;
using CozyCouch.Core.Scanning;
using CozyCouch.Core.Settings;
using CozyCouch.Core.Storage;
using CozyCouch.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace CozyCouch.Core.Tests.Corpus;

/// <summary>
/// Scans the owner's library (as sparse files at the real sizes, from <see cref="CorpusSnapshot"/>) and
/// identifies it with a real <see cref="TmdbClient"/> over whatever HTTP handler the caller supplies: a
/// replaying or a recording <see cref="FixtureHandler"/>. Returns the result as reference items.
/// </summary>
public static class CorpusPipeline
{
    /// <param name="tmdbTime">Clock for the client's request pacing: the real one when talking to TMDB, a fake one otherwise.</param>
    public static async Task<IReadOnlyList<ReferenceItem>> IdentifyAsync(
        HttpMessageHandler handler, string tmdbKey, TimeProvider tmdbTime, CancellationToken ct) =>
        Group(await IdentifyFilesAsync(handler, tmdbKey, tmdbTime, ct));

    /// <summary>As <see cref="IdentifyAsync"/>, per file with its episodes, before grouping into items.</summary>
    public static async Task<IReadOnlyList<IdentifiedFile>> IdentifyFilesAsync(
        HttpMessageHandler handler, string tmdbKey, TimeProvider tmdbTime, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "cozycouch-corpus-" + Guid.NewGuid().ToString("N"));
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

            return Read(db, folder.Id);
        }
        finally
        {
            // Pooled connections keep the database file locked on Windows.
            SqliteConnection.ClearAllPools();
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>What identification stored for one file.</summary>
    /// <param name="Episodes">The linked episodes as (season, number) of <paramref name="Title"/>.</param>
    /// <param name="EpisodeTitles">The episode titles parsed from the name.</param>
    public sealed record IdentifiedFile(
        string RelPath, MediaKind ParsedKind, string Status, TitleRow? Title,
        IReadOnlyList<(int Season, int Number)>? Episodes = null, IReadOnlyList<string>? EpisodeTitles = null);

    /// <summary>
    /// Every TMDB episode linked to more than one file, one line each. No two files may share one: parts of a
    /// film TMDB lists whole get no episode rather than all sharing the film's.
    /// </summary>
    public static IReadOnlyList<string> SharedEpisodes(IEnumerable<IdentifiedFile> files) =>
    [
        .. files
            .Where(f => f.Title is not null)
            .SelectMany(f => (f.Episodes ?? []).Select(e => (Key: (f.Title!.Id, e.Season, e.Number), File: f)))
            .GroupBy(x => x.Key, x => x.File)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.First().Title!.Name, StringComparer.Ordinal).ThenBy(g => g.Key.Season).ThenBy(g => g.Key.Number)
            .Select(g => $"{g.First().Title!.Name} {g.Key.Season}x{g.Key.Number}: " +
                         string.Join(" | ", g.Select(f => string.Join(" & ", f.EpisodeTitles ?? []) + " (" + f.RelPath + ")"))),
    ];

    private static IReadOnlyList<IdentifiedFile> Read(LibraryDatabase db, int folderId)
    {
        var files = new FileStore(db).ByFolder(folderId).Where(f => !f.Missing && f.Status != "not_media").ToList();
        var titles = new TitleStore(db);
        using var connection = db.Open();
        var links = connection
            .Query<(long FileId, long TitleId)>("SELECT file_id, title_id FROM file_titles ORDER BY confidence DESC, title_id")
            .GroupBy(l => l.FileId)
            .ToDictionary(g => g.Key, g => (int)g.First().TitleId);
        var episodes = connection
            .Query<(long FileId, int Season, int Number)>(
                "SELECT fe.file_id, e.season, e.number FROM file_episodes fe JOIN episodes e ON e.id = fe.episode_id ORDER BY e.season, e.number")
            .ToLookup(e => e.FileId, e => (e.Season, e.Number));

        return files
            .Select(f => new IdentifiedFile(
                f.RelPath, f.Parsed.Kind, f.Status, links.TryGetValue(f.Id, out var titleId) ? titles.Get(titleId) : null,
                [.. episodes[f.Id]], f.Parsed.EpisodeTitles))
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
