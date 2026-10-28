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

    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    public static FolderRow Folder(int id, string path) => new(id, path, true, false, false, 0, []);

    public static ReviewItem ReviewFile(int fileId, string name) =>
        new(fileId, name, name, name, null, MediaKind.Movie, null, [], false, []);

    public IReadOnlyList<ContinueItem> ContinueWatching(int limit) => [];

    public IReadOnlyList<TitleCard> RecentlyAdded(int limit) => Recent.Take(limit).ToList();

    public IReadOnlyList<TitleCard> Titles(TitleKind kind, TitleSort sort, bool includeHidden) => [];

    public TitleDetailsView? Title(int titleId) => null;

    public IReadOnlyList<ReviewItem> NeedsReview() => Review.ToList();

    public IReadOnlyList<FolderRow> Folders() => FolderRows.ToList();

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
