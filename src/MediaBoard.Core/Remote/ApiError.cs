using System.Text.Json.Serialization;

namespace MediaBoard.Core.Remote;

// Sent by name, so a code keeps its meaning if the list changes.
[JsonConverter(typeof(JsonStringEnumConverter<ApiErrorCode>))]
public enum ApiErrorCode
{
    NotFound,
    Unauthorized,
    InviteDead,
    WrongPassword,
    Locked,
    Disabled,
    PasswordTooShort,
    FileMissing,
    DriveUnavailable,
    StreamStopped,
    TooManyStreams,
    TooLarge,
    VersionMismatch,
    BadRequest,
}

public sealed record ApiErrorBody(ApiErrorCode Code);

public static class ApiVersion
{
    public const int Current = 1;
}
