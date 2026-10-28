using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;

namespace MediaBoard.Desktop.Tests.Support;

/// <summary>An in-memory <see cref="ILibrary"/>: tests set the lists and raise <see cref="Changed"/> themselves.</summary>
internal sealed class FakeLibrary : ILibrary
{
    public List<FolderRow> FolderRows { get; } = [];

    public List<ReviewItem> Review { get; } = [];

    public List<TitleCard> Recent { get; } = [];

    public List<ContinueItem> Continue { get; } = [];

    /// <summary>The titles of both kinds; <see cref="Titles"/> filters and sorts them the way the real library does.</summary>
    public List<TitleCard> TitleCards { get; } = [];

    /// <summary>The limit of each ContinueWatching and RecentlyAdded call, in order.</summary>
    public List<(string Query, int Limit)> Limits { get; } = [];

    public List<(TitleKind Kind, TitleSort Sort, bool IncludeHidden)> TitleQueries { get; } = [];

    /// <summary>Runs on the querying thread at the start of each read query, with its name: a test can block or throw.</summary>
    public Action<string>? OnQuery { get; set; }

    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    public static FolderRow Folder(int id, string path) => new(id, path, true, false, false, 0, []);

    public static TitleCard Card(int id, string name, TitleKind kind = TitleKind.Movie, int? year = 2000, string? poster = null,
        DateTime? added = null, bool hidden = false, bool offline = false) =>
        new(id, kind, name, year, null, poster, null, offline, hidden, false, added ?? new DateTime(2026, 1, 1).AddMinutes(id));

    public static ContinueItem Resume(int titleId, string name, string? episode = null, string? poster = null, double position = 600, double duration = 6000) =>
        new(titleId, titleId * 10, TitleKind.Movie, name, episode, null, poster, position, duration, false, false, new DateTime(2026, 2, 1).AddMinutes(titleId));

    public static ReviewItem ReviewFile(int fileId, string name) =>
        new(fileId, name, name, name, null, MediaKind.Movie, null, [], false, []);

    public IReadOnlyList<ContinueItem> ContinueWatching(int limit)
    {
        OnQuery?.Invoke(nameof(ContinueWatching));
        lock (Limits) Limits.Add((nameof(ContinueWatching), limit));
        return Continue.OrderByDescending(i => i.LastPlayed).Take(limit).ToList();
    }

    public IReadOnlyList<TitleCard> RecentlyAdded(int limit)
    {
        OnQuery?.Invoke(nameof(RecentlyAdded));
        lock (Limits) Limits.Add((nameof(RecentlyAdded), limit));
        return Recent.OrderByDescending(c => c.DateAdded).Take(limit).ToList();
    }

    public IReadOnlyList<TitleCard> Titles(TitleKind kind, TitleSort sort, bool includeHidden)
    {
        OnQuery?.Invoke(nameof(Titles));
        lock (TitleQueries) TitleQueries.Add((kind, sort, includeHidden));
        var cards = TitleCards.Where(c => c.Kind == kind && (includeHidden || !c.Hidden));
        return (sort switch
        {
            TitleSort.Year => cards.OrderByDescending(c => c.Year ?? int.MinValue).ThenBy(c => c.Name, StringComparer.Ordinal),
            TitleSort.DateAdded => cards.OrderByDescending(c => c.DateAdded).ThenBy(c => c.Name, StringComparer.Ordinal),
            _ => cards.OrderBy(c => c.Name, StringComparer.Ordinal),
        }).ToList();
    }

    /// <summary>The title pages, by title id. <see cref="Title"/> answers from here.</summary>
    public Dictionary<int, TitleDetailsView> Details { get; } = [];

    /// <summary>What <see cref="NextEpisodeFileId"/> answers, by title id.</summary>
    public Dictionary<int, int?> Next { get; } = [];

    /// <summary>Every edit the page made, as "rename:Episode:7:Pilot", "hidden:Title:1:True", "watched:12:True", "fix:12:603:Movie", "delete".</summary>
    public List<string> Edits { get; } = [];

    public DeletePreview? Preview { get; set; }

    public DeleteResult DeleteResult { get; set; } = new([], []);

    /// <summary>Runs inside <see cref="FixMatchAsync"/> before it records the call: a test can throw.</summary>
    public Action? OnFixMatch { get; set; }

    public TitleDetailsView? Title(int titleId)
    {
        OnQuery?.Invoke(nameof(Title));
        return Details.GetValueOrDefault(titleId);
    }

    public static TitleDetailsView Movie(int id, string name, double position = 0, bool watched = false, bool missing = false, int fileId = 0) =>
        new(id, TitleKind.Movie, name, 100 + id, 1999, null, "About " + name, 7.4, "/p.jpg", false, false, missing, [],
            [new FileView(fileId == 0 ? id * 10 : fileId, name + ".mkv", name + ".mkv", false, watched, position, 6000, missing)]);

    public static EpisodeRow Episode(int id, int season, int number, string name, int? fileId, bool watched = false, double position = 0,
        bool missing = false, bool hidden = false) =>
        new(id, season, number, name, null, 45, hidden, fileId, watched, position, position > 0 ? 2700 : 0, missing);

    public static TitleDetailsView Show(int id, string name, params SeasonView[] seasons) =>
        new(id, TitleKind.Show, name, 100 + id, 2015, 2022, "About " + name, 8.4, "/p.jpg", false, false,
            seasons.SelectMany(s => s.Episodes).All(e => e.FileId is null || e.Missing), seasons, []);

    public static SeasonView Season(int titleId, int number, params EpisodeRow[] episodes) =>
        new(LibraryIds.Season(titleId, number), number, number == 0 ? "Specials" : $"Season {number}", false, episodes);

    private void Edit(string log, Func<TitleDetailsView, TitleDetailsView> change)
    {
        lock (Edits) Edits.Add(log);
        foreach (var (id, details) in Details.ToList()) Details[id] = change(details);
    }

    public IReadOnlyList<ReviewItem> NeedsReview() => Review.ToList();

    // The count is read before the hook runs, so a blocked query returns what was true when it started.
    public int NeedsReviewCount()
    {
        var count = Review.Count;
        OnQuery?.Invoke(nameof(NeedsReviewCount));
        return count;
    }

    public IReadOnlyList<FolderRow> Folders()
    {
        OnQuery?.Invoke(nameof(Folders));
        return FolderRows.ToList();
    }

    public Func<string, IReadOnlyList<TitleCard>> LocalSearch { get; set; } = _ => [];

    public IReadOnlyList<TitleCard> SearchLocal(string query, int limit) => LocalSearch(query);

    public int? NextEpisodeFileId(int titleId) => Next.GetValueOrDefault(titleId);

    public void Rename(TargetKind kind, int id, string? displayName) => Edit($"rename:{kind}:{id}:{displayName}", d => kind switch
    {
        TargetKind.Title => d.TitleId == id ? d with { Name = displayName ?? "Original" } : d,
        TargetKind.Season => d with { Seasons = d.Seasons.Select(s => s.Id == id ? s with { Name = displayName ?? $"Season {s.Number}" } : s).ToList() },
        TargetKind.Episode => d with
        {
            Seasons = d.Seasons.Select(s => s with { Episodes = s.Episodes.Select(e => e.EpisodeId == id ? e with { Name = displayName ?? "Original" } : e).ToList() }).ToList(),
        },
        _ => d,
    });

    public void SetHidden(TargetKind kind, int id, bool hidden)
    {
        Edit($"hidden:{kind}:{id}:{hidden}", d => kind switch
        {
            TargetKind.Title => d.TitleId == id ? d with { Hidden = hidden } : d,
            TargetKind.Season => d with { Seasons = d.Seasons.Select(s => s.Id == id ? s with { Hidden = hidden } : s).ToList() },
            TargetKind.Episode => d with
            {
                Seasons = d.Seasons.Select(s => s with { Episodes = s.Episodes.Select(e => e.EpisodeId == id ? e with { Hidden = hidden } : e).ToList() }).ToList(),
            },
            _ => d,
        });
        if (kind == TargetKind.Title)
        {
            var index = TitleCards.FindIndex(c => c.TitleId == id);
            if (index >= 0) TitleCards[index] = TitleCards[index] with { Hidden = hidden };
        }
    }

    public void SetWatched(int fileId, bool watched) => Edit($"watched:{fileId}:{watched}", d => d with
    {
        Seasons = d.Seasons.Select(s => s with
        {
            Episodes = s.Episodes.Select(e => e.FileId == fileId ? e with { Watched = watched, PositionS = watched ? e.PositionS : 0 } : e).ToList(),
        }).ToList(),
        Files = d.Files.Select(f => f.FileId == fileId ? f with { Watched = watched, PositionS = watched ? f.PositionS : 0 } : f).ToList(),
    });

    public Task FixMatchAsync(int fileId, int tmdbId, TitleKind kind, CancellationToken ct)
    {
        OnFixMatch?.Invoke();
        lock (Edits) Edits.Add($"fix:{fileId}:{tmdbId}:{kind}");
        return Task.CompletedTask;
    }

    public void MarkNotMedia(int fileId) => throw new NotSupportedException();

    public DeletePreview PreviewDelete(int titleId) => Preview ?? new DeletePreview(titleId, "", []);

    public Task<DeleteResult> DeleteFromDiskAsync(DeletePreview preview)
    {
        lock (Edits) Edits.Add("delete");
        return Task.FromResult(DeleteResult);
    }
}
