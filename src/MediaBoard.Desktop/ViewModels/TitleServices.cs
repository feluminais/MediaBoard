using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>What a title page needs from the rest of the app. Every dialog and every call into the shell is a seam, so a test needs no window.</summary>
/// <param name="PlayerName">The player the Play button names, or null when none is installed or chosen. Slow and it logs: a page asks once.</param>
/// <param name="ConfirmDelete">Shows the files that would go to the Recycle Bin; true when the viewer confirmed.</param>
/// <param name="ConfirmNotMedia">Asks whether to mark a title (named) as not a movie or show; true when the viewer confirmed.</param>
/// <param name="IdentifyPending">Starts identification of what is pending (after a fix). Returns at once; the task ends with the queue.</param>
public sealed record TitleServices(
    ILibrary Library,
    ISearchService Search,
    ActivityHub Hub,
    PosterLoader Posters,
    PlaybackLauncher Launcher,
    Func<string?> PlayerName,
    Func<DeletePreview, Task<bool>> ConfirmDelete,
    Func<string, Task<bool>> ConfirmNotMedia,
    Func<Task> IdentifyPending)
{
    /// <summary>What this run of the app can do, which decides the edits a page offers.</summary>
    public AppCapabilities Capabilities { get; init; } = AppCapabilities.Local;
}

/// <summary>What a title page can ask the shell to do. All run on the UI thread.</summary>
/// <param name="Back">Returns to the page that opened this one.</param>
/// <param name="OpenSettings">Opens Settings (the Play button does when there is no player).</param>
/// <param name="Replace">Shows another library title in place of this page (a fixed match moved the file to it).</param>
public sealed record TitleNavigation(Action Back, Action OpenSettings, Action<int> Replace);
