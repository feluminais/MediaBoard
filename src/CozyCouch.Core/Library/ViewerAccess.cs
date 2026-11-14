using CozyCouch.Core.Storage;

namespace CozyCouch.Core.Library;

/// <param name="FullPath">Server side only. Never serialised.</param>
public sealed record ReachableFile(
    int FileId, int TitleId, string FullPath, string FileName, string TitleName,
    string? EpisodeLabel, double? DurationS, bool Missing)
{
    /// <summary>The title with its episode, as people read it.</summary>
    public string Title => EpisodeLabel is { } label ? $"{TitleName} {label}" : TitleName;
}

/// <summary>
/// Decides what a viewer may reach by id. It reads the same <see cref="LibrarySnapshot"/> the library queries do,
/// so a file the lists leave out is unreachable here too: a folder the viewer has no access to or that is switched
/// off, a hide at any level (title, season, episode, file, folder), a file marked "not a movie or show", a file a link
/// brings in from outside its folder. Callers answer an unreachable id exactly as they answer one that doesn't exist.
/// </summary>
public sealed class ViewerAccess(LibraryDatabase db, LocalLibrary? owner = null)
{
    public ReachableFile? File(Viewer viewer, int fileId)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        var snapshot = LibrarySnapshot.Load(db, viewer, owner?.SharedBase);
        if (!snapshot.FilesById.TryGetValue(fileId, out var file)) return null;
        if (file.TitleId is not { } titleId || !snapshot.Titles.TryGetValue(titleId, out var title)) return null;

        var folders = snapshot.Folders.ToDictionary(f => f.Id, f => f.Path);
        if (LocalLibrary.InsideLibrary(folders, file.FolderId, file.RelPath) is not { } fullPath) return null;

        // The lists may be a minute behind the disk, and this is the check that stands between a viewer and the bytes.
        if (!viewer.SeesHidden && new FolderLinks().IsThroughLink(folders[file.FolderId], file.RelPath)) return null;

        var episodes = file.Episodes.OrderBy(e => e.Key).ToList();
        double? duration = file.Progress is { DurationS: > 0 } progress ? progress.DurationS : null;
        return new ReachableFile(
            file.Id, titleId, fullPath, Path.GetFileName(file.RelPath), snapshot.TitleName(title),
            LocalLibrary.EpisodeLabel(episodes), duration, file.Missing);
    }

    public bool CanReachTitle(Viewer viewer, int titleId)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return LibrarySnapshot.Load(db, viewer, owner?.SharedBase).Titles.ContainsKey(titleId);
    }
}
