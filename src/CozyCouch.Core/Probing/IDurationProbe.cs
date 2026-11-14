namespace CozyCouch.Core.Probing;

public interface IDurationProbe
{
    /// <summary>Length of the media file in seconds, or null when it cannot be read.</summary>
    Task<double?> GetDurationSecondsAsync(string path, CancellationToken ct);
}
