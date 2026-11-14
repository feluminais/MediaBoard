namespace CozyCouch.Core.Metadata;

public enum UnavailableReason { Offline, NoKey, KeyRejected, ServerError }

/// <summary>
/// The metadata source could not answer. Messages never contain the API key or the request URL.
/// </summary>
public sealed class MetadataUnavailableException(UnavailableReason reason, string? message = null)
    : Exception(message ?? $"Metadata unavailable: {reason}.")
{
    public UnavailableReason Reason { get; } = reason;
}
