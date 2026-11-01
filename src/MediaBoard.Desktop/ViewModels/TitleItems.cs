using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Library;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>
/// Something on the title page the viewer can rename or hide: the title itself, a season, an episode or a file.
/// A page refresh updates the items it already has (see <c>Refresh</c> on each), so the row a viewer is typing in or
/// has keyboard focus on is the same row after the refresh.
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

    [ObservableProperty]
    public partial string Name { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HideText))]
    public partial bool IsHidden { get; private set; }

    public string HideText => IsHidden ? "Show" : "Hide";

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditText { get; set; } = "";

    /// <summary>What the library says now. The text being typed in an open edit is not touched.</summary>
    internal void Update(string name, bool hidden)
    {
        Name = name;
        IsHidden = hidden;
    }

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
}

public sealed partial class SeasonItem : EditableItem
{
    public SeasonItem(TitleViewModel page, SeasonView season, IReadOnlyList<EpisodeItem> episodes)
        : base(page, TargetKind.Season, season.Id, season.Name, season.Hidden)
    {
        Number = season.Number;
        Episodes = episodes;
    }

    public int Number { get; private set; }

    public IReadOnlyList<EpisodeItem> Episodes { get; private set; }

    internal void Refresh(SeasonView season, IReadOnlyList<EpisodeItem> episodes)
    {
        Update(season.Name, season.Hidden);
        Number = season.Number;
        Episodes = episodes;
    }
}

/// <summary>The "Other files" line above the files that belong to no episode.</summary>
public sealed class FilesHeader
{
    public static FilesHeader Instance { get; } = new();

    private FilesHeader()
    {
    }
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
        Set(label, code, fileId, watched, positionS, durationS, missing);
    }

    public static EpisodeItem Of(TitleViewModel page, EpisodeRow row) =>
        new(page, TargetKind.Episode, row.EpisodeId, row.Name, row.Hidden, $"E{row.Number:00}", $"S{row.Season:00}E{row.Number:00}", row.FileId, row.Watched,
            row.PositionS, row.DurationS, row.Missing);

    public static EpisodeItem Of(TitleViewModel page, FileView file) =>
        new(page, TargetKind.File, file.FileId, file.Name, file.Hidden, null, null, file.FileId, file.Watched,
            file.PositionS, file.DurationS, file.Missing);

    /// <summary>Takes what the library says now; the row stays the same object, so the list control keeps its elements.</summary>
    internal void Refresh(EpisodeRow row)
    {
        Update(row.Name, row.Hidden);
        Set($"E{row.Number:00}", $"S{row.Season:00}E{row.Number:00}", row.FileId, row.Watched, row.PositionS, row.DurationS, row.Missing);
        RaiseAll();
    }

    internal void Refresh(FileView file)
    {
        Update(file.Name, file.Hidden);
        Set(null, null, file.FileId, file.Watched, file.PositionS, file.DurationS, file.Missing);
        RaiseAll();
    }

    private void Set(string? label, string? code, int? fileId, bool watched, double positionS, double durationS, bool missing)
    {
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

    private void RaiseAll()
    {
        foreach (var name in Derived) OnPropertyChanged(name);
    }

    private static readonly string[] Derived =
    [
        nameof(Label), nameof(Code), nameof(PositionS), nameof(FileId), nameof(IsWatched), nameof(Missing), nameof(Progress),
        nameof(HasProgress), nameof(StatusText), nameof(HasStatus), nameof(HasLabel), nameof(CanPlay), nameof(CanMarkWatched), nameof(WatchedText),
    ];

    /// <summary>"E04"; null for a file with no episode.</summary>
    public string? Label { get; private set; }

    /// <summary>"S01E04", for the Play button; null for a file with no episode.</summary>
    public string? Code { get; private set; }

    public double PositionS { get; private set; }

    public int? FileId { get; private set; }

    public bool IsWatched { get; private set; }

    public bool Missing { get; private set; }

    /// <summary>How far through the file the viewer is, 0 to 1; 0 hides the bar.</summary>
    public double Progress { get; private set; }

    public bool HasProgress => Progress > 0;

    /// <summary>"Watched", the time left off at, "File missing" or "No file"; empty for an episode not started.</summary>
    public string StatusText { get; private set; } = "";

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
