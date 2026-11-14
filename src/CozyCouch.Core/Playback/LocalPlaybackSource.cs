using CozyCouch.Core.Library;
using CozyCouch.Core.Storage;

namespace CozyCouch.Core.Playback;

/// <summary>This PC's library and the owner's progress, as <see cref="PlaybackService"/> sees them.</summary>
public sealed class LocalPlaybackSource(FileStore files, ProgressStore progress) : IPlaybackSource, IProgressSink
{
    // Playback happens at this PC, so it is the owner who watches.
    private static readonly int OwnerId = Viewer.Owner.UserId;

    public Task<PlayTarget?> ResolveAsync(int fileId, CancellationToken ct)
    {
        var file = files.Get(fileId);
        if (file is null) return Task.FromResult<PlayTarget?>(null);

        var path = files.TryFullPath(file);
        // A file whose folder is gone can't be opened; the name is all the log needs.
        var target = path is null
            ? ToTarget(file, file.RelPath, missing: true)
            : ToTarget(file, path, missing: file.Missing || !File.Exists(path));
        return Task.FromResult<PlayTarget?>(target);
    }

    public PlayTarget? FindByLocator(string locator)
    {
        var file = files.FindByPath(locator);
        return file is null ? null : ToTarget(file, locator, file.Missing);
    }

    public void Save(int fileId, double positionS, double durationS) =>
        progress.Save(OwnerId, fileId, positionS, durationS);

    public void SetWatched(int fileId, bool watched) =>
        progress.SetWatched(OwnerId, fileId, watched);

    private PlayTarget ToTarget(MediaFileRow file, string locator, bool missing) =>
        new(file.Id, locator, TitleOf(file), file.DurationS, progress.Get(OwnerId, file.Id), missing);

    private static string TitleOf(MediaFileRow file) =>
        string.IsNullOrWhiteSpace(file.Parsed.Title) ? Path.GetFileNameWithoutExtension(file.RelPath) : file.Parsed.Title;
}
