using MediaBoard.Core.Storage;

namespace MediaBoard.Core.Remote;

/// <param name="FileId">The file Play or Resume starts; null when nothing can be played.</param>
public sealed record NextReply(int? FileId);

/// <param name="Watched">Null only in a body that left it out, which is refused.</param>
public sealed record WatchedRequest(bool? Watched);

/// <param name="Watched">Null only in a body that left it out, which is refused.</param>
/// <param name="FileIds">The title's files to mark. The server leaves alone any that aren't.</param>
public sealed record TitleWatchedRequest(bool? Watched, IReadOnlyList<int>? FileIds);

/// <summary>What a viewer's app needs to play a file. It carries no path: <see cref="FileName"/> is the name alone.</summary>
/// <param name="DurationS">Null until the file has been played or probed.</param>
/// <param name="Saved">This account's progress; null when it hasn't played the file.</param>
/// <param name="Missing">The file isn't on disk right now.</param>
public sealed record FileReply(int FileId, string FileName, string Title, string? EpisodeLabel, double? DurationS, Progress? Saved, bool Missing);
