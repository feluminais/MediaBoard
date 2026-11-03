using MediaBoard.Core.Metadata;

namespace MediaBoard.Core.Library;

public enum TargetKind { Title, Season, Episode, File, Folder }

public enum TitleSort { Name, Year, DateAdded }

/// <summary>
/// The one interface the UI talks to for library data. Everything it returns is display-ready: overrides are
/// applied (a display name wins over the TMDB name) and hidden items are left out unless asked for. Edits only
/// change the database; <see cref="DeleteFromDiskAsync"/> is the one call that touches files.
/// </summary>
public interface ILibrary
{
    /// <summary>In-progress files and the next episode of shows that were being watched, most recently played first.</summary>
    IReadOnlyList<ContinueItem> ContinueWatching(int limit);

    /// <summary>
    /// Takes the title off <see cref="ContinueWatching"/> without touching its progress. It returns once something
    /// of the title is played again.
    /// </summary>
    void DismissContinue(int titleId);

    /// <summary>Newest first by the date a file of the title was first seen. <c>int.MaxValue</c> returns the whole library.</summary>
    IReadOnlyList<TitleCard> RecentlyAdded(int limit);

    IReadOnlyList<TitleCard> Titles(TitleKind kind, TitleSort sort, bool includeHidden);

    /// <summary>The title page, or null when the title has no file in the library.</summary>
    TitleDetailsView? Title(int titleId);

    IReadOnlyList<ReviewItem> NeedsReview();

    /// <summary>How many items <see cref="NeedsReview"/> would list, without building them (the sidebar badge).</summary>
    int NeedsReviewCount();

    /// <summary>Library folders with the non-media folders the last scan skipped and whether each can be read.</summary>
    IReadOnlyList<FolderRow> Folders();

    /// <summary>Instant, offline search over the titles in the library.</summary>
    IReadOnlyList<TitleCard> SearchLocal(string query, int limit);

    /// <summary>The file the Play or Resume button starts for a title, or null when nothing can be played.</summary>
    int? NextEpisodeFileId(int titleId);

    /// <summary>Sets the display name; null (or blank) clears it. For a season, <paramref name="id"/> is <see cref="SeasonView.Id"/>.</summary>
    void Rename(TargetKind kind, int id, string? displayName);

    void SetHidden(TargetKind kind, int id, bool hidden);

    void SetWatched(int fileId, bool watched);

    /// <summary>
    /// Replaces the file's match with a TMDB title chosen by the user, and remembers the choice for the other
    /// episodes of the same show folder. Throws <see cref="MetadataUnavailableException"/> when TMDB cannot
    /// answer; nothing is stored then.
    /// </summary>
    Task FixMatchAsync(int fileId, int tmdbId, TitleKind kind, CancellationToken ct);

    /// <summary>The file is not a movie or show: it leaves the lists and is not identified again.</summary>
    void MarkNotMedia(int fileId);

    DeletePreview PreviewDelete(int titleId);

    /// <summary>Sends the previewed files to the Recycle Bin. Call only after the user confirmed the preview.</summary>
    Task<DeleteResult> DeleteFromDiskAsync(DeletePreview preview);

    /// <summary>Raised after every edit and after <see cref="LocalLibrary.NotifyChanged"/>, on the thread that made the change (a thread-pool thread for a delete). The UI marshals to its own thread.</summary>
    event Action? Changed;
}
