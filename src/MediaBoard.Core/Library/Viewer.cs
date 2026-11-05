using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Library;

/// <summary>Whose library a query answers for: their own progress, and only the folders they may reach.</summary>
/// <param name="AllowedFolderIds">The folders the viewer may reach; null means all of them.</param>
public sealed record Viewer(int UserId, IReadOnlySet<int>? AllowedFolderIds)
{
    /// <summary>The person at this PC. Everything the app itself does is done as the owner.</summary>
    public static Viewer Owner { get; } = new(LibraryDatabase.OwnerId, null);

    public bool IsOwner => UserId == LibraryDatabase.OwnerId;

    public bool CanReach(int folderId) => AllowedFolderIds is null || AllowedFolderIds.Contains(folderId);
}
