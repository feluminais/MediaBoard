namespace MediaBoard.Core.Remote;

/// <param name="FileId">The file Play or Resume starts; null when nothing can be played.</param>
public sealed record NextReply(int? FileId);
