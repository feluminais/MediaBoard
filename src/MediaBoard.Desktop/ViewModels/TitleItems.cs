using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Library;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>
/// Something on the title page the viewer can rename or hide: the title itself, a season, an episode or a file.
/// A page refresh builds new items, so the page copies an open edit over to the new item.
/// </summary>
public partial class EditableItem : ObservableObject
{
    private readonly TitleViewModel? _page;

    public EditableItem(TitleViewModel? page, TargetKind kind, int id, string name, bool isHidden)
    {
        _page = page;
        Kind = kind;
        Id = id;
        Name = name;
        IsHidden = isHidden;
    }

    public TargetKind Kind { get; }

    /// <summary>The id the library edits by (<see cref="SeasonView.Id"/> for a season).</summary>
    public int Id { get; }

    public string Name { get; }

    public bool IsHidden { get; }

    public string HideText => IsHidden ? "Show" : "Hide";

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditText { get; set; } = "";

    [RelayCommand]
    private void BeginRename()
    {
        EditText = Name;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelRename() => IsEditing = false;

    /// <summary>Saves the new name; an empty one clears the override, so the original name comes back.</summary>
    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        IsEditing = false;
        var text = EditText.Trim();
        if (_page is null || text == Name) return;

        await _page.RenameAsync(Kind, Id, text.Length == 0 ? null : text);
    }

    [RelayCommand]
    private Task ToggleHiddenAsync() => _page?.SetHiddenAsync(Kind, Id, !IsHidden) ?? Task.CompletedTask;

    internal void CarryEditFrom(EditableItem old)
    {
        EditText = old.EditText;
        IsEditing = old.IsEditing;
    }
}

public sealed class SeasonItem(TitleViewModel page, SeasonView season, IReadOnlyList<EpisodeItem> episodes)
    : EditableItem(page, TargetKind.Season, season.Id, season.Name, season.Hidden)
{
    public int Number { get; } = season.Number;

    public IReadOnlyList<EpisodeItem> Episodes { get; } = episodes;
}

/// <summary>An episode row, or a file the library could not place in an episode.</summary>
public sealed partial class EpisodeItem : EditableItem
{
    private readonly TitleViewModel _page;

    private EpisodeItem(
        TitleViewModel page, TargetKind kind, int id, string name, bool hidden, string? label, string? code, int? fileId, bool watched,
        double positionS, double durationS, bool missing)
        : base(page, kind, id, name, hidden)
    {
        _page = page;
        Label = label;
        Code = code;
        PositionS = positionS;
        FileId = fileId;
        IsWatched = watched;
        Missing = missing;
        Progress = !watched && durationS > 0 ? Math.Clamp(positionS / durationS, 0, 1) : 0;
        StatusText = fileId is null ? "No file"
            : missing ? "File missing"
            : watched ? "Watched"
            : positionS > 0 ? TimeText.Format(positionS)
            : "";
    }

    public static EpisodeItem Of(TitleViewModel page, EpisodeRow row) =>
        new(page, TargetKind.Episode, row.EpisodeId, row.Name, row.Hidden, $"E{row.Number:00}", $"S{row.Season:00}E{row.Number:00}", row.FileId, row.Watched,
            row.PositionS, row.DurationS, row.Missing);

    public static EpisodeItem Of(TitleViewModel page, FileView file) =>
        new(page, TargetKind.File, file.FileId, file.Name, file.Hidden, null, null, file.FileId, file.Watched,
            file.PositionS, file.DurationS, file.Missing);

    /// <summary>"E04"; null for a file with no episode.</summary>
    public string? Label { get; }

    /// <summary>"S01E04", for the Play button; null for a file with no episode.</summary>
    public string? Code { get; }

    public double PositionS { get; }

    public int? FileId { get; }

    public bool IsWatched { get; }

    public bool Missing { get; }

    /// <summary>How far through the file the viewer is, 0 to 1; 0 hides the bar.</summary>
    public double Progress { get; }

    public bool HasProgress => Progress > 0;

    /// <summary>"Watched", the time left off at, "File missing" or "No file"; empty for an episode not started.</summary>
    public string StatusText { get; }

    public bool HasStatus => StatusText.Length > 0;

    public bool HasLabel => Label is not null;

    public bool CanPlay => FileId is not null && !Missing;

    public bool CanMarkWatched => FileId is not null;

    public string WatchedText => IsWatched ? "Mark unwatched" : "Mark watched";

    [RelayCommand]
    private Task ToggleWatchedAsync() => FileId is { } file ? _page.SetWatchedAsync(file, !IsWatched) : Task.CompletedTask;

    [RelayCommand]
    private Task PlayAsync() => FileId is { } file && CanPlay ? _page.PlayFileAsync(file) : Task.CompletedTask;
}

internal static class TimeText
{
    /// <summary>"23:10", or "1:02:10" from an hour on.</summary>
    public static string Format(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
    }
}
