using System.Collections.Concurrent;
using MediaBoard.Core.Identification;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Library;

/// <summary>
/// <see cref="ILibrary"/> over the local database. This half holds the edits and the delete; the queries are in
/// LocalLibrary.Queries.cs.
/// <para>
/// Edits only write to the database. <see cref="DeleteFromDiskAsync"/> is the one call that touches files, and it
/// sends them to the Recycle Bin through the injected function (a file Windows would delete for good, or that
/// could not be moved, stays and is reported). Their rows are removed from the library at once
/// rather than left for the next scan to mark missing, so the title is gone as soon as the user confirms.
/// </para>
/// <para>
/// <see cref="Changed"/> is raised on the calling thread after every edit. When the data changes behind the
/// library's back (a scan, an identification run) the app calls <see cref="NotifyChanged"/>.
/// </para>
/// </summary>
public sealed partial class LocalLibrary : ILibrary
{
    private readonly LibraryDatabase _db;
    private readonly IMetadataProvider _provider;
    private readonly Func<string, RecycleResult> _sendToRecycleBin;
    private readonly Func<string, DriveType> _driveTypeOf;
    private readonly FileStore _files;
    private readonly FolderStore _folders;
    private readonly TitleStore _titles;
    private readonly MatchStore _matches;
    private readonly ProgressStore _progress;
    private readonly OverrideStore _overrides;
    private readonly ConcurrentDictionary<int, ScanInfo> _scans;
    private readonly Shared _shared;
    private readonly Viewer _viewer;
    private readonly bool _usesSharedBase;

    public LocalLibrary(
        LibraryDatabase db,
        IMetadataProvider provider,
        Func<string, RecycleResult>? sendToRecycleBin = null,
        TimeProvider? timeProvider = null,
        Func<string, DriveType>? driveTypeOf = null,
        bool shareSnapshots = false)
    {
        _db = db;
        _provider = provider;
        _sendToRecycleBin = sendToRecycleBin ?? RecycleBin.Send;
        _driveTypeOf = driveTypeOf ?? DriveTypeOf;
        _files = new FileStore(db);
        _folders = new FolderStore(db, timeProvider);
        _titles = new TitleStore(db, timeProvider);
        _matches = new MatchStore(db);
        _progress = new ProgressStore(db, timeProvider);
        _overrides = new OverrideStore(db);
        _scans = new ConcurrentDictionary<int, ScanInfo>();
        _shared = new Shared(shareSnapshots ? new SharedLibraryBase(db, timeProvider ?? TimeProvider.System) : null);
        _viewer = Viewer.Owner;
        _usesSharedBase = false;
    }

    private LocalLibrary(LocalLibrary source, Viewer viewer)
    {
        _db = source._db;
        _provider = source._provider;
        _sendToRecycleBin = source._sendToRecycleBin;
        _driveTypeOf = source._driveTypeOf;
        _files = source._files;
        _folders = source._folders;
        _titles = source._titles;
        _matches = source._matches;
        _progress = source._progress;
        _overrides = source._overrides;
        _scans = source._scans;
        _shared = source._shared;
        _viewer = viewer;
        _usesSharedBase = true;
    }

    /// <summary>
    /// The same library seen by another viewer: their progress and their folders. It shares the stores, the scan
    /// info and <see cref="Changed"/> with this one, so an edit through either reaches everyone listening.
    /// </summary>
    public LocalLibrary ForViewer(Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return new LocalLibrary(this, viewer);
    }

    /// <summary>Something this PC's pages show changed: the library, or the owner's own progress.</summary>
    public event Action? Changed
    {
        add => _shared.Changed += value;
        remove => _shared.Changed -= value;
    }

    /// <summary>The library that every viewer shares changed. Progress doesn't count: that belongs to one account.</summary>
    public event Action? ContentChanged
    {
        add => _shared.ContentChanged += value;
        remove => _shared.ContentChanged -= value;
    }

    /// <summary>Raised with the account whose watched marks, positions or dismissals changed.</summary>
    public event Action<int>? ProgressChanged
    {
        add => _shared.ProgressChanged += value;
        remove => _shared.ProgressChanged -= value;
    }

    /// <summary>Tells the library its data changed underneath it (after a scan or an identification run).</summary>
    public void NotifyChanged() => RaiseChanged();

    /// <summary>Tells the library this viewer's progress changed underneath it. The library itself is as it was.</summary>
    public void NotifyProgressChanged() => _shared.RaiseProgress(_viewer.UserId);

    /// <summary>
    /// Keeps what a scan found that the database does not: the non-media folders it skipped and whether the folder
    /// could be read. The app calls this with every scan result; it also raises <see cref="Changed"/>.
    /// </summary>
    public void RecordScan(int folderId, ScanResult result)
    {
        _scans.AddOrUpdate(
            folderId,
            _ => new ScanInfo(result.IgnoredFolders, result.FolderUnavailable),
            // An unreadable folder reports no listing, so the last list of skipped folders stays.
            (_, previous) => new ScanInfo(result.FolderUnavailable ? previous.IgnoredFolders : result.IgnoredFolders, result.FolderUnavailable));
        RaiseChanged();
    }

    // ---- Edits ----

    public void Rename(TargetKind kind, int id, string? displayName)
    {
        if (kind == TargetKind.Folder) throw new ArgumentException("A folder has no display name to change.", nameof(kind));

        _overrides.SetDisplayName(kind, id, displayName);
        RaiseChanged();
    }

    public void SetHidden(TargetKind kind, int id, bool hidden)
    {
        _overrides.SetHidden(kind, id, hidden);
        RaiseChanged();
    }

    public void SetWatched(int fileId, bool watched)
    {
        RequireFile(fileId);
        _progress.SetWatched(_viewer.UserId, fileId, watched);
        NotifyProgressChanged();
    }

    public void SetTitleWatched(int titleId, IReadOnlyCollection<int> fileIds, bool watched) => MarkTitle(titleId, fileIds, watched);

    /// <summary>What <see cref="SetTitleWatched"/> does. A file that isn't this title's, or is already that way, is left alone.</summary>
    /// <returns>How many files changed.</returns>
    public int MarkTitle(int titleId, IReadOnlyCollection<int> fileIds, bool watched)
    {
        var snapshot = Snapshot();
        if (!snapshot.Titles.ContainsKey(titleId)) return 0;

        var named = fileIds.ToHashSet();
        var changing = snapshot.FilesByTitle[titleId].Where(f => named.Contains(f.Id) && f.Watched != watched).Select(f => f.Id).ToList();
        if (changing.Count == 0) return 0;

        _progress.SetWatched(_viewer.UserId, changing, watched);
        NotifyProgressChanged();
        return changing.Count;
    }

    public void MarkNotMedia(int fileId)
    {
        RequireFile(fileId);
        _matches.SetNotMedia(fileId);
        _titles.DeleteOrphanProvisional();
        RaiseChanged();
    }

    /// <summary>
    /// Everything TMDB has to say is fetched first and stored after, so a failed lookup leaves the file as it was.
    /// An episode gets a rule for its show folder and parsed title; the match, the rule and the other files of
    /// that show waiting in review or offline (set back to pending) are written in one transaction, and the next
    /// identification run applies the rule to those files. A movie, or a file outside a show folder, gets no rule.
    /// </summary>
    public async Task FixMatchAsync(int fileId, int tmdbId, TitleKind kind, CancellationToken ct)
    {
        var file = RequireFile(fileId);
        var parsed = file.Parsed;

        var details = await _provider.GetTitleAsync(tmdbId, kind, ct).ConfigureAwait(false);
        var seasons = new List<SeasonDetails>();
        IReadOnlyList<EpisodeDetails> episodes = [];
        if (kind == TitleKind.Show) (episodes, seasons) = await ResolveEpisodesAsync(parsed, details, ct).ConfigureAwait(false);

        var titleId = _titles.Upsert(details);
        foreach (var season in seasons) _titles.UpsertEpisodes(titleId, season);
        var episodeIds = _titles.EpisodeIds(titleId, episodes.Select(e => (e.Season, e.Number)));

        var rule = kind == TitleKind.Show && parsed.Kind == MediaKind.Episode
                   && !string.IsNullOrEmpty(parsed.ShowFolder) && !string.IsNullOrWhiteSpace(parsed.Title)
            ? new MatchRule(file.FolderId, parsed.ShowFolder, parsed.Title)
            : null;
        _matches.SetUserMatch(fileId, titleId, episodeIds, rule);

        _titles.DeleteOrphanProvisional();
        RaiseChanged();
    }

    /// <summary>
    /// The episodes the file holds in the chosen show, found the way identification finds them (titles and
    /// numbers, the other seasons for a release numbered differently; see <see cref="EpisodeResolver.ResolveAsync"/>).
    /// When that cannot decide, the numbers parsed from the name are used as they are; a title that names no episode
    /// TMDB has links none, not the one at the number. The seasons returned for
    /// storing are the file's own, the specials and those its episodes are in, not every season looked through.
    /// </summary>
    private async Task<(IReadOnlyList<EpisodeDetails> Episodes, List<SeasonDetails> Seasons)> ResolveEpisodesAsync(
        ParsedMedia parsed, TitleDetails show, CancellationToken ct)
    {
        var fetched = new List<SeasonDetails>();
        if (parsed.Episodes.Count == 0 && parsed.EpisodeTitles.Count == 0) return ([], fetched);

        async Task<SeasonDetails?> Season(int number)
        {
            var season = await TryGetSeasonAsync(show.TmdbId, number, ct).ConfigureAwait(false);
            if (season is not null) fetched.Add(season);
            return season;
        }

        var own = parsed.Season ?? 1;
        var resolved = await EpisodeResolver.ResolveAsync(parsed, Season, () => Task.FromResult(show.SeasonNumbers))
                           .ConfigureAwait(false)
                       ?? fetched.FirstOrDefault(s => s.Season == own)?.Episodes
                           .Where(e => parsed.Episodes.Contains(e.Number)).OrderBy(e => e.Number).ToList()
                       ?? [];
        var keep = fetched.Where(s => s.Season == own || s.Season == 0 || resolved.Any(e => e.Season == s.Season)).ToList();
        return (resolved, keep);
    }

    /// <summary>A season TMDB does not have is not an error: the file is matched without episodes.</summary>
    private async Task<SeasonDetails?> TryGetSeasonAsync(int tmdbId, int season, CancellationToken ct)
    {
        try
        {
            return await _provider.GetSeasonAsync(tmdbId, season, ct).ConfigureAwait(false);
        }
        catch (MetadataUnavailableException e) when (e.Reason == UnavailableReason.ServerError)
        {
            return null;
        }
    }

    // ---- Delete from disk ----

    public DeletePreview PreviewDelete(int titleId)
    {
        var snapshot = Snapshot();
        if (!snapshot.Titles.TryGetValue(titleId, out var title)) return new DeletePreview(titleId, "", []);

        var folders = snapshot.Folders.ToDictionary(f => f.Id, f => f.Path);
        var files = new List<DeleteFile>();
        foreach (var file in snapshot.FilesByTitle[titleId].Where(f => !f.Missing).OrderBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            if (InsideLibrary(folders, file.FolderId, file.RelPath) is { } path) files.Add(new DeleteFile(file.Id, path, file.Size, _driveTypeOf(path) != DriveType.Fixed));
        }

        return new DeletePreview(titleId, snapshot.TitleName(title), files);
    }

    /// <summary>
    /// The paths come from the database again, not from the preview, and a file that does not resolve to a place
    /// below one of the library folders is refused. A file that cannot be sent keeps its row; the rest are removed.
    /// </summary>
    public Task<DeleteResult> DeleteFromDiskAsync(DeletePreview preview) => Task.Run(() => DeleteFiles(preview));

    private DeleteResult DeleteFiles(DeletePreview preview)
    {
        var folders = _folders.All().ToDictionary(f => f.Id, f => f.Path);
        var deleted = new List<string>();
        var failed = new List<DeleteFailure>();
        var gone = new List<int>();

        foreach (var entry in preview.Files)
        {
            var file = _files.Get(entry.FileId);
            if (file is null)
            {
                failed.Add(new DeleteFailure(entry.Path, "The file is no longer in the library."));
                continue;
            }

            if (InsideLibrary(folders, file.FolderId, file.RelPath) is not { } path)
            {
                failed.Add(new DeleteFailure(entry.Path, "The file is not inside a library folder, so it was left alone."));
                continue;
            }

            try
            {
                if (!File.Exists(path))
                {
                    failed.Add(new DeleteFailure(path, "The file was not found on disk."));
                    continue;
                }

                var result = _sendToRecycleBin(path);
                switch (result.Outcome)
                {
                    case RecycleOutcome.Recycled:
                        deleted.Add(path);
                        gone.Add(file.Id);
                        break;
                    case RecycleOutcome.Declined:
                        failed.Add(new DeleteFailure(path, "Not deleted: this file cannot go to the Recycle Bin and you chose not to delete it for good.", Declined: true));
                        break;
                    default:
                        failed.Add(new DeleteFailure(path, result.Detail ?? "Windows could not move the file to the Recycle Bin."));
                        break;
                }
            }
            catch (Exception e)
            {
                failed.Add(new DeleteFailure(path, e.Message));
            }
        }

        if (gone.Count > 0)
        {
            _files.Remove(gone);
            _titles.DeleteOrphanProvisional();
            RaiseChanged();
        }

        return new DeleteResult(deleted, failed);
    }

    /// <summary>The file's full path, or null when it does not lie below its library folder (a ".." or rooted path).</summary>
    internal static string? InsideLibrary(Dictionary<int, string> folders, int folderId, string relPath)
    {
        if (!folders.TryGetValue(folderId, out var folder) || string.IsNullOrWhiteSpace(relPath) || Path.IsPathRooted(relPath)) return null;

        var full = Path.GetFullPath(Path.Combine(folder, relPath));
        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && full.Length > prefix.Length ? full : null;
    }

    // ---- Helpers ----

    /// <summary>UNC paths have no drive letter; they and anything Windows cannot classify count as not fixed.</summary>
    private static DriveType DriveTypeOf(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return DriveType.Unknown;
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return DriveType.Network;

        try
        {
            return new DriveInfo(root).DriveType;
        }
        catch (ArgumentException)
        {
            return DriveType.Unknown;
        }
    }

    // A file the viewer can't reach is as good as unknown to them.
    private MediaFileRow RequireFile(int fileId) =>
        _files.Get(fileId) is { } file && _viewer.CanReach(file.FolderId)
            ? file
            : throw new ArgumentException($"There is no file with id {fileId}.", nameof(fileId));

    private void RaiseChanged() => _shared.Raise();

    /// <summary>
    /// Another viewer's copy reads the library part of the snapshot from the shared base when there is one (see
    /// <c>shareSnapshots</c>). The owner's own instance never does: it is the one edits and tests write around.
    /// </summary>
    internal LibrarySnapshot Snapshot() => LibrarySnapshot.Load(_db, _viewer, _usesSharedBase ? _shared.Base : null);

    /// <inheritdoc />
    public int FilesBehindLinks()
    {
        var snapshot = Snapshot();
        var behindLinks = snapshot.Library.BehindLinks(new FolderLinks());
        return snapshot.Files.Count(f => !f.Hidden && behindLinks.Contains(f.Id));
    }

    /// <summary>Lets go of what the sharing server kept, for when it stops listening.</summary>
    public void ForgetSnapshot() => _shared.Base?.Invalidate();

    /// <summary>For <see cref="ViewerAccess"/>, so a check by id reads the same base the lists do.</summary>
    internal SharedLibraryBase? SharedBase => _shared.Base;

    /// <summary>What every viewer's copy of the library shares: the listeners and the count of changes that outdates a search index.</summary>
    private sealed class Shared(SharedLibraryBase? sharedBase)
    {
        private int _version;

        public SharedLibraryBase? Base { get; } = sharedBase;

        public event Action? Changed;

        public event Action? ContentChanged;

        public event Action<int>? ProgressChanged;

        public int Version => Volatile.Read(ref _version);

        public void Raise()
        {
            // The base goes first: a search that reads the new version must not be handed the old base.
            Base?.Invalidate();
            Interlocked.Increment(ref _version);
            Changed?.Invoke();
            ContentChanged?.Invoke();
        }

        // The base holds no progress, so it stays, and a viewer's edit is nothing to this PC's pages: left to reload here,
        // one viewer's clicks would cost every other viewer a full read of the library.
        public void RaiseProgress(int userId)
        {
            if (userId == LibraryDatabase.OwnerId)
            {
                Interlocked.Increment(ref _version);
                Changed?.Invoke();
            }

            ProgressChanged?.Invoke(userId);
        }
    }

    private sealed record ScanInfo(IReadOnlyList<string> IgnoredFolders, bool Unavailable);
}
