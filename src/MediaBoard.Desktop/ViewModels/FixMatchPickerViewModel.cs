using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;

namespace MediaBoard.Desktop.ViewModels;

public enum MatchFilter { All, Movies, Shows }

/// <summary>One TMDB title the viewer can pick.</summary>
public sealed class MatchChoice(SearchCandidate candidate)
{
    public SearchCandidate Candidate { get; } = candidate;

    public string Name => Candidate.Name;

    /// <summary>"2019 · Movie · 8.1": year, kind and rating, whichever TMDB knows.</summary>
    public string Detail { get; } = DetailOf(candidate.Year, candidate.Kind, candidate.Rating);

    /// <summary>The same line for any title: the picker's results and the candidates on the Needs review page.</summary>
    public static string DetailOf(int? year, TitleKind kind, double? rating) => string.Join(" · ", new[]
    {
        year?.ToString(CultureInfo.InvariantCulture),
        kind == TitleKind.Movie ? "Movie" : "Show",
        rating is { } stars ? stars.ToString("0.0", CultureInfo.InvariantCulture) : null,
    }.Where(part => part is not null));
}

/// <summary>
/// The "Fix match" search: a box, a kind filter and the TMDB hits (online results only). It searches a short while
/// after the last keystroke, through a <see cref="BackgroundQuery"/>. Picking a result hands it to the page; the
/// kind filter only narrows the hits already found.
/// </summary>
public sealed partial class FixMatchPickerViewModel : ObservableObject
{
    public const int TypingPauseMs = 300;

    private readonly ISearchService _search;
    private readonly Func<SearchCandidate, Task> _pick;
    private readonly Action _cancel;
    private readonly BackgroundQuery _query;
    private IReadOnlyList<SearchCandidate> _found = [];
    private OnlineState _state = OnlineState.TooShort;
    private bool _searched;
    private CancellationTokenSource? _pause;
    private CancellationTokenSource? _searchCancel;

    public FixMatchPickerViewModel(
        ISearchService search, ActivityHub hub, string query, TitleKind kind, Func<SearchCandidate, Task> pick, Action cancel)
    {
        _search = search;
        _pick = pick;
        _cancel = cancel;
        _query = new BackgroundQuery(hub, "TMDB results");
        Query = query;
        Filter = kind == TitleKind.Movie ? MatchFilter.Movies : MatchFilter.Shows;
        _ = SearchAsync();
    }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial MatchFilter Filter { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<MatchChoice> Results { get; private set; } = [];

    /// <summary>Why there is nothing to pick ("No matches", "Offline", ...); null while there are results or before the first search.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    public IReadOnlyList<MatchFilter> Filters { get; } = [MatchFilter.All, MatchFilter.Movies, MatchFilter.Shows];

    public static string FilterText(MatchFilter filter) => filter switch
    {
        MatchFilter.Movies => "Movies",
        MatchFilter.Shows => "Shows",
        _ => "All",
    };

    partial void OnQueryChanged(string value)
    {
        _pause?.Cancel();
        _pause = new CancellationTokenSource();
        _ = SearchAfterPauseAsync(_pause.Token);
    }

    partial void OnFilterChanged(MatchFilter value) => Show();

    private async Task SearchAfterPauseAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TypingPauseMs, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SearchAsync();
    }

    /// <summary>Searches TMDB for the text now. The task ends once the results are shown (or dropped for a newer search).</summary>
    public Task SearchAsync()
    {
        _pause?.Cancel();
        _searchCancel?.Cancel(); // a newer search makes the one in flight pointless: stop its request
        var cancel = _searchCancel = new CancellationTokenSource();
        var text = Query;
        return _query.RunAsync(
            () => _search.SearchAsync(text, cancel.Token).GetAwaiter().GetResult(),
            results =>
            {
                _found = results.Online.Select(o => o.Candidate).ToList();
                _state = results.State;
                _searched = true;
                Show();
            });
    }

    private void Show()
    {
        if (!_searched) return;

        var shown = _found.Where(c => Filter switch
        {
            MatchFilter.Movies => c.Kind == TitleKind.Movie,
            MatchFilter.Shows => c.Kind == TitleKind.Show,
            _ => true,
        }).Select(c => new MatchChoice(c)).ToList();
        Results = shown;
        Message = shown.Count > 0 ? null : _state switch
        {
            OnlineState.Ok => "No matches",
            OnlineState.Offline => "Connect to the internet to search",
            OnlineState.NoKey => "Add a TMDB key in Settings",
            OnlineState.KeyRejected => "TMDB rejected the key. Check it in Settings.",
            _ => "Type to search",
        };
    }

    [RelayCommand]
    private Task PickAsync(MatchChoice? choice) => choice is null ? Task.CompletedTask : _pick(choice.Candidate);

    [RelayCommand]
    private void Cancel()
    {
        _pause?.Cancel();
        _searchCancel?.Cancel();
        _query.Discard(); // the cancelled request is not a failure to report
        _cancel();
    }
}
