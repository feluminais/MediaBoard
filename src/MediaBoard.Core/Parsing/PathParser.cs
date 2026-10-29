using System.Text.RegularExpressions;

namespace MediaBoard.Core.Parsing;

/// <summary>
/// Combines the folder names and the file name of a library file to decide movie or episode,
/// show title, season and episodes. The name itself is read by <see cref="ReleaseNameParser"/>.
/// </summary>
public static class PathParser
{
    private const int MinSiblingsForBareNumber = 2;
    private static readonly char[] Stray = [' ', '-', '–', '_', ',', '.'];

    // A folder that is only a season: "Season 01 (2015)", "S02", "Season 1-6". The second number marks a range.
    private static readonly Regex SeasonFolder = new(
        @"^(?:Seasons?|Series|S)[\s._]*(?<n>\d{1,2})(?:\s*[-–]\s*S?(?<n2>\d{1,2}))?(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A season marker after the show title: "Show - Season 1", "Show S01 BluRay".
    private static readonly Regex SeasonSuffix = new(
        @"^(?<title>.*?)[\s\-–_.]*\b(?:(?:Seasons?|Series)[\s._]*|S)(?<n>\d{1,2})(?:\s*[-–]\s*S?(?<n2>\d{1,2}))?(?![A-Za-z0-9]).*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumberPrefix = new(@"^\d{1,3}\.\s+", RegexOptions.Compiled);

    public static ParsedMedia Parse(string relativePath, IReadOnlyList<string> siblingFileNames)
    {
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return new ParsedMedia(MediaKind.Unknown, "", null, null, [], [], null, null, null);

        var fileName = segments[^1];
        var folders = segments[..^1];
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var file = ReleaseNameParser.Parse(stem);
        var fileTitle = Tidy(file.Title);
        var folderInfos = folders.Select(ParseFolder).ToArray();

        IReadOnlyList<int> episodes = file.Episodes;
        if (episodes.Count == 0 && IsBareNumberEpisode(file, fileTitle, fileName, siblingFileNames))
            episodes = [file.LeadingNumber!.Value];

        return episodes.Count > 0
            ? BuildEpisode(file, fileTitle, episodes, folders, folderInfos)
            : BuildMovie(stem, file, fileTitle, folderInfos);
    }

    private static ParsedMedia BuildEpisode(
        ParsedName file, string fileTitle, IReadOnlyList<int> episodes, string[] folders, FolderInfo[] folderInfos)
    {
        var title = fileTitle.Length > 0
            ? fileTitle
            : folderInfos.Select(f => f.Title).FirstOrDefault(t => t.Length > 0) ?? "";

        // The deepest folder is the most specific; with no marker anywhere an episode belongs to season 1.
        var season = file.Season
                     ?? folderInfos.Reverse().Select(f => f.Season).FirstOrDefault(s => s is not null)
                     ?? 1;

        // A year in front of the episode or season marker names the show ("South Park (1997)", "Frasier.2023.S02E01");
        // one after it dates the episode or season ("E01. Jared Has Aides (2002)", "Widows.Bay.S01.2026").
        // A folder's show year wins over the file's.
        var showYear = folderInfos.Select(f => f.Year).FirstOrDefault(y => y is not null)
                       ?? (file.YearAfterMarker ? null : file.Year);
        var airYear = (file.YearAfterMarker ? file.Year : null)
                      ?? folderInfos.Reverse().Select(f => f.AirYear).FirstOrDefault(y => y is not null);

        return new ParsedMedia(
            MediaKind.Episode,
            title,
            showYear,
            season,
            episodes,
            file.EpisodeTitles,
            folders.Length > 0 ? folders[0] : null,
            file.TmdbId ?? folderInfos.Select(f => f.TmdbId).FirstOrDefault(id => id is not null),
            file.ImdbId ?? folderInfos.Select(f => f.ImdbId).FirstOrDefault(id => id is not null),
            airYear);
    }

    private static ParsedMedia BuildMovie(string stem, ParsedName file, string fileTitle, FolderInfo[] folderInfos)
    {
        var title = fileTitle;
        var year = file.Year;
        var tmdbId = file.TmdbId;
        var imdbId = file.ImdbId;

        // "Toy Story 3": the parser moves a trailing number out of the title. "01. Title" keeps its prefix out.
        if (file.LeadingNumber is { } number && !NumberPrefix.IsMatch(stem.Trim()))
            title = Tidy($"{title} {number}");

        // A movie folder fills in what the file name leaves out.
        var parent = folderInfos.Length > 0 ? folderInfos[^1] : null;
        if (parent is not null && (title.Length == 0 || (year is null && SameTitle(parent.Title, title))))
        {
            if (title.Length == 0)
                title = parent.Title;
            year ??= parent.Year;
            tmdbId ??= parent.TmdbId;
            imdbId ??= parent.ImdbId;
        }

        if (title.Length == 0)
            title = Tidy(stem);

        return new ParsedMedia(MediaKind.Movie, title, year, null, [], [], null, tmdbId, imdbId);
    }

    /// <summary>
    /// A bare number is an episode number only without a year, and when at least two other video files
    /// in the same folder have the same title and a number of their own.
    /// </summary>
    private static bool IsBareNumberEpisode(
        ParsedName file, string fileTitle, string fileName, IReadOnlyList<string> siblingFileNames)
    {
        if (file.LeadingNumber is null || file.Year is not null || fileTitle.Length == 0)
            return false;

        var matching = 0;
        foreach (var sibling in siblingFileNames)
        {
            if (string.Equals(sibling, fileName, StringComparison.OrdinalIgnoreCase) || !MediaFileRules.IsVideo(sibling))
                continue;

            var parsed = ReleaseNameParser.Parse(Path.GetFileNameWithoutExtension(sibling));
            if (parsed.LeadingNumber is not null && parsed.Year is null && parsed.Episodes.Count == 0
                && SameTitle(Tidy(parsed.Title), fileTitle))
            {
                if (++matching >= MinSiblingsForBareNumber) return true;
            }
        }
        return false;
    }

    private static FolderInfo ParseFolder(string folder)
    {
        var seasonOnly = SeasonFolder.Match(folder);
        if (seasonOnly.Success)
            return new FolderInfo("", null, SeasonFrom(seasonOnly), null, null, ReleaseNameParser.Parse(folder).Year);

        var parsed = ReleaseNameParser.Parse(folder);
        var title = parsed.Title;

        // "Rocky 2" without a year: the parser moved the 2 out of the title.
        if (parsed.LeadingNumber is { } number && parsed.Episodes.Count == 0 && !NumberPrefix.IsMatch(folder.Trim()))
            title = $"{title} {number}";

        int? season = null;
        var suffix = SeasonSuffix.Match(title);
        if (suffix.Success)
        {
            title = suffix.Groups["title"].Value;
            season = SeasonFrom(suffix);
        }

        // A year after a season marker the title still held ("Brassic - Season 1 (2019)") dates the season.
        var yearDatesSeason = suffix.Success || parsed.YearAfterMarker;
        return new FolderInfo(Tidy(title), yearDatesSeason ? null : parsed.Year, season, parsed.TmdbId, parsed.ImdbId,
            yearDatesSeason ? parsed.Year : null);
    }

    private static int? SeasonFrom(Match match) =>
        match.Groups["n2"].Success ? null : int.Parse(match.Groups["n"].Value);

    private static bool SameTitle(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Tidy(string text) => text.Trim().Trim(Stray);

    /// <param name="Year">The title's year: a movie's release or a show's start.</param>
    /// <param name="AirYear">A year that dates a season rather than the show.</param>
    private sealed record FolderInfo(string Title, int? Year, int? Season, int? TmdbId, string? ImdbId, int? AirYear);
}
