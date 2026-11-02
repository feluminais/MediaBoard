using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>What the graph colours the cells by: how far the viewer got, or what TMDB's viewers rate them.</summary>
public enum GraphMode { Progress, Ratings }

/// <summary>Where a library episode stands. <see cref="Unknown"/> is an episode of a show that is not in the library.</summary>
public enum EpisodeState { Unknown, NoFile, Missing, Unwatched, Started, Watched }

/// <summary>One episode on the graph: its place, its TMDB rating (null while nobody voted), where the viewer is with it, and what the hover card shows.</summary>
/// <param name="Progress">0 to 1, how far through the file the viewer is; only for <see cref="EpisodeState.Started"/>.</param>
public sealed record GraphEpisode(
    int Season,
    int Number,
    string Name,
    double? Rating,
    int VoteCount,
    string? Overview,
    string? StillPath,
    EpisodeState State = EpisodeState.Unknown,
    double Progress = 0,
    bool Hidden = false,
    int? RuntimeMin = null,
    string? AirDate = null,
    string? StatusText = null)
{
    /// <summary>The rating as drawn on the cell and used for its colour: one decimal, "?" while unrated.</summary>
    public string RatingText => Rating is { } r ? Rounded(r).ToString("0.0", CultureInfo.InvariantCulture) : "?";

    public double? RoundedRating => Rating is { } r ? Rounded(r) : null;

    private static double Rounded(double rating) => Math.Round(rating, 1, MidpointRounding.AwayFromZero);

    public string Subtitle => $"Season {Season} Episode {Number}";

    /// <summary>What else is known, comma-separated: where the viewer is, the air date, the length, the votes.</summary>
    public string Facts => string.Join(", ", new[]
    {
        StatusText,
        AirDate,
        RuntimeMin is { } m ? $"{m} min" : null,
        VoteCount > 0 ? $"{VoteCount.ToString("N0", CultureInfo.InvariantCulture)} {(VoteCount == 1 ? "vote" : "votes")}" : null,
    }.Where(s => !string.IsNullOrEmpty(s)));
}

public sealed record GraphSeason(int Number, IReadOnlyList<GraphEpisode> Episodes);

/// <summary>A season of a library show as the graph needs it.</summary>
public sealed record LibrarySeason(int Number, IReadOnlyList<LibraryEpisode> Episodes);

public sealed record LibraryEpisode(
    int Number, string Name, string? AirDate, int? RuntimeMin, bool Hidden, EpisodeState State, double Progress, string StatusText);

/// <summary>
/// The episode graph of a show: every season's episodes as cells. A show that is not in the library shows TMDB's ratings,
/// filled in as the seasons arrive (the request queue paces them, so a 30-season show takes a couple of seconds and the
/// page never waits for it). A library show shows where the viewer is with each episode, and takes TMDB's ratings,
/// descriptions and stills from the same seasons when it can reach them. A season that did not arrive is listed in
/// <see cref="Status"/> and <see cref="LoadCommand"/> asks for only those again.
/// </summary>
public sealed partial class EpisodeGraphViewModel : ObservableObject
{
    private readonly int _tmdbId;
    private readonly IReadOnlyList<int> _numbers;
    private readonly ISearchService _search;
    private readonly Dictionary<int, SeasonDetails> _loaded = [];
    private IReadOnlyList<LibrarySeason>? _library;
    private CancellationTokenSource? _run;
    private bool _running;

    /// <param name="seasonNumbers">The show's seasons; season 0 (the specials) is not part of the graph.</param>
    /// <param name="loadStill">Turns a still's TMDB path into a local file (the poster loader); null when there is none.</param>
    /// <param name="library">The show is in the library: the graph starts on the viewer's progress and takes ratings as a second view.</param>
    public EpisodeGraphViewModel(
        int tmdbId, IEnumerable<int> seasonNumbers, ISearchService search, Func<string, CancellationToken, Task<string?>> loadStill,
        bool library = false)
    {
        _tmdbId = tmdbId;
        _numbers = seasonNumbers.Where(n => n > 0).Distinct().Order().ToList();
        _search = search;
        IsLibrary = library;
        Mode = library ? GraphMode.Progress : GraphMode.Ratings;
        Card = new EpisodeCardViewModel(loadStill);
    }

    public bool IsLibrary { get; }

    public string Heading => IsLibrary ? "Episodes" : "Episode ratings";

    /// <summary>The seasons that arrived, in season order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GraphSeason> Seasons { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgressLegend), nameof(ShowRatingLegend), nameof(ShowStatus))]
    public partial GraphMode Mode { get; set; }

    public bool ShowProgressLegend => Mode == GraphMode.Progress;

    public bool ShowRatingLegend => Mode == GraphMode.Ratings;

    /// <summary>A library show whose ratings arrived: the viewer can switch between progress and ratings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModes))]
    public partial bool HasRatings { get; private set; }

    public bool ShowModes => IsLibrary && HasRatings;

    [RelayCommand]
    private void ShowProgress() => Mode = GraphMode.Progress;

    [RelayCommand]
    private void ShowRatings() => Mode = GraphMode.Ratings;

    /// <summary>The season whose episodes the page lists under the graph (library shows); drawn as chosen in the graph.</summary>
    [ObservableProperty]
    public partial int? SelectedSeason { get; set; }

    /// <summary>The card for the episode under the pointer; its <see cref="EpisodeCardViewModel.Episode"/> is null when none is.</summary>
    public EpisodeCardViewModel Card { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(ShowStatus), nameof(ShowRetry))]
    public partial string? Status { get; private set; }

    public bool HasStatus => Status is not null;

    /// <summary>The loading line and the failures: for a library show only while its ratings are on screen.</summary>
    public bool ShowStatus => HasStatus && (!IsLibrary || Mode == GraphMode.Ratings);

    /// <summary>Some seasons did not load and nothing is loading now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRetry))]
    public partial bool CanRetry { get; private set; }

    public bool ShowRetry => CanRetry && !IsLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRetry))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Every season arrived and none of them lists an episode.</summary>
    [ObservableProperty]
    public partial bool HasNoEpisodes { get; private set; }

    public bool IsComplete => _loaded.Count == _numbers.Count;

    /// <summary>The viewer clicked a cell (season and episode) or a season's heading (episode null).</summary>
    public event Action<int, int?>? Chosen;

    public void Choose(int season, int? episode) => Chosen?.Invoke(season, episode);

    /// <summary>Call on the UI thread. A library show hands over its seasons again whenever the library changed.</summary>
    public void SetLibrary(IReadOnlyList<LibrarySeason> seasons)
    {
        _library = seasons;
        Rebuild();
    }

    /// <summary>Loads the seasons that are not here yet. Returns when they all arrived, failed, or <see cref="Stop"/> ran.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_running || IsComplete) return;

        _running = true;
        var run = _run = new CancellationTokenSource();
        IsLoading = true;
        CanRetry = false;
        var failed = 0;
        try
        {
            foreach (var number in _numbers)
            {
                if (_loaded.ContainsKey(number)) continue;

                Status = $"Loading {_loaded.Count + 1} of {_numbers.Count}";
                var details = await _search.GetOnlineSeasonAsync(_tmdbId, number, run.Token); // resumes on the UI thread
                if (details is null)
                {
                    failed++;
                    continue;
                }

                _loaded[number] = details;
                Rebuild();
            }

            Status = failed == 0 ? null : failed == 1 ? "1 season didn't load" : $"{failed} seasons didn't load";
            CanRetry = failed > 0;
            HasNoEpisodes = !IsLibrary && failed == 0 && Seasons.Count == 0;
        }
        catch (OperationCanceledException)
        {
            Status = null; // the page left; coming back loads the rest
        }
        finally
        {
            IsLoading = false;
            _running = false;
            if (ReferenceEquals(_run, run)) _run = null;
            run.Dispose();
        }
    }

    /// <summary>The page is not on screen: stop asking TMDB. What already arrived stays.</summary>
    public void Stop() => _run?.Cancel();

    private void Rebuild()
    {
        HasRatings = _loaded.Values.Any(s => s.Episodes.Any(e => e.Rating is not null));
        if (!HasRatings && Mode == GraphMode.Ratings && IsLibrary) Mode = GraphMode.Progress;

        Seasons = _library is { } library ? FromLibrary(library) : FromOnline();
    }

    private List<GraphSeason> FromOnline() =>
        _numbers.Where(_loaded.ContainsKey)
            .Select(n => new GraphSeason(n, _loaded[n].Episodes
                .Where(e => e.Number > 0)
                .OrderBy(e => e.Number)
                .Select(e => new GraphEpisode(n, e.Number, e.Name, e.Rating, e.VoteCount, e.Overview, e.StillPath))
                .ToList()))
            .Where(s => s.Episodes.Count > 0)
            .ToList();

    private List<GraphSeason> FromLibrary(IReadOnlyList<LibrarySeason> library) =>
        library.Where(s => s.Number > 0 && s.Episodes.Count > 0)
            .OrderBy(s => s.Number)
            .Select(s =>
            {
                var online = _loaded.TryGetValue(s.Number, out var details)
                    ? details.Episodes.GroupBy(e => e.Number).ToDictionary(g => g.Key, g => g.First())
                    : null;
                return new GraphSeason(s.Number, s.Episodes
                    .OrderBy(e => e.Number)
                    .Select(e =>
                    {
                        var extra = online?.GetValueOrDefault(e.Number);
                        return new GraphEpisode(s.Number, e.Number, e.Name, extra?.Rating, extra?.VoteCount ?? 0, extra?.Overview, extra?.StillPath,
                            e.State, e.Progress, e.Hidden, e.RuntimeMin ?? extra?.RuntimeMin, e.AirDate, e.StatusText);
                    })
                    .ToList());
            })
            .ToList();
}

/// <summary>
/// The hover card: what to say about the episode under the pointer, and its still frame, which is fetched only once
/// the pointer has rested on the cell for a moment, so sweeping across a thousand cells downloads nothing.
/// </summary>
public sealed partial class EpisodeCardViewModel(Func<string, CancellationToken, Task<string?>> loadStill) : ObservableObject
{
    private static readonly TimeSpan StillDelay = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _still;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(Title), nameof(Subtitle), nameof(Facts), nameof(HasFacts), nameof(Overview), nameof(HasOverview), nameof(RatingText), nameof(HasRating))]
    public partial GraphEpisode? Episode { get; private set; }

    [ObservableProperty]
    public partial string? StillLocalPath { get; private set; }

    public bool IsVisible => Episode is not null;

    public string Title => Episode?.Name ?? "";

    public string Subtitle => Episode?.Subtitle ?? "";

    public string Facts => Episode?.Facts ?? "";

    public bool HasFacts => Facts.Length > 0;

    public string RatingText => Episode?.RatingText ?? "";

    public bool HasRating => Episode?.Rating is not null;

    public string? Overview => Episode?.Overview;

    public bool HasOverview => !string.IsNullOrWhiteSpace(Episode?.Overview);

    /// <summary>Call on the UI thread. Null hides the card.</summary>
    public void Show(GraphEpisode? episode)
    {
        if (Equals(episode, Episode)) return;

        _still?.Cancel();
        _still = null;
        StillLocalPath = null;
        Episode = episode;
        if (episode?.StillPath is { } path) _ = LoadStillAsync(path);
    }

    private async Task LoadStillAsync(string path)
    {
        var source = _still = new CancellationTokenSource();
        try
        {
            await Task.Delay(StillDelay, source.Token);
            var local = await loadStill(path, source.Token); // resumes on the UI thread
            if (!source.IsCancellationRequested) StillLocalPath = local;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
