namespace CozyCouch.Desktop;

/// <summary>How often AppHost looks for unavailable folders that came back, and the clock it uses (tests pass their own).</summary>
public sealed record FolderRecheck(TimeProvider Clock, TimeSpan Interval)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    public static FolderRecheck Default => new(TimeProvider.System, DefaultInterval);
}
