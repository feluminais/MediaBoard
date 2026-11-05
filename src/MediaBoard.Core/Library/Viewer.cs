using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Library;

/// <summary>Whose library a query answers for: their own progress, and only the folders they may reach.</summary>
/// <param name="AllowedFolderIds">The folders the viewer may reach; null means all of them.</param>
/// <param name="OverNetwork">Asked for through the sharing server, not at this PC. Holds for the owner's own devices too.</param>
public sealed record Viewer(int UserId, IReadOnlySet<int>? AllowedFolderIds, bool OverNetwork = false)
{
    /// <summary>The person at this PC. Everything the app itself does is done as the owner.</summary>
    public static Viewer Owner { get; } = new(LibraryDatabase.OwnerId, null);

    public bool IsOwner => UserId == LibraryDatabase.OwnerId;

    /// <summary>
    /// Only the owner at this PC sees what they hid and the folders they switched off. Over the network a hide at any level
    /// makes the item unreachable, whoever is asking: hidden items don't leave this PC.
    /// </summary>
    public bool SeesHidden => IsOwner && !OverNetwork;

    public bool CanReach(int folderId) => AllowedFolderIds is null || AllowedFolderIds.Contains(folderId);
}
