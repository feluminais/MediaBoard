using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>
/// The title page. For a library title: poster, description, rating, year, the Play or Resume button naming the player,
/// seasons and episodes with their watched state, and the display-only edits (rename, hide, mark watched, fix match)
/// plus the explicit "Delete from disk". For an online title (<see cref="Online"/>): the same header with a
/// "Not in library" tag and nothing to play or edit. Every library read and write runs on the thread pool; the
/// shell refreshes the page whenever the library changes.
/// </summary>
public sealed partial class TitleViewModel : PageViewModel
{
    public const string NotInLibraryTag = "Not in library";
    private const string FileMissing = "File missing";

    private readonly TitleServices _s;
    private readonly TitleNavigation _nav;
    private readonly int _titleId;
    private readonly int _tmdbId;
    private readonly TitleKind _onlineKind;
    private readonly BackgroundQuery _query;
    private readonly BackgroundQuery _previewQuery;
    private readonly BackgroundQuery _relocateQuery;
    private readonly Lazy<string?> _player;
    private string? _playerName;
    private bool _playerKnown;
    private int? _playFileId;
    private TitleKind _kind;
    private IReadOnlyList<(int FileId, bool Watched)> _files = [];
    private string? _posterPath;
    private bool _onlineLoaded;
    private bool _headerLoaded;
    private Dictionary<(TargetKind, int), EditableItem> _items = [];

    /// <summary>A page for a title in the library.</summary>
    public TitleViewModel(int titleId, TitleServices services, TitleNavigation navigation)
        : this(titleId, 0, TitleKind.Movie, online: false, services, navigation)
    {
    }

    private TitleViewModel(int titleId, int tmdbId, TitleKind kind, bool online, TitleServices services, TitleNavigation navigation)
        : base("Title")
    {
        _s = services;
        _nav = navigation;
        _titleId = titleId;
        _tmdbId = tmdbId;
        _onlineKind = kind;
        _kind = kind;
        IsOnline = online;
        _query = new BackgroundQuery(services.Hub, "the title");
        _previewQuery = new BackgroundQuery(services.Hub, "the files to delete");
        _relocateQuery = new BackgroundQuery(services.Hub, "the fixed match");
        _player = new Lazy<string?>(services.PlayerName);
        Header = new EditableItem(null, TargetKind.Title, titleId, "", false);
        PlayText = "Play";
        Rows.Add(this);
    }

    /// <summary>
    /// What the page lists, flat so the list virtualizes: this page for the header block, then seasons and episodes,
    /// then <see cref="FilesHeader"/> and the loose files. A refresh changes it row by row.
    /// </summary>
    public ObservableCollection<object> Rows { get; } = [];

    /// <summary>A page for a TMDB title that is not in the library (an online search hit).</summary>
    public static TitleViewModel Online(int tmdbId, TitleKind kind, TitleServices services, TitleNavigation navigation) =>
        new(0, tmdbId, kind, online: true, services, navigation);

    public bool IsOnline { get; }

    public bool IsLibrary => !IsOnline;

    /// <summary>The name, with its inline rename and hide.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHiddenTag))]
    public partial EditableItem Header { get; private set; }

    public bool ShowHiddenTag => IsLibrary && Header.IsHidden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions), nameof(ShowDetails))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>The title is no longer in the library (everything was deleted, or the folder left).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions), nameof(ShowDetails))]
    public partial bool NotFound { get; private set; }

    public string NotFoundText => "This title isn't in the library anymore.";

    /// <summary>"Not in library" (online), "Not identified (offline)", or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTag))]
    public partial string? Tag { get; private set; }

    public bool HasTag => Tag is not null;

    [ObservableProperty]
    public partial string? YearText { get; private set; }

    [ObservableProperty]
    public partial string? RatingText { get; private set; }

    [ObservableProperty]
    public partial string? Overview { get; private set; }

    /// <summary>The poster file in the image cache, once found.</summary>
    [ObservableProperty]
    public partial string? PosterLocalPath { get; private set; }

    /// <summary>An online title TMDB could not be asked about: "Offline", or why.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOfflineMessage), nameof(ShowDetails))]
    public partial string? OfflineMessage { get; private set; }

    public bool HasOfflineMessage => OfflineMessage is not null;

    /// <summary>The Play, edit and delete buttons: library titles only.</summary>
    public bool ShowActions => IsLibrary && IsLoaded && !NotFound;

    public bool ShowDetails => IsLoaded && !NotFound && !HasOfflineMessage;

    [ObservableProperty]
    public partial string PlayText { get; private set; }

    /// <summary>The Play button works: a file can start, or there is no player and the button opens Settings.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial bool CanPlay { get; private set; }

    /// <summary>Why nothing can play ("File missing"), shown beside the button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayReason))]
    public partial string? PlayReason { get; private set; }

    public bool HasPlayReason => PlayReason is not null;

    [ObservableProperty]
    public partial IReadOnlyList<SeasonItem> Seasons { get; private set; } = [];

    /// <summary>Files that belong to no episode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFiles))]
    public partial IReadOnlyList<EpisodeItem> Files { get; private set; } = [];

    /// <summary>A movie's only file is the movie itself; list files for shows and for several versions.</summary>
    public bool ShowFiles => Files.Count > 1 || (Files.Count == 1 && _kind == TitleKind.Show);

    public bool HasSeasons => Seasons.Count > 0;

    public string HideText => Header.HideText;

    public string WatchedText => _files.Count > 0 && _files.All(f => f.Watched) ? "Mark unwatched" : "Mark watched";

    public bool CanMarkWatched => _files.Count > 0;

    /// <summary>A short message under the buttons ("Connect to the internet to fix this match").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    public bool HasNotice => Notice is not null;

    /// <summary>The Fix match search; null while it is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicker))]
    public partial FixMatchPickerViewModel? Picker { get; private set; }

    public bool HasPicker => Picker is not null;

    /// <summary>What the last "Delete from disk" did, one line each.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeleteSummary))]
    public partial IReadOnlyList<string> DeleteSummary { get; private set; } = [];

    public bool HasDeleteSummary => DeleteSummary.Count > 0;

    // ---- Loading ----

    public override Task RefreshAsync() => IsOnline ? RefreshOnlineAsync() : RefreshLibraryAsync();

    private Task RefreshLibraryAsync() => _query.RunAsync(
        () =>
        {
            var details = _s.Library.Title(_titleId);
            var next = details is null ? null : _s.Library.NextEpisodeFileId(_titleId);
            return new Loaded(details, next, _player.Value); // the player is looked up once per page
        },
        Apply);

    private Task RefreshOnlineAsync()
    {
        if (_onlineLoaded) return Task.CompletedTask; // the library changing says nothing new about a TMDB title

        return _query.RunAsync(
            () => _s.Search.GetOnlineDetailsAsync(_tmdbId, _onlineKind, CancellationToken.None).GetAwaiter().GetResult(),
            ApplyOnline);
    }

    private sealed record Loaded(TitleDetailsView? Details, int? NextFileId, string? Player);

    private void Apply(Loaded data)
    {
        _playerName = data.Player;
        _playerKnown = true;
        IsLoaded = true;

        if (data.Details is not { } d)
        {
            NotFound = true;
            _files = [];
            _playFileId = null;
            Seasons = [];
            Files = [];
            _items = [];
            CollectionSync.Sync(Rows, [this]);
            CanPlay = false;
            PlayReason = null;
            OnPropertyChanged(nameof(WatchedText));
            OnPropertyChanged(nameof(CanMarkWatched));
            return;
        }

        NotFound = false;
        _kind = d.Kind;

        Tag = d.Offline ? "Not identified (offline)" : null;
        YearText = YearOf(d.Year, d.EndYear);
        RatingText = RatingOf(d.Rating);
        Overview = d.Overview;
        ShowPoster(d.PosterPath);

        // Items the page already has are updated in place: the list keeps their elements, so a name being typed or
        // the button the viewer has focused is still there after the refresh.
        if (_headerLoaded)
        {
            Header.Update(d.Name, d.Hidden);
            OnPropertyChanged(nameof(ShowHiddenTag));
        }
        else
        {
            Header = new EditableItem(this, TargetKind.Title, d.TitleId, d.Name, d.Hidden);
            _headerLoaded = true;
        }

        var known = _items;
        var next = new Dictionary<(TargetKind, int), EditableItem>();
        var seasons = d.Seasons.Select(season =>
        {
            var episodes = season.Episodes
                .Select(e => Reuse(known, next, (TargetKind.Episode, e.EpisodeId), i => i.Refresh(e), () => EpisodeItem.Of(this, e)))
                .ToList();
            return Reuse(known, next, (TargetKind.Season, season.Id), i => i.Refresh(season, episodes), () => new SeasonItem(this, season, episodes));
        }).ToList();
        var files = d.Files
            .Select(f => Reuse(known, next, (TargetKind.File, f.FileId), i => i.Refresh(f), () => EpisodeItem.Of(this, f)))
            .ToList();
        _items = next;
        Seasons = seasons;
        Files = files;
        OnPropertyChanged(nameof(HasSeasons));
        OnPropertyChanged(nameof(ShowFiles));
        SyncRows(seasons, files);

        var rows = seasons.SelectMany(s => s.Episodes).Concat(files).Where(i => i.FileId is not null).ToList();
        _files = rows.GroupBy(i => i.FileId!.Value).Select(g => (g.Key, g.Any(i => i.IsWatched))).ToList();
        OnPropertyChanged(nameof(HideText));
        OnPropertyChanged(nameof(WatchedText));
        OnPropertyChanged(nameof(CanMarkWatched));

        ApplyPlay(d, data.NextFileId, rows);
    }

    private void ApplyPlay(TitleDetailsView d, int? next, List<EpisodeItem> rows)
    {
        var item = next is null ? null : rows.FirstOrDefault(i => i.FileId == next);
        _playFileId = next is not null && item?.Missing != true ? next : null;

        var code = item?.Code is { } c ? c + " " : "";
        var resume = item is { IsWatched: false, PositionS: > 0 };
        PlayText = _playerName is not { } player ? "Choose a player"
            : resume ? $"Resume {code}in {player}, {TimeText.Format(item!.PositionS)}"
            : $"Play {code}in {player}";
        PlayReason = _playFileId is not null ? null
            : next is null && !d.Missing ? "Nothing to play"
            : FileMissing;
        CanPlay = _playerName is null || _playFileId is not null;
    }

    private void ApplyOnline(TitleDetails? details)
    {
        IsLoaded = true;
        Tag = NotInLibraryTag;
        if (details is null)
        {
            OfflineMessage = _s.Hub.Current.KeyState == KeyState.Missing ? "Add a TMDB key in Settings" : "Offline";
            return;
        }

        _onlineLoaded = true;
        _kind = details.Kind;
        OfflineMessage = null;
        Header = new EditableItem(null, TargetKind.Title, 0, details.Name, false);
        YearText = YearOf(details.Year, details.EndYear);
        RatingText = RatingOf(details.Rating);
        Overview = details.Overview;
        ShowPoster(details.PosterPath);
        ShowGraph(details);
    }

    // A show not in the library gets the episode graph under its header; the seasons load in the background.
    private void ShowGraph(TitleDetails details)
    {
        if (details.Kind != TitleKind.Show || details.SeasonNumbers.All(n => n <= 0)) return;

        Graph = new EpisodeGraphViewModel(_tmdbId, details.SeasonNumbers, _s.Search, _s.Posters.LoadStillAsync);
        CollectionSync.Sync(Rows, [this, Graph]);
        _ = Graph.LoadAsync();
    }

    /// <summary>The episode graph of an online show; null for a movie, a library title, or while TMDB has not answered.</summary>
    public EpisodeGraphViewModel? Graph { get; private set; }

    public override void Closed() => Graph?.Stop();

    public override void Opened()
    {
        if (Graph is { IsComplete: false } graph) _ = graph.LoadAsync();
    }

    private static string? YearOf(int? year, int? endYear) =>
        year is not { } y ? null
        : endYear is { } end && end != y ? string.Create(CultureInfo.InvariantCulture, $"{y}–{end}")
        : y.ToString(CultureInfo.InvariantCulture);

    private static string? RatingOf(double? rating) =>
        rating is { } r && r > 0 ? "★ " + r.ToString("0.0", CultureInfo.InvariantCulture) : null;

    private void ShowPoster(string? path)
    {
        if (path == _posterPath) return;

        _posterPath = path;
        if (path is null) PosterLocalPath = null;
        else _ = LoadPosterAsync(path);
    }

    private async Task LoadPosterAsync(string path)
    {
        var local = await _s.Posters.LoadAsync(path, CancellationToken.None); // resumes on the UI thread
        if (path == _posterPath) PosterLocalPath = local;
    }

    // The item the last refresh made for this season, episode or file, brought up to date; a new one when there was none.
    private static T Reuse<T>(
        Dictionary<(TargetKind, int), EditableItem> known, Dictionary<(TargetKind, int), EditableItem> next,
        (TargetKind, int) key, Action<T> refresh, Func<T> create)
        where T : EditableItem
    {
        var item = known.GetValueOrDefault(key) as T;
        if (item is not null) refresh(item);
        else item = create();
        next[key] = item;
        return item;
    }

    // One flat list for the page, so the list control builds only the rows in view: the page itself (its header
    // block), then each season with its episodes, then the files that belong to no episode.
    private void SyncRows(IReadOnlyList<SeasonItem> seasons, IReadOnlyList<EpisodeItem> files)
    {
        var rows = new List<object> { this };
        foreach (var season in seasons)
        {
            rows.Add(season);
            rows.AddRange(season.Episodes);
        }

        if (ShowFiles)
        {
            rows.Add(FilesHeader.Instance);
            rows.AddRange(files);
        }

        CollectionSync.Sync(Rows, rows);
    }

    // ---- Edits ----

    internal Task RenameAsync(TargetKind kind, int id, string? name) => EditAsync("rename", () => _s.Library.Rename(kind, id, name));

    internal Task SetHiddenAsync(TargetKind kind, int id, bool hidden) =>
        EditAsync(hidden ? "hide" : "show", () => _s.Library.SetHidden(kind, id, hidden));

    internal Task SetWatchedAsync(int fileId, bool watched) =>
        EditAsync("change the watched state", () => _s.Library.SetWatched(fileId, watched));

    // The library raises Changed after an edit and the shell refreshes the page; refreshing here as well keeps the page
    // right without the shell, at the price of one more read.
    private async Task EditAsync(string what, Action edit)
    {
        Notice = null;
        try
        {
            await Task.Run(edit);
        }
        catch (Exception e)
        {
            _s.Hub.Log(ActivityLevel.Error, $"Couldn't {what}: {e.Message}");
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private Task ToggleHiddenAsync() => SetHiddenAsync(TargetKind.Title, _titleId, !Header.IsHidden);

    /// <summary>Marks every file of the title watched, or unwatched when they all are.</summary>
    [RelayCommand]
    private Task ToggleWatchedAsync()
    {
        var files = _files;
        var target = !(files.Count > 0 && files.All(f => f.Watched));
        return EditAsync("change the watched state", () =>
        {
            foreach (var (fileId, watched) in files)
            {
                if (watched != target) _s.Library.SetWatched(fileId, target);
            }
        });
    }

    // ---- Playing ----

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        if (_playerKnown && _playerName is null)
        {
            _nav.OpenSettings();
            return;
        }

        if (_playFileId is { } file) await _s.Launcher.PlayAsync(file);
    }

    internal Task PlayFileAsync(int fileId)
    {
        if (_playerKnown && _playerName is null)
        {
            _nav.OpenSettings();
            return Task.CompletedTask;
        }

        return _s.Launcher.PlayAsync(fileId);
    }

    [RelayCommand]
    private void Back() => _nav.Back();

    // ---- Fix match ----

    [RelayCommand]
    private void FixMatch()
    {
        Notice = null;
        if (ConnectionProblem() is { } problem)
        {
            Notice = problem;
            return;
        }

        if (_files.Count == 0) return;

        Picker = new FixMatchPickerViewModel(_s.Search, _s.Hub, Header.Name, _kind, PickMatchAsync, () => Picker = null);
    }

    private string? ConnectionProblem() => MatchProblems.Of(_s.Hub.Current);

    // The library fixes one file at a time, so every file of the title is moved, one after the other. The first
    // failure stops the run; what already moved stays moved, and the page says how many did.
    private async Task PickMatchAsync(SearchCandidate choice)
    {
        Picker = null;
        var fileIds = _files.Select(f => f.FileId).ToList();
        if (fileIds.Count == 0) return;

        var moved = 0;
        string? problem = null;
        try
        {
            await Task.Run(async () =>
            {
                foreach (var fileId in fileIds)
                {
                    await _s.Library.FixMatchAsync(fileId, choice.TmdbId, choice.Kind, CancellationToken.None);
                    moved++;
                }
            });
        }
        catch (MetadataUnavailableException e)
        {
            problem = MatchProblems.Of(e.Reason);
        }
        catch (Exception e)
        {
            _s.Hub.Log(ActivityLevel.Error, $"Couldn't fix the match: {e.Message}");
            problem = $"Couldn't fix the match: {e.Message}";
        }

        if (problem is not null)
        {
            Notice = moved == 0
                ? problem
                : $"{problem.TrimEnd('.')}. Moved {moved} of {Plural.Of(fileIds.Count, "file")}.";
            if (moved == 0) return;
        }

        _ = IdentifyAsync();
        await ShowFixedMatchAsync(choice);
    }

    // The fix stores the match and requeues the folder's other files; the new matches show up as identification
    // finishes and the library announces it.
    private async Task IdentifyAsync()
    {
        try
        {
            await _s.IdentifyPending();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _s.Hub.Log(ActivityLevel.Warning, $"Couldn't identify the rest: {e.Message}");
        }
    }

    // The file moved to the title TMDB's pick stands for. This page is still there when other files stay on it;
    // otherwise show the new title, or step back when it can't be found.
    private async Task ShowFixedMatchAsync(SearchCandidate choice)
    {
        int? target = null;
        var stays = true;
        (bool Stays, int? Target) Find()
        {
            if (_s.Library.Title(_titleId) is not null) return (true, null);

            foreach (var card in _s.Library.SearchLocal(choice.Name, 10))
            {
                if (_s.Library.Title(card.TitleId) is { } t && t.TmdbId == choice.TmdbId && t.Kind == choice.Kind)
                    return (false, card.TitleId);
            }

            return (false, null);
        }

        await _relocateQuery.RunAsync(Find, found => (stays, target) = found);

        if (stays) await RefreshAsync();
        else if (target is { } id) _nav.Replace(id);
        else _nav.Back();
    }

    // ---- Not a movie or show ----

    /// <summary>Takes the title out of the library for good: every file is marked "not media" and is not identified again.</summary>
    [RelayCommand]
    private async Task NotMediaAsync()
    {
        var fileIds = _files.Select(f => f.FileId).ToList();
        if (fileIds.Count == 0) return;

        if (!await _s.ConfirmNotMedia(Header.Name)) return;

        try
        {
            await Task.Run(() =>
            {
                foreach (var fileId in fileIds) _s.Library.MarkNotMedia(fileId);
            });
        }
        catch (Exception e)
        {
            _s.Hub.Log(ActivityLevel.Error, $"Couldn't mark it as not a movie or show: {e.Message}");
            Notice = $"Couldn't mark it as not a movie or show: {e.Message}";
            return;
        }

        _nav.Back(); // the title has no files left
    }

    // ---- Delete from disk ----

    [RelayCommand]
    private async Task DeleteAsync()
    {
        DeleteSummary = [];
        DeletePreview? preview = null;
        await _previewQuery.RunAsync(() => _s.Library.PreviewDelete(_titleId), p => preview = p);
        if (preview is null) return;

        if (preview.Files.Count == 0)
        {
            DeleteSummary = ["No files on disk to delete."];
            return;
        }

        if (!await _s.ConfirmDelete(preview)) return;

        DeleteResult result;
        try
        {
            result = await _s.Library.DeleteFromDiskAsync(preview);
        }
        catch (Exception e)
        {
            _s.Hub.Log(ActivityLevel.Error, $"Couldn't delete the files: {e.Message}");
            DeleteSummary = [$"Couldn't delete the files: {e.Message}"];
            return;
        }

        var summary = new List<string>();
        if (result.Deleted.Count > 0)
        {
            summary.Add($"Deleted {Plural.Of(result.Deleted.Count, "file")}.");
            _s.Hub.Log(ActivityLevel.Info, $"Deleted {Plural.Of(result.Deleted.Count, "file")} of {preview.TitleName}.");
        }

        foreach (var failure in result.Failed)
        {
            var line = $"Kept {Path.GetFileName(failure.Path)}: {failure.Reason}";
            summary.Add(line);
            _s.Hub.Log(ActivityLevel.Warning, line);
        }

        DeleteSummary = summary;
        await RefreshAsync();
        if (NotFound) _nav.Back();
    }

    [RelayCommand]
    private void DismissSummary() => DeleteSummary = [];
}
