namespace CozyCouch.Core.Remote;

/// <param name="Addresses">Host and port with no scheme, in the order to try them. IPv6 literals are in brackets.</param>
/// <param name="Fingerprint">The SHA-256 of the server's certificate in hex, which the client pins.</param>
public sealed record ServerEndpoint(string ServerName, IReadOnlyList<string> Addresses, string Fingerprint)
{
    /// <summary>A SHA-256 in hex.</summary>
    public const int FingerprintLength = 64;

    /// <summary>For text from an invite or a settings file, which promise nothing, and which a pin must be able to read.</summary>
    public static bool IsFingerprint(string? text) => text is { Length: FingerprintLength } && text.All(char.IsAsciiHexDigit);
}
