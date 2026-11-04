using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Library;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>
/// One poster card: a title in a grid or row, or a Continue watching entry. The poster is found only when the card is
/// shown (<see cref="RequestPoster"/>) and the request is dropped when the card goes away (<see cref="CancelPoster"/>).
/// </summary>
public sealed partial class PosterCardViewModel : ObservableObject
{
    private readonly PosterLoader _loader;
    private readonly Action<PosterCardViewModel> _open;
    private readonly Action<PosterCardViewModel>? _remove;
    private CancellationTokenSource? _request;

    private PosterCardViewModel(
        object source, string key, int titleId, int? fileId, string name, string? subtitle, string? detail,
        string? posterPath, double progress, bool isDimmed, PosterLoader loader, Action<PosterCardViewModel> open,
        Action<PosterCardViewModel>? remove = null)
    {
        _remove = remove;
        Source = source;
        Key = key;
        TitleId = titleId;
        FileId = fileId;
        Name = name;
        Subtitle = subtitle;
        Detail = detail;
        PosterPath = posterPath;
        Progress = progress;
        IsDimmed = isDimmed;
        _loader = loader;
        _open = open;
    }

    /// <summary>The library record this card shows; two refreshes that read equal records can keep the same card.</summary>
    public object Source { get; }

    /// <summary>Identifies the card across refreshes.</summary>
    public string Key { get; }

    public int TitleId { get; }

    /// <summary>The file a Continue watching card plays; null for a title card.</summary>
    public int? FileId { get; }

    /// <summary>A click starts a file rather than opening the title page.</summary>
    public bool CanPlay => FileId is not null;

    public string Name { get; }

    public string? Subtitle { get; }

    /// <summary>A second muted line in list rows ("Added 3 Mar 2026").</summary>
    public string? Detail { get; }

    public string? PosterPath { get; }

    /// <summary>How far through the file the viewer is, 0 to 1; 0 hides the bar.</summary>
    public double Progress { get; }

    /// <summary>Hidden or missing: drawn faded.</summary>
    public bool IsDimmed { get; }

    /// <summary>A poster lookup is queued or running for this card.</summary>
    public bool IsLookingForPoster => _request is not null;

    public bool HasProgress => Progress > 0;

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>The cached image file, once found. Null shows the placeholder.</summary>
    [ObservableProperty]
    public partial string? PosterLocalPath { get; private set; }

    /// <summary>A Continue watching card can be taken off the list.</summary>
    public bool CanRemove => _remove is not null;

    [RelayCommand]
    private void Open() => _open(this);

    /// <summary>Takes the title off Continue watching; its progress stays.</summary>
    [RelayCommand]
    private void Remove() => _remove?.Invoke(this);

    public static PosterCardViewModel For(TitleCard card, PosterLoader loader, Action<PosterCardViewModel> open, bool withDate = false) =>
        new(card, $"t{card.TitleId}", card.TitleId, null, card.Name, TitleSubtitle(card),
            withDate ? "Added " + card.DateAdded.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : null,
            card.PosterPath, 0, card.Hidden || card.Missing, loader, open);

    public static PosterCardViewModel For(
        ContinueItem item, PosterLoader loader, Action<PosterCardViewModel> open, Action<PosterCardViewModel>? remove = null) =>
        new(item, $"f{item.FileId}", item.TitleId, item.FileId, item.TitleName, ContinueSubtitle(item), null,
            item.PosterPath, item.IsNext ? 0 : StartedProgress(item), item.Missing, loader, open, remove);

    /// <summary>The least a started file's bar shows, so a file opened for a moment (or with no known length) still looks started.</summary>
    internal const double MinStartedProgress = 0.08;

    private static double StartedProgress(ContinueItem item)
    {
        var ratio = item.DurationS > 0 ? item.PositionS / item.DurationS : 0;
        return Math.Clamp(ratio, MinStartedProgress, 1);
    }

    /// <summary>
    /// Starts finding the poster unless it is found, being found, or there is none to find. Call when the card is
    /// shown. Safe to call again: a card that got no answer (offline) tries again the next time it is shown.
    /// </summary>
    public void RequestPoster()
    {
        if (PosterPath is null || PosterLocalPath is not null || _request is not null) return;

        var request = _request = new CancellationTokenSource();
        _ = ResolveAsync(PosterPath, request);
    }

    /// <summary>The card went away (scrolled off, or the page closed): stop looking.</summary>
    public void CancelPoster()
    {
        _request?.Cancel();
        _request = null;
    }

    /// <summary>Takes over a poster already found for the same picture, so a rebuilt card doesn't flash the placeholder.</summary>
    internal void Inherit(PosterCardViewModel previous)
    {
        if (previous.PosterLocalPath is not null && previous.PosterPath == PosterPath) PosterLocalPath = previous.PosterLocalPath;
    }

    private async Task ResolveAsync(string posterPath, CancellationTokenSource request)
    {
        string? local = null;
        try
        {
            local = await _loader.LoadAsync(posterPath, request.Token); // resumes on the UI thread
        }
        catch (OperationCanceledException)
        {
        }

        if (!request.IsCancellationRequested) PosterLocalPath = local;
        if (ReferenceEquals(_request, request)) _request = null;
        request.Dispose();
    }

    private static string? TitleSubtitle(TitleCard card)
    {
        if (card.Offline) return "Not identified (offline)";
        if (card.Year is not { } year) return null;
        return card.EndYear is { } end && end != year ? $"{year}–{end}" : year.ToString(CultureInfo.InvariantCulture);
    }

    private static string? ContinueSubtitle(ContinueItem item)
    {
        var episode = item.EpisodeLabel is null ? item.EpisodeName : item.EpisodeName is null ? item.EpisodeLabel : $"{item.EpisodeLabel} · {item.EpisodeName}";
        return item.IsNext && episode is not null ? "Next: " + episode : episode;
    }
}

/// <summary>Builds the cards of a list, keeping the previous card for every record that didn't change.</summary>
internal static class PosterCards
{
    public static List<PosterCardViewModel> Build<T>(
        IReadOnlyList<T> records, IReadOnlyDictionary<string, PosterCardViewModel> previous, Func<T, PosterCardViewModel> create)
    {
        var cards = new List<PosterCardViewModel>(records.Count);
        foreach (var record in records)
        {
            var card = create(record);
            if (previous.TryGetValue(card.Key, out var old))
            {
                if (old.Source.Equals(card.Source)) card = old;
                else card.Inherit(old);
            }

            cards.Add(card);
        }

        return cards;
    }

    /// <summary>The same cards in the same order.</summary>
    public static bool SameCards(IReadOnlyList<PosterCardViewModel> a, IReadOnlyList<PosterCardViewModel> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i])) return false;
        }

        return true;
    }

    public static Dictionary<string, PosterCardViewModel> Index(IEnumerable<PosterCardViewModel> cards)
    {
        var index = new Dictionary<string, PosterCardViewModel>();
        foreach (var card in cards) index[card.Key] = card;
        return index;
    }
}
