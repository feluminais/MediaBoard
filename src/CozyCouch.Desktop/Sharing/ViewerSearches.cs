using CozyCouch.Core.Library;
using CozyCouch.Core.Metadata;

namespace CozyCouch.Desktop.Sharing;

/// <summary>
/// One search service per viewer, kept between requests for its search index and season cache. Each request builds a new
/// <see cref="Viewer"/>, so one is known by its id and folders, and a change of folders gets a new service.
/// </summary>
public sealed class ViewerSearches(Func<Viewer, ISearchService> build)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, (Viewer Viewer, ISearchService Search)> _kept = [];

    public ISearchService For(Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        lock (_gate)
        {
            if (_kept.TryGetValue(viewer.UserId, out var kept) && SameFolders(kept.Viewer, viewer)) return kept.Search;

            var search = build(viewer);
            _kept[viewer.UserId] = (viewer, search);
            return search;
        }
    }

    /// <summary>Drops what's kept for an account that can't sign in any more. It won't make another request to replace it.</summary>
    public void Forget(int userId)
    {
        lock (_gate) _kept.Remove(userId);
    }

    public void Clear()
    {
        lock (_gate) _kept.Clear();
    }

    private static bool SameFolders(Viewer a, Viewer b) =>
        a.AllowedFolderIds is null || b.AllowedFolderIds is null
            ? a.AllowedFolderIds is null && b.AllowedFolderIds is null
            : a.AllowedFolderIds.SetEquals(b.AllowedFolderIds);
}
