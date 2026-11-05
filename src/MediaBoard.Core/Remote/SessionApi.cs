namespace MediaBoard.Core.Remote;

// The two requests and SessionReply are classes, not records: a record's ToString would print the password or token
// into any log that formats one.

public sealed record PingReply(int Version, string Name);

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

public sealed class SessionReply
{
    public required string Token { get; init; }
}

public sealed record MeReply(string Name);
