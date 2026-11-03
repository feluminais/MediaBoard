using Dapper;
using MediaBoard.Core.Identification;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Library;

public sealed partial class LocalLibrary
{
    /// <summary>A title's best fuzzy match must score at least this (see <see cref="TitleSimilarity"/>).</summary>
    private const double FuzzyThreshold = 0.6;

    private readonly object _indexGate = new();
    private SearchEntry[]? _searchIndex;
    private int _indexVersion;

    // ---- Home rows and grids ----

    public IReadOnlyList<ContinueItem> ContinueWatching(int limit)
    {
        if (limit <= 0) return [];

        var snapshot = LibrarySnapshot.Load(_db);
        var items = new List<ContinueItem>();
        foreach (var (titleId, title) in snapshot.Titles)
        {
            if (snapshot.IsTitleHidden(titleId)) continue;
            if (ResumePoint(title, snapshot.FilesByTitle[titleId]) is { } point) items.Add(ContinueItemOf(snapshot, title, point));
        }

        return items.OrderByDescending(i => i.LastPlayed).ThenBy(i => i.TitleName, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
    }

    public IReadOnlyList<TitleCard> RecentlyAdded(int limit)
    {
        if (limit <= 0) return [];

        var snapshot = LibrarySnapshot.Load(_db);
        return Cards(snapshot, includeHidden: false)
            .OrderByDescending(c => c.DateAdded).ThenBy(c => SortKey(c.Name), StringComparer.Ordinal)
            .Take(limit).ToList();
    }

    public IReadOnlyList<TitleCard> Titles(TitleKind kind, TitleSort sort, bool includeHidden)
    {
        var snapshot = LibrarySnapshot.Load(_db);
        var cards = Cards(snapshot, includeHidden).Where(c => c.Kind == kind);
        return (sort switch
        {
            TitleSort.Year => cards.OrderByDescending(c => c.Year ?? int.MinValue).ThenBy(c => SortKey(c.Name), StringComparer.Ordinal),
            TitleSort.DateAdded => cards.OrderByDescending(c => c.DateAdded).ThenBy(c => SortKey(c.Name), StringComparer.Ordinal),
            _ => cards.OrderBy(c => SortKey(c.Name), StringComparer.Ordinal),
        }).ToList();
    }

    // ---- Title page ----

    public TitleDetailsView? Title(int titleId)
    {
        var snapshot = LibrarySnapshot.Load(_db);
        if (!snapshot.Titles.TryGetValue(titleId, out var title)) return null;

        var files = snapshot.FilesByTitle[titleId];
        var filesByEpisode = new Dictionary<int, List<FileEntry>>();
        foreach (var file in files)
        {
            foreach (var episode in file.Episodes)
            {
                if (!filesByEpisode.TryGetValue(episode.EpisodeId, out var list)) filesByEpisode[episode.EpisodeId] = list = [];
                list.Add(file);
            }
        }

        var seasons = new List<SeasonView>();
        foreach (var group in _titles.Episodes(titleId).GroupBy(e => e.Season))
        {
            // A season the library holds nothing of is TMDB's list, not the user's.
            if (!group.Any(e => filesByEpisode.ContainsKey(e.Id))) continue;

            var rows = group.Select(e => EpisodeRowOf(snapshot, e, filesByEpisode.GetValueOrDefault(e.Id))).ToList();
            var id = LibraryIds.Season(titleId, group.Key);
            var fallbackName = group.Key == 0 ? "Specials" : $"Season {group.Key}";
            seasons.Add(new SeasonView(
                id, group.Key, snapshot.NameOverride(TargetKind.Season, id) ?? fallbackName,
                snapshot.IsHidden(TargetKind.Season, id), rows));
        }

        var loose = files.Where(f => f.Episodes.Count == 0).OrderBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FileView(
                f.Id, snapshot.NameOverride(TargetKind.File, f.Id) ?? f.Name, f.RelPath, f.Hidden, f.Watched,
                f.Progress?.PositionS ?? 0, f.Progress?.DurationS ?? 0, f.Missing))
            .ToList();

        return new TitleDetailsView(
            title.Id, title.Kind, snapshot.TitleName(title), title.TmdbId, title.Year, title.EndYear, title.Overview,
            title.Rating, title.PosterPath, title.TmdbId is null, snapshot.IsTitleHidden(titleId), files.All(f => f.Missing),
            seasons, loose);
    }

    /// <summary>One file stands for an episode held by several (two versions): a present one, then the last played.</summary>
    private static EpisodeRow EpisodeRowOf(LibrarySnapshot snapshot, StoredEpisode episode, List<FileEntry>? held)
    {
        var name = snapshot.NameOverride(TargetKind.Episode, episode.Id)
                   ?? (string.IsNullOrWhiteSpace(episode.Name) ? $"Episode {episode.Number}" : episode.Name);
        var hidden = snapshot.IsHidden(TargetKind.Episode, episode.Id) || (held is { Count: > 0 } && held.All(f => f.Hidden));
        var file = held?.OrderBy(f => f.Missing).ThenByDescending(f => f.Progress?.LastPlayed ?? DateTime.MinValue).ThenBy(f => f.Id).FirstOrDefault();

        return new EpisodeRow(
            episode.Id, episode.Season, episode.Number, name, episode.AirDate, episode.RuntimeMin, hidden,
            file?.Id, held?.Any(f => f.Watched) ?? false, file?.Progress?.PositionS ?? 0, file?.Progress?.DurationS ?? 0,
            file?.Missing ?? false);
    }

    // ---- Needs review and folders ----

    /// <summary>
    /// One COUNT query: a file is left out when it, or its folder, is hidden, or its folder is disabled (the same
    /// rules as the snapshot).
    /// </summary>
    public int NeedsReviewCount()
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<int>(
            """
            SELECT COUNT(*) FROM files f
            WHERE f.status = 'review'
              AND NOT EXISTS (SELECT 1 FROM folders d WHERE d.id = f.folder_id AND d.enabled = 0)
              AND NOT EXISTS (SELECT 1 FROM overrides o WHERE o.hidden = 1
                              AND ((o.target_kind = 'file' AND o.target_id = f.id)
                                OR (o.target_kind = 'folder' AND o.target_id = f.folder_id)))
            """);
    }

    public IReadOnlyList<ReviewItem> NeedsReview()
    {
        var snapshot = LibrarySnapshot.Load(_db);
        var visible = snapshot.Files.Where(f => f.Status == "review" && !f.Hidden).ToDictionary(f => f.Id);
        if (visible.Count == 0) return [];

        Dictionary<int, List<ReviewCandidate>> candidates;
        using (var connection = _db.Open())
        {
            candidates = connection.Query<CandidateDto>(
                    "SELECT file_id AS FileId, tmdb_id AS TmdbId, kind, name, year FROM review_candidates ORDER BY file_id, score DESC")
                .GroupBy(c => (int)c.FileId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(c => new ReviewCandidate(
                        (int)c.TmdbId, c.Kind == "show" ? TitleKind.Show : TitleKind.Movie, c.Name, (int?)c.Year)).ToList());
        }

        return _files.ByStatus("review")
            .Where(f => visible.ContainsKey(f.Id))
            .OrderBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ReviewItem(
                f.Id, Path.GetFileName(f.RelPath), f.RelPath, f.Parsed.Title, f.Parsed.Year, f.Parsed.Kind, f.Parsed.Season,
                f.Parsed.Episodes, f.Missing, candidates.GetValueOrDefault(f.Id) ?? [], f.Parsed.ShowFolder))
            .ToList();
    }

    public IReadOnlyList<FolderRow> Folders()
    {
        var snapshot = LibrarySnapshot.Load(_db);
        var counts = snapshot.FileCountByFolder;
        return snapshot.Folders.Select(folder =>
        {
            var scan = _scans.GetValueOrDefault(folder.Id);
            var (shows, movies) = snapshot.TitleCountByFolder.GetValueOrDefault(folder.Id);
            return new FolderRow(
                folder.Id, folder.Path, folder.Enabled, snapshot.IsHidden(TargetKind.Folder, folder.Id),
                (scan?.Unavailable ?? false) || !Directory.Exists(folder.Path), counts.GetValueOrDefault(folder.Id),
                scan?.IgnoredFolders ?? [], shows, movies);
        }).ToList();
    }

    // ---- Next episode ----

    public int? NextEpisodeFileId(int titleId)
    {
        var snapshot = LibrarySnapshot.Load(_db);
        if (!snapshot.Titles.TryGetValue(titleId, out var title)) return null;

        var all = snapshot.FilesByTitle[titleId];
        if (ResumePoint(title, all) is { File.Missing: false } point) return point.File.Id;

        // Nothing started, or what was started leads nowhere: the first episode not watched yet, else the first
        // one again. Specials only count when the title has nothing else.
        var playable = InPlayOrder(all.Where(f => !f.Hidden && !f.Missing)).ToList();
        var regular = playable.Where(f => !f.OnlySpecials).ToList();
        if (regular.Count == 0) regular = playable;
        var watched = WatchedEpisodes(all);
        return (regular.FirstOrDefault(f => !IsWatched(f, watched)) ?? regular.FirstOrDefault())?.Id;
    }

    private readonly record struct Resume(FileEntry File, bool IsNext, DateTime LastPlayed);

    /// <summary>
    /// Where the user would pick up. Looks at the file played last: left half-way, it is the answer; finished, a
    /// show moves on to the next episode that has a file and is not watched. A movie that was finished has none.
    /// </summary>
    private static Resume? ResumePoint(TitleRow title, List<FileEntry> allFiles)
    {
        var files = allFiles.Where(f => !f.Hidden).ToList();
        var played = files.Where(f => f.WasPlayed)
            .OrderByDescending(f => f.Progress!.LastPlayed).ThenBy(f => f.Id).FirstOrDefault();
        if (played is null) return null;
        if (played.InProgress) return new Resume(played, false, played.Progress!.LastPlayed);
        if (title.Kind != TitleKind.Show) return null;

        return NextAfter(files, WatchedEpisodes(allFiles), played) is { } next
            ? new Resume(next, true, played.Progress!.LastPlayed)
            : null;
    }

    /// <summary>The first file after the last episode of <paramref name="played"/> that is present and not watched.</summary>
    private static FileEntry? NextAfter(List<FileEntry> files, HashSet<int> watchedEpisodes, FileEntry played)
    {
        foreach (var candidate in InPlayOrder(files))
        {
            if (candidate == played || candidate.Missing || candidate.Hidden || IsWatched(candidate, watchedEpisodes)) continue;

            if (played.Episodes.Count == 0)
            {
                // Files nobody placed in an episode follow the order of their names.
                if (candidate.Episodes.Count == 0 && string.Compare(candidate.RelPath, played.RelPath, StringComparison.OrdinalIgnoreCase) > 0)
                    return candidate;
                continue;
            }

            // Specials are not "next" after a regular episode.
            if (candidate.OnlySpecials && !played.OnlySpecials) continue;
            if (candidate.Episodes.Any(e => e.Key > played.LastKey && !watchedEpisodes.Contains(e.EpisodeId))) return candidate;
        }

        return null;
    }

    private static IEnumerable<FileEntry> InPlayOrder(IEnumerable<FileEntry> files) =>
        files.OrderBy(f => f.FirstKey).ThenBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase);

    /// <summary>Episodes covered by a watched file; one file can cover several.</summary>
    private static HashSet<int> WatchedEpisodes(IEnumerable<FileEntry> files) =>
        files.Where(f => f.Watched).SelectMany(f => f.Episodes.Select(e => e.EpisodeId)).ToHashSet();

    private static bool IsWatched(FileEntry file, HashSet<int> watchedEpisodes) =>
        file.Watched || (file.Episodes.Count > 0 && file.Episodes.All(e => watchedEpisodes.Contains(e.EpisodeId)));

    private static ContinueItem ContinueItemOf(LibrarySnapshot snapshot, TitleRow title, Resume point)
    {
        var file = point.File;
        var episodes = file.Episodes.OrderBy(e => e.Key).ToList();
        string? episodeName = episodes.Count > 0
            ? string.Join(" & ", episodes.Select(e => EpisodeName(snapshot, e)))
            : title.Kind == TitleKind.Show ? snapshot.NameOverride(TargetKind.File, file.Id) ?? file.Name : null;

        return new ContinueItem(
            title.Id, file.Id, title.Kind, snapshot.TitleName(title), EpisodeLabel(episodes), episodeName, title.PosterPath,
            point.IsNext ? 0 : file.Progress?.PositionS ?? 0, file.Progress?.DurationS ?? 0, point.IsNext, file.Missing,
            point.LastPlayed);
    }

    private static string EpisodeName(LibrarySnapshot snapshot, EpisodeRef episode) =>
        snapshot.NameOverride(TargetKind.Episode, episode.EpisodeId)
        ?? (string.IsNullOrWhiteSpace(episode.Name) ? $"Episode {episode.Number}" : episode.Name);

    private static string? EpisodeLabel(List<EpisodeRef> episodes)
    {
        if (episodes.Count == 0) return null;

        var first = episodes[0];
        var last = episodes[^1];
        var start = $"S{first.Season:00}E{first.Number:00}";
        if (episodes.Count == 1) return start;
        return last.Season == first.Season ? $"{start}-E{last.Number:00}" : $"{start}-S{last.Season:00}E{last.Number:00}";
    }

    // ---- Cards ----

    private static IEnumerable<TitleCard> Cards(LibrarySnapshot snapshot, bool includeHidden)
    {
        foreach (var (titleId, title) in snapshot.Titles)
        {
            var files = snapshot.FilesByTitle[titleId];
            var hidden = snapshot.IsTitleHidden(titleId);
            if (hidden && !includeHidden) continue;

            yield return new TitleCard(
                titleId, title.Kind, snapshot.TitleName(title), title.Year, title.EndYear, title.PosterPath, title.Rating,
                title.TmdbId is null, hidden, files.All(f => f.Missing), files.Max(f => f.FirstSeen));
        }
    }

    /// <summary>Sorts "The Sopranos" under S, like the search does.</summary>
    private static string SortKey(string name) => TitleNormalizer.Normalize(name);

    // ---- Local search ----

    /// <summary>
    /// A match is, best first: the whole name, the start of the name, a piece of the name, or every typed word
    /// starting some word of the name. Only when nothing matches that way does a typo-tolerant comparison run, so the
    /// common search never pays for it. The visible titles are kept in memory until something changes.
    /// </summary>
    public IReadOnlyList<TitleCard> SearchLocal(string query, int limit)
    {
        var normalized = TitleNormalizer.Normalize(query ?? "");
        if (normalized.Length == 0 || limit <= 0) return [];

        var index = SearchIndex();
        var typed = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var hits = new List<(int Rank, double Score, SearchEntry Entry)>();
        foreach (var entry in index)
        {
            var rank = entry.Keys.Min(key => Rank(normalized, typed, key));
            if (rank != NoMatch) hits.Add((rank, 1.0, entry));
        }

        if (hits.Count == 0)
        {
            var typedLetters = Letters(normalized);
            foreach (var entry in index)
            {
                var score = 0.0;
                for (var i = 0; i < entry.Keys.Length; i++)
                {
                    if (CanReachThreshold(normalized, typedLetters, entry.Keys[i], entry.Letters[i]))
                        score = Math.Max(score, TitleSimilarity.ScoreNormalized(normalized, entry.Keys[i], numbersMustAgree: false));
                }

                if (score >= FuzzyThreshold) hits.Add((NoMatch, score, entry));
            }
        }

        return hits.OrderBy(h => h.Rank).ThenByDescending(h => h.Score).ThenBy(h => h.Entry.Keys[0], StringComparer.Ordinal)
            .Take(limit).Select(h => h.Entry.Card).ToList();
    }

    private const int NoMatch = int.MaxValue;

    private static int Rank(string query, string[] typed, string key)
    {
        if (key == query) return 0;
        if (key.StartsWith(query, StringComparison.Ordinal)) return 1;
        if (key.Contains(query, StringComparison.Ordinal)) return 2;

        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return typed.All(t => words.Any(w => w.StartsWith(t, StringComparison.Ordinal))) ? 3 : NoMatch;
    }

    /// <param name="Keys">Normalized names to match against: the display name first, then the TMDB name when it differs.</param>
    /// <param name="Letters">Per key, how often each letter bucket occurs (see <see cref="Letters"/>).</param>
    private sealed record SearchEntry(TitleCard Card, string[] Keys, int[][] Letters);

    private const int LetterBuckets = 64;

    private static int[] Letters(string text)
    {
        var counts = new int[LetterBuckets];
        foreach (var ch in text) counts[ch % LetterBuckets]++;
        return counts;
    }

    /// <summary>
    /// A cheap test that never rejects a title <see cref="TitleSimilarity"/> would accept. The score is
    /// TitleSortShare * sortRatio + TitleSetShare * setRatio with both ratios at most 1, so reaching the threshold
    /// needs a sortRatio of (threshold - TitleSetShare) / TitleSortShare. The sortRatio is 1 - edits / longer, and
    /// edits are at least longer - (letters the two have in common), so the letters in common must be that share of
    /// the longer text. Letters are counted in 64 buckets, which can only add to the overlap.
    /// </summary>
    private static bool CanReachThreshold(string typed, int[] typedLetters, string key, int[] keyLetters)
    {
        var longer = Math.Max(typed.Length, key.Length);
        var common = 0;
        for (var i = 0; i < LetterBuckets; i++) common += Math.Min(typedLetters[i], keyLetters[i]);

        const double neededSortRatio = (FuzzyThreshold - ScoringWeights.TitleSetShare) / ScoringWeights.TitleSortShare;
        return common >= neededSortRatio * longer;
    }

    private SearchEntry[] SearchIndex()
    {
        int version;
        lock (_indexGate)
        {
            if (_searchIndex is { } cached) return cached;
            version = _indexVersion;
        }

        var snapshot = LibrarySnapshot.Load(_db);
        var built = Cards(snapshot, includeHidden: false)
            .Select(card =>
            {
                var tmdbName = snapshot.Titles[card.TitleId].Name;
                string[] keys = [SortKey(card.Name), SortKey(tmdbName)];
                if (keys[0] == keys[1]) keys = [keys[0]];
                return new SearchEntry(card, keys, keys.Select(Letters).ToArray());
            })
            .ToArray();

        // A change that landed while the list was being built leaves it stale; the next search builds it again.
        lock (_indexGate)
        {
            if (version == _indexVersion) _searchIndex = built;
        }

        return built;
    }

    private void InvalidateSearchIndex()
    {
        lock (_indexGate)
        {
            _indexVersion++;
            _searchIndex = null;
        }
    }

    private sealed class CandidateDto
    {
        public long FileId { get; set; }
        public long TmdbId { get; set; }
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public long? Year { get; set; }
    }
}
