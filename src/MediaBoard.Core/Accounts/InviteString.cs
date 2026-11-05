using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace MediaBoard.Core.Accounts;

/// <param name="Addresses">Where to reach the server, in the order to try them.</param>
/// <param name="Fingerprint">The SHA-256 of the server's certificate, which the client pins.</param>
public sealed record InvitePayload(string ServerName, IReadOnlyList<string> Addresses, string Fingerprint, string Code);

/// <summary>The one string a person passes on to invite someone: "mb1-" and the payload as base64url JSON.</summary>
public static class InviteString
{
    private const string Prefix = "mb1-";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Encode(InvitePayload payload) =>
        Prefix + Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload, Options));

    /// <summary>Takes pasted text as it comes, so spaces and line breaks picked up on the way are ignored.</summary>
    /// <returns>False unless the text is an invite with every field present.</returns>
    public static bool TryDecode(string text, [NotNullWhen(true)] out InvitePayload? payload)
    {
        payload = null;
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (!compact.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        InvitePayload? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<InvitePayload>(Base64Url.DecodeFromChars(compact.AsSpan(Prefix.Length)), Options);
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return false;
        }

        if (decoded is null || !IsComplete(decoded)) return false;
        payload = decoded;
        return true;
    }

    // The JSON comes from a stranger's paste, so the record's non-null types promise nothing yet.
    private static bool IsComplete(InvitePayload payload) =>
        !string.IsNullOrEmpty(payload.ServerName)
        && !string.IsNullOrEmpty(payload.Fingerprint)
        && !string.IsNullOrEmpty(payload.Code)
        && payload.Addresses is { Count: > 0 }
        && payload.Addresses.All(address => !string.IsNullOrEmpty(address));
}
