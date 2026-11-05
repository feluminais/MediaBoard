using System.Text.Json.Serialization;

namespace MediaBoard.Core.Remote;

// The requests and SessionReply are classes, not records: a record's ToString would print the password or token
// into any log that formats one.

/// <param name="Version">Required, because one left out would read as 0, a version mismatch that stops the retries.</param>
/// <param name="Name">The server's name.</param>
public sealed record PingReply([property: JsonRequired] int Version, string Name);

/// <summary>A null <see cref="Password"/> asks which form to show: see <c>AccountService.Redeem</c>.</summary>
public sealed class RedeemRequest
{
    public string? Code { get; init; }

    public string? Password { get; init; }

    public string? DeviceName { get; init; }
}

public sealed class SignInRequest
{
    public string? Name { get; init; }

    public string? Password { get; init; }

    public string? DeviceName { get; init; }
}

public sealed class ChangePasswordRequest
{
    public string? Current { get; init; }

    public string? Next { get; init; }
}

public sealed class SessionReply
{
    public required string Token { get; init; }
}

/// <param name="Name">This account's name.</param>
/// <param name="OwnerName">The owner's account name, which is who a viewer is told stopped a stream.</param>
public sealed record MeReply(string Name, string OwnerName);
