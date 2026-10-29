using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;
using MediaBoard.Core.Probing;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Identification;

/// <summary>
/// Identifies scanned files that are still pending or were left offline and stores the result: title,
/// episodes and status. A file nobody could place yet gets a provisional title named after the file, so it
/// stays playable and listed. A run stops asking TMDB at the first sign that it cannot answer (offline, no
/// key, key rejected) and picks up again when the connection returns or the key changes.
/// </summary>
public sealed class IdentificationService : IDisposable
{
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);

    private readonly LibraryDatabase _db;
    private readonly IMetadataProvider _provider;
    private readonly IDurationProbe _probe;
    private readonly Connectivity _connectivity;
    private readonly SettingsStore _settings;
    private readonly ActivityHub _hub;
    private readonly FileStore _files;
    private readonly TitleStore _titles;
    private readonly MatchStore _matches;
    private readonly OverrideStore _overrides;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _current;
    private bool _again;
    private bool _started;
    private bool _disposed;
    private bool _keyBlocked;
    private string? _blockedKey;

    public IdentificationService(
        LibraryDatabase db,
        IMetadataProvider provider,
        IDurationProbe probe,
        Connectivity connectivity,
        SettingsStore settings,
        ActivityHub hub,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _provider = provider;
        _probe = probe;
        _connectivity = connectivity;
        _settings = settings;
        _hub = hub;
        _files = new FileStore(db);
        _titles = new TitleStore(db, timeProvider);
        _matches = new MatchStore(db);
        _overrides = new OverrideStore(db);
    }

    /// <summary>Starts reacting to the connection coming back and to a new TMDB key, and runs once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
        }

        _connectivity.Changed += OnConnectivityChanged;
        _settings.Changed += OnSettingsChanged;
        _ = RunPendingAsync(_lifetime.Token);
    }

    /// <summary>
    /// Identifies every pending and offline file. One run at a time: a request made during a run schedules
    /// one more after it, and the returned task completes when both are done. Never throws; problems go to
    /// the activity log.
    /// </summary>
    public Task RunPendingAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_current is { } running)
            {
                _again = true;
                return running;
            }

            return _current = Task.Run(() => RunLoopAsync(ct), CancellationToken.None);
        }
    }

    /// <summary>Completes when no run is going (including any run scheduled behind it).</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate) return _current ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        Task? running;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            running = _current;
        }

        _connectivity.Changed -= OnConnectivityChanged;
        _settings.Changed -= OnSettingsChanged;
        _lifetime.Cancel();
        running?.Wait(DisposeWait);
        _lifetime.Dispose();
    }

    private void OnConnectivityChanged(bool online)
    {
        if (online) _ = RunPendingAsync(_lifetime.Token);
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        lock (_gate)
        {
            if (!_keyBlocked || NormalizeKey(settings.TmdbKey) == _blockedKey) return;
        }

        _ = RunPendingAsync(_lifetime.Token);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _current = null;
                    _again = false;
                }

                return;
            }

            lock (_gate)
            {
                if (!_again)
                {
                    _current = null;
                    return;
                }

                _again = false;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await ProcessAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _hub.Log(ActivityLevel.Error, "Identification stopped on an error.", e.Message);
        }
    }

    // ---- One run ----

    private enum Stop { None, Offline, NoKey, KeyRejected }

    private sealed class Run
    {
        public Run(IMetadataProvider provider)
        {
            Provider = new CachingProvider(provider);
            Identifier = new Identifier(Provider);
        }

        public CachingProvider Provider { get; }

        public Identifier Identifier { get; }

        public Dictionary<ProvisionalKey, int> Provisional { get; } = [];

        public HashSet<(int TitleId, int Season)> StoredSeasons { get; } = [];

        public int Waiting { get; set; }
    }

    private readonly record struct ProvisionalKey(string Title, TitleKind Kind, int FolderId, string ShowFolder);

    private async Task ProcessAsync(CancellationToken ct)
    {
        // Parses stored by an older parser are redone first, and the files they may have misled are queued again.
        ParseUpgrade.Run(_db);

        var renamed = RenamedPlaceholders();
        // Disabled folders wait until they are switched on again; removed ones have no files left.
        var work = _files.ByStatusInEnabledFolders("pending").Concat(_files.ByStatusInEnabledFolders("offline"))
            .Where(f => !f.Missing)
            .OrderBy(f => f.FolderId)
            .ThenBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            _hub.SetPendingIdentification(work.Count);
            if (work.Count == 0) return;

            var key = NormalizeKey(_settings.Load().TmdbKey);
            var run = NewRun();
            var stop = StopBeforeStart(key);
            run.Waiting = work.Count;

            foreach (var file in work)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // The user may have removed or switched off the folder while the pass ran.
                    if (!_files.IsActive(file.Id))
                    {
                        run.Waiting--;
                        _hub.SetPendingIdentification(run.Waiting);
                        continue;
                    }

                    if (RestoreUserMatch(file, run)) continue;

                    if (stop != Stop.None) MarkUnreachable(file, run);
                    else stop = await IdentifyAsync(file, run, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception) when (!_files.IsActive(file.Id))
                {
                    // Its folder was removed or switched off while the file was being identified.
                    run.Waiting--;
                    _hub.SetPendingIdentification(run.Waiting);
                }
                catch (Exception e)
                {
                    _hub.Log(ActivityLevel.Error, $"Could not identify {Path.GetFileName(file.RelPath)}.", e.Message);
                }
            }

            _hub.SetPendingIdentification(run.Waiting);
        }
        finally
        {
            CarryPlaceholderNames(renamed);
            _titles.DeleteOrphanProvisional();
        }
    }

    private sealed record RenamedPlaceholder(int TitleId, ProvisionalKey Key, string Name);

    /// <summary>Placeholder titles the user renamed, with the key that says which files are theirs.</summary>
    private List<RenamedPlaceholder> RenamedPlaceholders()
    {
        var names = _overrides.All()
            .Where(o => o is { Kind: TargetKind.Title, DisplayName: not null })
            .ToDictionary(o => o.TargetId, o => o.DisplayName!);
        if (names.Count == 0) return [];

        return _titles.ProvisionalLinks()
            .Where(l => names.ContainsKey(l.TitleId))
            .DistinctBy(l => l.TitleId)
            .Select(l => new RenamedPlaceholder(l.TitleId, KeyOf(l.Parsed.Title, l.Kind, l.FolderId, l.Parsed.ShowFolder), names[l.TitleId]))
            .ToList();
    }

    /// <summary>A renamed placeholder whose files all went to one title hands that title its name (when it has none).</summary>
    private void CarryPlaceholderNames(List<RenamedPlaceholder> renamed)
    {
        foreach (var placeholder in renamed)
        {
            var files = _files.ByFolder(placeholder.Key.FolderId)
                .Where(f => KeyOf(f.Parsed.Title, f.Parsed.Kind == MediaKind.Episode ? TitleKind.Show : TitleKind.Movie,
                    f.FolderId, f.Parsed.ShowFolder) == placeholder.Key)
                .Select(f => f.Id)
                .ToList();
            _matches.CarryPlaceholderName(placeholder.TitleId, files, placeholder.Name);
        }
    }

    private Run NewRun()
    {
        var run = new Run(_provider);
        foreach (var link in _titles.ProvisionalLinks())
        {
            run.Provisional[KeyOf(link.Parsed.Title, link.Kind, link.FolderId, link.Parsed.ShowFolder)] = link.TitleId;
        }

        return run;
    }

    /// <summary>
    /// Nothing is asked of TMDB when we already know it cannot answer: offline, or the key that failed
    /// is still the saved one. A key that changed since the failure lifts the pause.
    /// </summary>
    private Stop StopBeforeStart(string? key)
    {
        lock (_gate)
        {
            if (_keyBlocked && key != _blockedKey) _keyBlocked = false;
            if (_keyBlocked) return key is null ? Stop.NoKey : Stop.KeyRejected;
        }

        return _connectivity.IsOnline ? Stop.None : Stop.Offline;
    }

    /// <summary>
    /// A file the user matched is never identified again. A rescan may have reset it to pending, so its
    /// status goes back to matched, with no call to TMDB and whatever state the run is in.
    /// </summary>
    private bool RestoreUserMatch(MediaFileRow file, Run run)
    {
        if (!_matches.HasUserMatch(file.Id)) return false;

        _matches.RestoreMatched(file.Id);
        run.Waiting--;
        _hub.SetPendingIdentification(run.Waiting);
        return true;
    }

    /// <summary>Identifies one file. Returns why the run must stop, or None.</summary>
    private async Task<Stop> IdentifyAsync(MediaFileRow file, Run run, CancellationToken ct)
    {
        if (_files.TryFullPath(file) is not { } fullPath)
        {
            // Its folder was just removed: the file is done with, so it stops counting as waiting.
            run.Waiting--;
            _hub.SetPendingIdentification(run.Waiting);
            return Stop.None;
        }

        var duration = file.DurationS;
        if (duration is null)
        {
            duration = await _probe.GetDurationSecondsAsync(fullPath, ct).ConfigureAwait(false);
            if (duration is > 0) _files.SetDuration(file.Id, duration.Value);
            else duration = null;
        }

        var outcome = await TryRuleAsync(file, run, ct).ConfigureAwait(false)
                      ?? new Outcome(await run.Identifier.IdentifyAsync(file.Parsed, fullPath, duration, ct).ConfigureAwait(false));
        var result = outcome.Result;

        switch (result.Status)
        {
            case IdentificationStatus.Matched:
                await StoreMatchAsync(file, outcome, run, ct).ConfigureAwait(false);
                break;
            case IdentificationStatus.NeedsReview:
                _matches.SetReview(file.Id, result.Candidates, ProvisionalFor(file, run));
                break;
            default:
                _matches.SetProvisional(file.Id, ProvisionalFor(file, run));
                return StopFor(result.Status);
        }

        run.Waiting--;
        _hub.SetPendingIdentification(run.Waiting);
        return Stop.None;
    }

    private Stop StopFor(IdentificationStatus status)
    {
        switch (status)
        {
            case IdentificationStatus.NoKey:
            case IdentificationStatus.KeyRejected:
                BlockKey();
                _hub.Log(
                    ActivityLevel.Warning,
                    status == IdentificationStatus.NoKey ? "Identification paused: no TMDB key." : "Identification paused: TMDB rejected the key.",
                    "It resumes when you save a different key in Settings.");
                return status == IdentificationStatus.NoKey ? Stop.NoKey : Stop.KeyRejected;
            default:
                _connectivity.ReportOffline();
                _hub.Log(ActivityLevel.Warning, "Identification paused: TMDB is unreachable.", "It resumes when the connection is back.");
                return Stop.Offline;
        }
    }

    private void BlockKey()
    {
        var key = NormalizeKey(_settings.Load().TmdbKey);
        lock (_gate)
        {
            _keyBlocked = true;
            _blockedKey = key;
        }
    }

    /// <summary>A file the run will not ask about keeps (or gets) a provisional title and the status offline.</summary>
    private void MarkUnreachable(MediaFileRow file, Run run)
    {
        // Already offline with its provisional title: a rescan would have reset it to pending if the name changed.
        if (file.Status == "offline") return;

        _matches.SetProvisional(file.Id, ProvisionalFor(file, run));
    }

    // ---- Match rules ----

    private sealed record Outcome(
        IdentificationResult Result, int? TitleId = null, IReadOnlyList<SeasonDetails>? FetchedSeasons = null);

    /// <summary>
    /// An episode inside a show folder that has a rule for its parsed title goes to the rule's show without
    /// asking TMDB which show it is. Movies and loose files never use rules.
    /// </summary>
    private async Task<Outcome?> TryRuleAsync(MediaFileRow file, Run run, CancellationToken ct)
    {
        var parsed = file.Parsed;
        if (parsed.Kind != MediaKind.Episode || string.IsNullOrEmpty(parsed.ShowFolder)) return null;
        if (_matches.FindRule(file.FolderId, parsed.ShowFolder, parsed.Title) is not { } titleId) return null;
        if (_titles.Get(titleId) is not { Kind: TitleKind.Show, TmdbId: not null } show) return null;

        try
        {
            var fetched = new List<SeasonDetails>();
            var episodes = await ResolveRuleEpisodesAsync(show, parsed, preferStored: true, fetched, run, ct).ConfigureAwait(false);

            // The file's own season came from storage and may be stale (a new episode since): ask TMDB once more.
            // Other seasons the lookup fetched say nothing about that.
            var number = parsed.Season ?? 1;
            if (episodes is null && fetched.All(s => s.Season != number))
            {
                episodes = await ResolveRuleEpisodesAsync(show, parsed, preferStored: false, fetched, run, ct).ConfigureAwait(false);
            }

            if (episodes is null)
            {
                var candidate = new SearchCandidate(
                    show.TmdbId!.Value, TitleKind.Show, show.Name, null, show.Year, 0, show.Rating, show.PosterPath, show.Overview);
                return new Outcome(new IdentificationResult(IdentificationStatus.NeedsReview, null, [], 1.0, [candidate], "rule"));
            }

            // Other seasons read only to look a title up are not stored unless the file landed in one.
            var keep = fetched.Where(s => s.Season == number || s.Season == 0 || episodes.Any(e => e.Season == s.Season)).ToList();
            var details = new TitleDetails(
                show.TmdbId!.Value, TitleKind.Show, show.Name, show.Year, show.EndYear, show.Overview, show.Rating, show.PosterPath, null, []);
            return new Outcome(new IdentificationResult(IdentificationStatus.Matched, details, episodes, 1.0, [], "rule"), show.Id, keep);
        }
        catch (MetadataUnavailableException e)
        {
            var status = e.Reason switch
            {
                UnavailableReason.NoKey => IdentificationStatus.NoKey,
                UnavailableReason.KeyRejected => IdentificationStatus.KeyRejected,
                _ => IdentificationStatus.Offline,
            };
            return new Outcome(new IdentificationResult(status, null, [], 0, [], "none"));
        }
    }

    /// <summary>
    /// The same episode lookup as the identifier's, other seasons included, reading stored seasons first when
    /// <paramref name="preferStored"/> is set. The show's season list is asked of TMDB only when a title has
    /// to be looked up in the other seasons.
    /// </summary>
    private Task<IReadOnlyList<EpisodeDetails>?> ResolveRuleEpisodesAsync(
        TitleRow show, ParsedMedia parsed, bool preferStored, List<SeasonDetails> fetched, Run run, CancellationToken ct) =>
        EpisodeResolver.ResolveAsync(
            parsed,
            number => RuleSeasonAsync(show, number, preferStored, fetched, run, ct),
            () => RuleSeasonNumbersAsync(show, run, ct));

    private static async Task<IReadOnlyList<int>> RuleSeasonNumbersAsync(TitleRow show, Run run, CancellationToken ct)
    {
        try
        {
            return (await run.Provider.GetTitleAsync(show.TmdbId!.Value, TitleKind.Show, ct).ConfigureAwait(false)).SeasonNumbers;
        }
        catch (MetadataUnavailableException e) when (e.Reason == UnavailableReason.ServerError)
        {
            return [];
        }
    }

    private async Task<SeasonDetails?> RuleSeasonAsync(
        TitleRow show, int season, bool preferStored, List<SeasonDetails> fetched, Run run, CancellationToken ct)
    {
        if (preferStored && _titles.GetSeason(show.Id, season) is { } stored) return stored;

        try
        {
            var details = await run.Provider.GetSeasonAsync(show.TmdbId!.Value, season, ct).ConfigureAwait(false);
            fetched.Add(details);
            return details;
        }
        catch (MetadataUnavailableException e) when (e.Reason == UnavailableReason.ServerError)
        {
            return null;
        }
    }

    // ---- Storing ----

    private async Task StoreMatchAsync(MediaFileRow file, Outcome outcome, Run run, CancellationToken ct)
    {
        var result = outcome.Result;
        var title = result.Title!;
        var titleId = outcome.TitleId ?? _titles.Upsert(title);
        var episodeIds = new List<int>();

        if (title.Kind == TitleKind.Show)
        {
            var seasons = outcome.FetchedSeasons;
            if (seasons is null)
            {
                // The identifier read these seasons through the cache, so asking again costs nothing.
                var list = new List<SeasonDetails>();
                foreach (var number in result.Episodes.Select(e => e.Season).Distinct())
                {
                    list.Add(await run.Provider.GetSeasonAsync(title.TmdbId, number, ct).ConfigureAwait(false));
                }

                seasons = list;
            }

            foreach (var season in seasons)
            {
                if (run.StoredSeasons.Add((titleId, season.Season))) _titles.UpsertEpisodes(titleId, season);
            }

            episodeIds.AddRange(_titles.EpisodeIds(titleId, result.Episodes.Select(e => (e.Season, e.Number))));
        }

        _matches.SetMatch(file.Id, titleId, episodeIds, result.Confidence, result.Source);
    }

    private int ProvisionalFor(MediaFileRow file, Run run)
    {
        var parsed = file.Parsed;
        var kind = parsed.Kind == MediaKind.Episode ? TitleKind.Show : TitleKind.Movie;
        var name = string.IsNullOrWhiteSpace(parsed.Title) ? Path.GetFileNameWithoutExtension(file.RelPath) : parsed.Title;
        var key = KeyOf(name, kind, file.FolderId, parsed.ShowFolder);
        if (run.Provisional.TryGetValue(key, out var existing)) return existing;

        var id = _titles.UpsertProvisional(name, kind, kind == TitleKind.Movie ? parsed.Year : null);
        run.Provisional[key] = id;
        return id;
    }

    private static ProvisionalKey KeyOf(string title, TitleKind kind, int folderId, string? showFolder) =>
        new(TitleNormalizer.Normalize(title), kind, folderId, showFolder ?? "");

    private static string? NormalizeKey(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();

    // ---- Per-run cache ----

    /// <summary>
    /// Answers repeated questions from memory for the length of one run, so a show's details and each of
    /// its seasons are fetched once however many episodes are identified. A run is sequential, so there is
    /// no locking. A failure is kept only when TMDB answered with an error for that id; being offline or
    /// having a bad key is not remembered.
    /// </summary>
    private sealed class CachingProvider(IMetadataProvider inner) : IMetadataProvider
    {
        private readonly Dictionary<(int, TitleKind), Task<TitleDetails>> _titles = [];
        private readonly Dictionary<(int, int), Task<SeasonDetails>> _seasons = [];
        private readonly Dictionary<(string, int?, TitleKind), Task<IReadOnlyList<SearchCandidate>>> _searches = [];

        public Task<IReadOnlyList<SearchCandidate>> SearchAsync(string title, int? year, TitleKind kind, CancellationToken ct) =>
            Cached(_searches, (title.ToUpperInvariant(), year, kind), () => inner.SearchAsync(title, year, kind, ct));

        public Task<IReadOnlyList<SearchCandidate>> SearchAnyAsync(string query, CancellationToken ct) =>
            inner.SearchAnyAsync(query, ct);

        public Task<(int TmdbId, TitleKind Kind)?> FindByImdbIdAsync(string imdbId, CancellationToken ct) =>
            inner.FindByImdbIdAsync(imdbId, ct);

        public Task<TitleDetails> GetTitleAsync(int tmdbId, TitleKind kind, CancellationToken ct) =>
            Cached(_titles, (tmdbId, kind), () => inner.GetTitleAsync(tmdbId, kind, ct));

        public Task<SeasonDetails> GetSeasonAsync(int showTmdbId, int season, CancellationToken ct) =>
            Cached(_seasons, (showTmdbId, season), () => inner.GetSeasonAsync(showTmdbId, season, ct));

        private static async Task<T> Cached<TKey, T>(Dictionary<TKey, Task<T>> cache, TKey key, Func<Task<T>> fetch)
            where TKey : notnull
        {
            if (!cache.TryGetValue(key, out var task))
            {
                task = fetch();
                cache[key] = task;
            }

            try
            {
                return await task.ConfigureAwait(false);
            }
            catch (Exception e) when (e is not MetadataUnavailableException { Reason: UnavailableReason.ServerError })
            {
                cache.Remove(key);
                throw;
            }
        }
    }
}
