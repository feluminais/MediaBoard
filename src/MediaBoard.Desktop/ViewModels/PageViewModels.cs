using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Library;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>A page the sidebar opens.</summary>
public abstract class PageViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>Re-reads the library. Called on the UI thread after the library changed.</summary>
    public virtual void Refresh()
    {
    }
}

/// <summary>A page later tasks fill in; until then it only says why it is empty.</summary>
public sealed class PlaceholderPageViewModel(string title, string emptyText) : PageViewModel(title)
{
    public string EmptyText { get; } = emptyText;
}

/// <summary>
/// Home: the "Continue watching" and "Recently added" rows, or a prompt to add a folder when there is none. Nothing
/// shows until the first <see cref="Refresh"/>, which runs once the database has been opened in the background.
/// </summary>
public sealed partial class HomeViewModel(ILibrary library, Action addFolder) : PageViewModel("Home")
{
    public const int RowLimit = 20;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolders))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolders))]
    public partial bool HasNoFolders { get; private set; }

    public bool HasFolders => IsLoaded && !HasNoFolders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NothingInProgress))]
    public partial IReadOnlyList<ContinueItem> ContinueWatching { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NothingAdded))]
    public partial IReadOnlyList<TitleCard> RecentlyAdded { get; private set; } = [];

    public bool NothingInProgress => ContinueWatching.Count == 0;

    public bool NothingAdded => RecentlyAdded.Count == 0;

    [RelayCommand]
    private void AddFolder() => addFolder();

    public override void Refresh()
    {
        HasNoFolders = library.Folders().Count == 0;
        ContinueWatching = HasNoFolders ? [] : library.ContinueWatching(RowLimit);
        RecentlyAdded = HasNoFolders ? [] : library.RecentlyAdded(RowLimit);
        IsLoaded = true;
    }
}
