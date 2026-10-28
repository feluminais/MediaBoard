using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>One TMDB title the library guessed for a file. The rating and poster arrive later, from TMDB, when it answers.</summary>
public sealed partial class ReviewCandidateViewModel : ObservableObject
{
    private readonly ReviewViewModel _page;
    private readonly ReviewRowViewModel _row;

    internal ReviewCandidateViewModel(ReviewCandidate candidate, ReviewRowViewModel row, ReviewViewModel page)
    {
        Candidate = candidate;
        _row = row;
        _page = page;
        Detail = MatchChoice.DetailOf(candidate.Year, candidate.Kind, null);
    }

    public ReviewCandidate Candidate { get; }

    public string Name => Candidate.Name;

    internal (int TmdbId, TitleKind Kind) Key => (Candidate.TmdbId, Candidate.Kind);

    /// <summary>"1995 · Movie · 8.3": year and kind from the library, the rating once TMDB has told.</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; }

    /// <summary>The cached poster file, once found. Null shows the placeholder.</summary>
    [ObservableProperty]
    public partial string? PosterLocalPath { get; private set; }

    internal string? PosterPath { get; private set; }

    /// <summary>TMDB's answer has been applied.</summary>
    internal bool HasInfo { get; private set; }

    internal void Apply(OnlineInfo info)
    {
        HasInfo = true;
        Detail = MatchChoice.DetailOf(Candidate.Year, Candidate.Kind, info.Rating);
        PosterPath = info.PosterPath;
    }

    internal void ShowPoster(string? localPath) => PosterLocalPath = localPath;

    [RelayCommand]
    private Task PickAsync() => _page.PickAsync(_row, Candidate.TmdbId, Candidate.Kind);
}

/// <summary>What TMDB said about a candidate.</summary>
internal sealed record OnlineInfo(double? Rating, string? PosterPath);

/// <summary>One file waiting for a decision: where it is, what the name says, its best guesses, and the search box.</summary>
public sealed partial class ReviewRowViewModel : ObservableObject
{
    public const int CandidateLimit = 3;

    private readonly ReviewViewModel _page;

    internal ReviewRowViewModel(ReviewItem item, ReviewViewModel page)
    {
        Item = item;
        _page = page;
        Candidates = item.Candidates.Take(CandidateLimit).Select(c => new ReviewCandidateViewModel(c, this, page)).ToList();
        Summary = SummaryOf(item);
    }

    internal ReviewItem Item { get; }

    public int FileId => Item.FileId;

    /// <summary>The path inside the library folder.</summary>
    public string Path => Item.RelPath;

    /// <summary>"Mystery S01E02", or "Heat (1995)": what the file name says it is.</summary>
    public string Summary { get; }

    public IReadOnlyList<ReviewCandidateViewModel> Candidates { get; }

    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>The search box for this file; null while it is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicker))]
    public partial FixMatchPickerViewModel? Picker { get; internal set; }

    public bool HasPicker => Picker is not null;

    /// <summary>A short message under the file ("Connect to the internet to fix this match").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; internal set; }

    public bool HasNotice => Notice is not null;

    /// <summary>Whether a refreshed list can keep this row (and its open search) for the new record.</summary>
    internal bool Matches(ReviewItem item) =>
        Item.FileId == item.FileId && Item.RelPath == item.RelPath && Item.ParsedTitle == item.ParsedTitle
        && Item.Candidates.Take(CandidateLimit).SequenceEqual(item.Candidates.Take(CandidateLimit));

    [RelayCommand]
    private void Search() => _page.OpenSearch(this);

    [RelayCommand]
    private Task NotMediaAsync() => _page.NotMediaAsync(this);

    private static string SummaryOf(ReviewItem item)
    {
        if (item.Kind == MediaKind.Episode && EpisodeLabel(item) is { } label) return $"{item.ParsedTitle} {label}";

        return item.Year is { } year ? $"{item.ParsedTitle} ({year.ToString(CultureInfo.InvariantCulture)})" : item.ParsedTitle;
    }

    // "S01E02", "S01E02-E03", "S01" or "E05": whatever the file name gave.
    private static string? EpisodeLabel(ReviewItem item)
    {
        static string Two(int number) => number.ToString("00", CultureInfo.InvariantCulture);

        var season = item.Season is { } s ? "S" + Two(s) : "";
        var episodes = item.Episodes.Count switch
        {
            0 => "",
            1 => "E" + Two(item.Episodes[0]),
            _ => $"E{Two(item.Episodes[0])}-E{Two(item.Episodes[^1])}",
        };
        var label = season + episodes;
        return label.Length == 0 ? null : label;
    }
}

/// <summary>The files of one show folder (one decision for all of them), or the files that sit on their own.</summary>
public sealed class ReviewGroup : ObservableObject
{
    internal ReviewGroup(string? header)
    {
        Header = header;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CountText));
    }

    /// <summary>The show folder; null for files that belong to no show folder.</summary>
    public string? Header { get; }

    public bool HasHeader => Header is not null;

    /// <summary>Kept across refreshes: a refresh adds and removes rows, it does not rebuild them.</summary>
    public ObservableCollection<ReviewRowViewModel> Rows { get; } = [];

    public string CountText => Plural.Of(Rows.Count, "file");
}

/// <summary>
/// Needs review: the files the app could not identify with confidence, each with its best TMDB guesses, a search box and
/// "Not a movie or show". Episodes of one show folder are listed together, because one pick covers them all: the
/// library stores a rule for the folder and puts its other waiting files back in the identification queue.
/// Every library read goes through a <see cref="BackgroundQuery"/>.
/// </summary>
public sealed partial class ReviewViewModel : PageViewModel
{
    /// <summary>How many of the first rows get a rating and poster from TMDB.</summary>
    public const int EnrichRowLimit = 30;

    private readonly TitleServices _s;
    private readonly BackgroundQuery _query;
    private readonly BackgroundQuery _details;
    private readonly Dictionary<(int TmdbId, TitleKind Kind), OnlineInfo> _online = [];
    private readonly HashSet<(int TmdbId, TitleKind Kind)> _unanswered = [];
    private readonly Dictionary<string, ReviewGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
    private ReviewGroup? _loose;
    private IReadOnlyDictionary<int, ReviewRowViewModel> _rows = new Dictionary<int, ReviewRowViewModel>();
    private CancellationTokenSource _lifetime = new();
    private bool _enriching;

    public ReviewViewModel(TitleServices services)
        : base("Needs review")
    {
        _s = services;
        _query = new BackgroundQuery(services.Hub, "Needs review");
        _details = new BackgroundQuery(services.Hub, "TMDB details");
    }

    public string EmptyText => "Nothing to review";

    /// <summary>The groups in order, each with its rows. The objects stay the same across refreshes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial IReadOnlyList<ReviewGroup> Groups { get; private set; } = [];

    /// <summary>What the list shows, flat so it can be virtualized: a <see cref="ReviewGroup"/> for each header, then its rows.</summary>
    public ObservableCollection<object> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoaded { get; private set; }

    public bool ShowEmpty => IsLoaded && Groups.Count == 0;

    /// <summary>What the last pick set in motion ("Saved for Mystery. Identifying 19 more files."); null when there is nothing to add.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    public partial string? Hint { get; private set; }

    public bool HasHint => Hint is not null;

    /// <summary>The identification run the last pick started; it ends with the queue.</summary>
    public Task Identification { get; private set; } = Task.CompletedTask;

    /// <summary>The run that looks up ratings and posters of the listed candidates; it ends when none is left to ask about.</summary>
    public Task Enrichment { get; private set; } = Task.CompletedTask;

    // The page is costly to load on a big library and the sidebar badge counts on its own, so it loads when opened.
    public override bool RefreshesWhileHidden => false;

    public override void Opened()
    {
        if (_lifetime.IsCancellationRequested) _lifetime = new CancellationTokenSource();
        _unanswered.Clear(); // reopening is the retry for titles TMDB could not answer
        _ = RefreshAsync();
    }

    public override void Closed() => _lifetime.Cancel();

    public override Task RefreshAsync() =>
        _query.RunAsync(_s.Library.NeedsReview, items =>
        {
            Apply(items);
            IsLoaded = true;
        });

    private void Apply(IReadOnlyList<ReviewItem> items)
    {
        var rows = new Dictionary<int, ReviewRowViewModel>();
        var wanted = new Dictionary<ReviewGroup, List<ReviewRowViewModel>>();
        var order = new List<ReviewGroup>();
        foreach (var item in items)
        {
            _rows.TryGetValue(item.FileId, out var old);
            var row = old is not null && old.Matches(item) ? old : new ReviewRowViewModel(item, this);
            if (old is not null && !ReferenceEquals(old, row))
            {
                row.Picker = old.Picker;
                row.Notice = old.Notice;
            }

            rows[item.FileId] = row;
            var group = GroupFor(item.ShowFolder);
            if (!wanted.TryGetValue(group, out var list))
            {
                wanted[group] = list = [];
                order.Add(group);
            }

            list.Add(row);
        }

        _rows = rows;

        // Everything below keeps the objects it can: a removal takes one row out of the collections, and the list
        // control only drops that one item instead of rebuilding.
        order = order.Where(g => g.HasHeader).Concat(order.Where(g => !g.HasHeader)).ToList(); // files without a show folder last
        foreach (var group in _groups.Values.Concat(_loose is null ? [] : [_loose]).ToList())
        {
            if (wanted.ContainsKey(group)) continue;

            group.Rows.Clear();
            if (group.HasHeader) _groups.Remove(group.Header!);
            else _loose = null;
        }

        foreach (var (group, list) in wanted) Sync(group.Rows, list);
        if (!Groups.SequenceEqual(order, ReferenceEqualityComparer.Instance)) Groups = order;

        var flat = new List<object>();
        foreach (var group in order)
        {
            if (group.HasHeader) flat.Add(group);
            flat.AddRange(group.Rows);
        }

        Sync(Items, flat);
        StartEnrichment();
    }

    private ReviewGroup GroupFor(string? showFolder)
    {
        if (showFolder is null) return _loose ??= new ReviewGroup(null);
        if (!_groups.TryGetValue(showFolder, out var group)) _groups[showFolder] = group = new ReviewGroup(showFolder);
        return group;
    }

    // Makes <target> hold the same items as <wanted>, in order, with the fewest removals, inserts and moves.
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted)
        where T : class
    {
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;

            var at = -1;
            for (var j = i + 1; j < target.Count && at < 0; j++)
            {
                if (ReferenceEquals(target[j], wanted[i])) at = j;
            }

            if (at >= 0) target.Move(at, i);
            else target.Insert(i, wanted[i]);
        }
    }

    // ---- Search ----

    internal void OpenSearch(ReviewRowViewModel row)
    {
        Hint = null;
        row.Notice = null;
        if (MatchProblems.Of(_s.Hub.Current) is { } problem)
        {
            row.Notice = problem;
            return;
        }

        foreach (var other in _rows.Values) other.Picker = null; // one search box at a time
        var kind = row.Item.Kind == MediaKind.Movie ? TitleKind.Movie : TitleKind.Show;
        row.Picker = new FixMatchPickerViewModel(
            _s.Search, _s.Hub, row.Item.ParsedTitle, kind, choice => PickAsync(row, choice.TmdbId, choice.Kind), () => row.Picker = null);
    }

    // ---- Pick ----

    // The library stores the match and, for an episode in a show folder, a rule that puts the folder's other waiting
    // files back in the queue; identification then applies the rule. So the siblings leave the list as soon as the
    // fix is stored, and the hint counts them.
    internal async Task PickAsync(ReviewRowViewModel row, int tmdbId, TitleKind kind)
    {
        Hint = null;
        row.Notice = null;
        row.Picker = null;
        var before = _rows.Count;
        var folder = row.Item.ShowFolder;

        try
        {
            await Task.Run(() => _s.Library.FixMatchAsync(row.FileId, tmdbId, kind, CancellationToken.None));
        }
        catch (MetadataUnavailableException e)
        {
            row.Notice = MatchProblems.Of(e.Reason);
            return;
        }
        catch (Exception e)
        {
            _s.Hub.Log(ActivityLevel.Error, $"Couldn't fix the match: {e.Message}");
            row.Notice = $"Couldn't fix the match: {e.Message}";
            return;
        }

        Identification = IdentifyAsync();
        await RefreshAsync();

        var followed = before - _rows.Count - 1;
        Hint = folder is not null && followed > 0 ? $"Saved for {folder}. Identifying {Plural.Of(followed, "more file")}." : null;
    }

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

    // ---- Not a movie or show ----

    internal async Task NotMediaAsync(ReviewRowViewModel row)
    {
        Hint = null;
        row.Notice = null;
        if (!await _s.ConfirmNotMedia(row.Item.FileName)) return;

        try
        {
            await Task.Run(() => _s.Library.MarkNotMedia(row.FileId));
        }
        catch (Exception e)
        {
            _s.Hub.Log(ActivityLevel.Error, $"Couldn't mark it as not a movie or show: {e.Message}");
            row.Notice = $"Couldn't mark it as not a movie or show: {e.Message}";
            return;
        }

        await RefreshAsync();
    }

    // ---- Ratings and posters ----

    // The library stores only name, year and kind of a guess. TMDB gives the rating and poster, one title at a time
    // (the client paces its requests). To keep the volume small: only the first rows are looked up, a title several
    // files guessed is asked about once, nothing is asked while TMDB can't answer (offline, no key, rejected key), the
    // run stops at the first sign of that, a title that failed is not asked again until the page is reopened, and
    // leaving the page stops the run.
    private void StartEnrichment()
    {
        foreach (var candidate in Candidates())
        {
            if (!candidate.HasInfo && _online.TryGetValue(candidate.Key, out var info))
            {
                candidate.Apply(info);
                _ = ShowPosterAsync(candidate);
            }
        }

        if (_enriching || _lifetime.IsCancellationRequested || MatchProblems.Of(_s.Hub.Current) is not null) return;

        _enriching = true;
        Enrichment = EnrichAsync(_lifetime.Token);
    }

    private IEnumerable<ReviewCandidateViewModel> Candidates() =>
        Items.OfType<ReviewRowViewModel>().Take(EnrichRowLimit).SelectMany(r => r.Candidates);

    private (int TmdbId, TitleKind Kind)? NextUnasked()
    {
        foreach (var candidate in Candidates())
        {
            if (!_online.ContainsKey(candidate.Key) && !_unanswered.Contains(candidate.Key)) return candidate.Key;
        }

        return null;
    }

    private async Task EnrichAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && NextUnasked() is { } key)
            {
                OnlineInfo? found = null;
                await _details.RunAsync(
                    () => _s.Search.GetOnlineDetailsAsync(key.TmdbId, key.Kind, ct).GetAwaiter().GetResult(),
                    details => found = details is null ? null : new OnlineInfo(details.Rating, details.PosterPath));
                if (ct.IsCancellationRequested) break;

                if (found is null)
                {
                    if (MatchProblems.Of(_s.Hub.Current) is not null) break; // TMDB can't answer right now: not this title's fault
                    _unanswered.Add(key);
                    continue;
                }

                _online[key] = found;
                foreach (var candidate in Candidates().Where(c => c.Key == key))
                {
                    candidate.Apply(found);
                    _ = ShowPosterAsync(candidate);
                }
            }
        }
        finally
        {
            _enriching = false;
        }
    }

    private async Task ShowPosterAsync(ReviewCandidateViewModel candidate)
    {
        if (candidate.PosterPath is not { } path) return;

        var local = await _s.Posters.LoadAsync(path, CancellationToken.None); // resumes on the UI thread
        if (candidate.PosterPath == path) candidate.ShowPoster(local);
    }
}
