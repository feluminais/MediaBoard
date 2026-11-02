using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>One episode on the graph: its place, its TMDB rating (null while nobody voted) and what the hover card shows.</summary>
public sealed record GraphEpisode(
    int Season,
    int Number,
    string Name,
    double? Rating,
    int VoteCount,
    string? Overview,
    string? StillPath)
{
    /// <summary>The rating as drawn on the cell and used for its colour: one decimal, "?" while unrated.</summary>
    public string RatingText => Rating is { } r ? Rounded(r).ToString("0.0", CultureInfo.InvariantCulture) : "?";

    public double? RoundedRating => Rating is { } r ? Rounded(r) : null;

    private static double Rounded(double rating) => Math.Round(rating, 1, MidpointRounding.AwayFromZero);

    public string Subtitle => VoteCount > 0
        ? $"Season {Season} Episode {Number} · {VoteCount.ToString("N0", CultureInfo.InvariantCulture)} {(VoteCount == 1 ? "vote" : "votes")}"
        : $"Season {Season} Episode {Number}";
}

public sealed record GraphSeason(int Number, IReadOnlyList<GraphEpisode> Episodes);

/// <summary>
/// The episode graph of an online show: every season's episodes as rating-coloured cells, filled in as the seasons
/// arrive from TMDB, a season at a time (the request queue paces them, so a 30-season show takes a couple of seconds
/// and the page never waits for it). A season that did not arrive is listed in <see cref="Status"/> and
/// <see cref="LoadCommand"/> asks for only those again.
/// </summary>
public sealed partial class EpisodeGraphViewModel : ObservableObject
{
    private readonly int _tmdbId;
    private readonly IReadOnlyList<int> _numbers;
    private readonly ISearchService _search;
    private readonly Dictionary<int, GraphSeason> _loaded = [];
    private CancellationTokenSource? _run;
    private bool _running;

    /// <param name="seasonNumbers">The show's seasons; season 0 (the specials) is not part of the graph.</param>
    /// <param name="loadStill">Turns a still's TMDB path into a local file (the poster loader); null when there is none.</param>
    public EpisodeGraphViewModel(
        int tmdbId, IEnumerable<int> seasonNumbers, ISearchService search, Func<string, CancellationToken, Task<string?>> loadStill)
    {
        _tmdbId = tmdbId;
        _numbers = seasonNumbers.Where(n => n > 0).Distinct().Order().ToList();
        _search = search;
        Card = new EpisodeCardViewModel(loadStill);
    }

    /// <summary>The seasons that arrived, in season order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GraphSeason> Seasons { get; private set; } = [];

    /// <summary>The card for the episode under the pointer; its <see cref="EpisodeCardViewModel.Episode"/> is null when none is.</summary>
    public EpisodeCardViewModel Card { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(ShowRetry))]
    public partial string? Status { get; private set; }

    public bool HasStatus => Status is not null;

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

                _loaded[number] = ToGraphSeason(number, details);
                Seasons = _numbers.Where(_loaded.ContainsKey).Select(n => _loaded[n]).Where(s => s.Episodes.Count > 0).ToList();
            }

            Status = failed == 0 ? null : failed == 1 ? "1 season didn't load" : $"{failed} seasons didn't load";
            CanRetry = failed > 0;
            HasNoEpisodes = failed == 0 && Seasons.Count == 0;
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

    private static GraphSeason ToGraphSeason(int number, SeasonDetails details) =>
        new(number, details.Episodes
            .Where(e => e.Number > 0)
            .OrderBy(e => e.Number)
            .Select(e => new GraphEpisode(number, e.Number, e.Name, e.Rating, e.VoteCount, e.Overview, e.StillPath))
            .ToList());
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
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(Title), nameof(Subtitle), nameof(Overview), nameof(HasOverview), nameof(RatingText))]
    public partial GraphEpisode? Episode { get; private set; }

    [ObservableProperty]
    public partial string? StillLocalPath { get; private set; }

    public bool IsVisible => Episode is not null;

    public string Title => Episode?.Name ?? "";

    public string Subtitle => Episode?.Subtitle ?? "";

    public string RatingText => Episode?.RatingText ?? "";

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
