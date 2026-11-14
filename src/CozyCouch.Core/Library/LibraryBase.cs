using Dapper;
using CozyCouch.Core.Metadata;
using CozyCouch.Core.Storage;

namespace CozyCouch.Core.Library;

/// <summary>
/// What a snapshot holds that no viewer's own data changes: the folders, files with their episodes and hides, titles
/// and overrides. Read-only once built. Progress and the dismissed list are the viewer's and never come from here.
/// </summary>
internal sealed class LibraryBase
{
    private HashSet<int>? _behindLinks;

    private LibraryBase(
        IReadOnlyList<LibraryFolder> folders, List<FileEntry> files, Dictionary<int, TitleRow> titles,
        Dictionary<(TargetKind, int), OverrideRow> overrides)
    {
        Folders = folders;
        Files = files;
        Titles = titles;
        Overrides = overrides;
    }

    public IReadOnlyList<LibraryFolder> Folders { get; }

    public List<FileEntry> Files { get; }

    public Dictionary<int, TitleRow> Titles { get; }

    public Dictionary<(TargetKind, int), OverrideRow> Overrides { get; }

    /// <summary>
    /// The files a link brings into their folder. Worked out once per base and only when asked, because it reads the disk,
    /// and the owner at this PC never needs it.
    /// </summary>
    public HashSet<int> BehindLinks(FolderLinks links) => LazyInitializer.EnsureInitialized(ref _behindLinks, () =>
    {
        var folders = Folders.ToDictionary(f => f.Id, f => f.Path);
        return Files.Where(f => folders.TryGetValue(f.FolderId, out var folder) && links.IsThroughLink(folder, f.RelPath))
            .Select(f => f.Id).ToHashSet();
    });

    /// <summary>Which read of <see cref="SharedLibraryBase"/> made this, or 0 for one made outside it. A cache keeps the number, not the base.</summary>
    public long Generation { get; set; }

    public static LibraryBase Read(LibraryDatabase db)
    {
        using var connection = db.Open();
        var folders = connection.Query<FolderRowDto>("SELECT id, path, enabled FROM folders ORDER BY id")
            .Select(r => new LibraryFolder((int)r.Id, r.Path, r.Enabled != 0)).ToList();
        var overrides = new OverrideStore(db).All().ToDictionary(o => (o.Kind, o.TargetId));

        var files = connection.Query<FileDto>(
                """
                SELECT f.id, f.folder_id AS FolderId, f.rel_path AS RelPath, f.size, f.first_seen AS FirstSeen,
                       f.missing, f.status, ft.title_id AS TitleId
                FROM files f LEFT JOIN file_titles ft ON ft.file_id = f.id
                WHERE f.status <> 'not_media'
                """)
            .Select(r => new FileEntry
            {
                Id = (int)r.Id,
                FolderId = (int)r.FolderId,
                RelPath = r.RelPath,
                Size = r.Size,
                Status = r.Status,
                FirstSeen = LibrarySnapshot.ParseTime(r.FirstSeen),
                Missing = r.Missing != 0,
                TitleId = (int?)r.TitleId,
            })
            .ToList();
        var titles = connection.Query<TitleDto>(
                """
                SELECT id, tmdb_id AS TmdbId, kind, name, year, end_year AS EndYear, overview, rating, poster_path AS PosterPath
                FROM titles
                """)
            .Select(r => new TitleRow(
                (int)r.Id, (int?)r.TmdbId, r.Kind == "show" ? TitleKind.Show : TitleKind.Movie, r.Name, (int?)r.Year,
                (int?)r.EndYear, r.Overview, r.Rating, r.PosterPath, null))
            .ToDictionary(t => t.Id);

        var byId = files.ToDictionary(f => f.Id);
        foreach (var row in connection.Query<FileEpisodeDto>(
                     """
                     SELECT fe.file_id AS FileId, e.id AS EpisodeId, e.season, e.number, e.name
                     FROM file_episodes fe JOIN episodes e ON e.id = fe.episode_id
                     """))
        {
            if (byId.TryGetValue((int)row.FileId, out var file))
                file.Episodes.Add(new EpisodeRef((int)row.EpisodeId, (int)row.Season, (int)row.Number, row.Name));
        }

        var folderHidden = folders.Where(f => overrides.TryGetValue((TargetKind.Folder, f.Id), out var o) && o.Hidden)
            .Select(f => f.Id).ToHashSet();
        foreach (var file in files)
        {
            file.Hidden = folderHidden.Contains(file.FolderId)
                          || (overrides.TryGetValue((TargetKind.File, file.Id), out var o) && o.Hidden);
        }

        return new LibraryBase(folders, files, titles, overrides);
    }

    private sealed class FolderRowDto
    {
        public long Id { get; set; }
        public string Path { get; set; } = "";
        public long Enabled { get; set; }
    }

    private sealed class FileDto
    {
        public long Id { get; set; }
        public long FolderId { get; set; }
        public string RelPath { get; set; } = "";
        public long Size { get; set; }
        public string FirstSeen { get; set; } = "";
        public long Missing { get; set; }
        public string Status { get; set; } = "";
        public long? TitleId { get; set; }
    }

    private sealed class FileEpisodeDto
    {
        public long FileId { get; set; }
        public long EpisodeId { get; set; }
        public long Season { get; set; }
        public long Number { get; set; }
        public string? Name { get; set; }
    }

    private sealed class TitleDto
    {
        public long Id { get; set; }
        public long? TmdbId { get; set; }
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public long? Year { get; set; }
        public long? EndYear { get; set; }
        public string? Overview { get; set; }
        public double? Rating { get; set; }
        public string? PosterPath { get; set; }
    }
}

/// <summary>
/// A <see cref="LibraryBase"/> kept between requests, for the sharing server, where five viewers ask for the same
/// 5,000 titles. It is dropped on every change the library reports, and after <see cref="MaxAge"/> for a write that
/// reported nothing.
/// </summary>
/// <param name="read">How the base is read; tests pass their own to land a change in the middle of a read.</param>
internal sealed class SharedLibraryBase(LibraryDatabase db, TimeProvider time, Func<LibraryDatabase, LibraryBase>? read = null)
{
    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    // Longer than the base's own life: reading every directory each time would cost more than the base saves. Lists can
    // lag by this much, and ViewerAccess checks the disk again before it hands out a file.
    private static readonly TimeSpan LinksMaxAge = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Lock _build = new();
    private readonly Func<LibraryDatabase, LibraryBase> _read = read ?? LibraryBase.Read;
    private LibraryBase? _kept;
    private long _builtAt;
    private FolderLinks? _links;
    private long _linksAt;
    private int _version;
    private long _generation;

    /// <summary>Whether a base is kept and still young enough to hand out.</summary>
    public bool Holds => Kept() is not null;

    /// <summary>What was read about links on disk, shared by the bases of the next minute.</summary>
    public FolderLinks Links
    {
        get
        {
            lock (_gate)
            {
                if (_links is null || time.GetElapsedTime(_linksAt) >= LinksMaxAge)
                {
                    _links = new FolderLinks();
                    _linksAt = time.GetTimestamp();
                }

                return _links;
            }
        }
    }

    /// <summary>Drops the base, and makes a read that is running now unfit to keep.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _version++;
            _kept = null;
        }
    }

    /// <summary>Whether the base kept now is the one <paramref name="generation"/> names, for a cache made from it.</summary>
    public bool IsCurrent(long generation) => Kept() is { } kept && kept.Generation == generation;

    public LibraryBase Get()
    {
        if (Kept() is { } kept) return kept;

        // One build at a time: viewers arriving together wait for it instead of each reading the library.
        lock (_build)
        {
            if (Kept() is { } built) return built;

            int version;
            lock (_gate) version = _version;
            var fresh = _read(db);
            fresh.Generation = Interlocked.Increment(ref _generation);
            lock (_gate)
            {
                // A change that landed during the read outdates it; the next request reads again.
                if (version == _version)
                {
                    _kept = fresh;
                    _builtAt = time.GetTimestamp();
                }
            }

            return fresh;
        }
    }

    // An old base is let go here, so it doesn't sit in memory until the next change.
    private LibraryBase? Kept()
    {
        lock (_gate)
        {
            if (_kept is null) return null;
            if (time.GetElapsedTime(_builtAt) < MaxAge) return _kept;

            _kept = null;
            return null;
        }
    }
}
