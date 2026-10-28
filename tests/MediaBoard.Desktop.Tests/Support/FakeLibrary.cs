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

    public TitleDetailsView? Title(int titleId) => null;

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

    public IReadOnlyList<TitleCard> SearchLocal(string query, int limit) => [];

    public int? NextEpisodeFileId(int titleId) => null;

    public void Rename(TargetKind kind, int id, string? displayName) => throw new NotSupportedException();

    public void SetHidden(TargetKind kind, int id, bool hidden) => throw new NotSupportedException();

    public void SetWatched(int fileId, bool watched) => throw new NotSupportedException();

    public Task FixMatchAsync(int fileId, int tmdbId, TitleKind kind, CancellationToken ct) => throw new NotSupportedException();

    public void MarkNotMedia(int fileId) => throw new NotSupportedException();

    public DeletePreview PreviewDelete(int titleId) => throw new NotSupportedException();

    public Task<DeleteResult> DeleteFromDiskAsync(DeletePreview preview) => throw new NotSupportedException();
}
