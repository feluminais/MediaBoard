namespace MediaBoard.Core.Remote;

/// <param name="Addresses">Host and port with no scheme, in the order to try them. IPv6 literals are in brackets.</param>
/// <param name="Fingerprint">The SHA-256 of the server's certificate in hex, which the client pins.</param>
public sealed record ServerEndpoint(string ServerName, IReadOnlyList<string> Addresses, string Fingerprint);
