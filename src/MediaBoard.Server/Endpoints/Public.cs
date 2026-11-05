using MediaBoard.Core.Accounts;
using MediaBoard.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MediaBoard.Server.Endpoints;

/// <summary>The three routes that need no token.</summary>
internal static class Public
{
    private const int MaxDeviceName = 64;

    public static void Map(IEndpointRouteBuilder api, SharingServer server, AccountService accounts)
    {
        api.MapGet("ping", () => ApiResults.Ok(new PingReply(ApiVersion.Current, server.ServerName)));

        api.MapPost("invites/redeem", async (HttpContext context) =>
        {
            var body = await ApiResults.ReadJson<RedeemRequest>(context);
            if (body is not { Code: { } code, DeviceName: { } deviceName } || !IsDeviceName(deviceName))
                return ApiResults.Error(ApiErrorCode.BadRequest);

            var address = ClientAddress.Of(context);
            // A check waits in a gate and then hashes for a while, which mustn't hold a request thread.
            var result = await Task.Run(() => accounts.Redeem(code, body.Password, deviceName, address));
            return Answer(result.Issued, result.Failure);
        });

        api.MapPost("sign-in", async (HttpContext context) =>
        {
            var body = await ApiResults.ReadJson<SignInRequest>(context);
            if (body is not { Name: { } name, Password: { } password, DeviceName: { } deviceName } || !IsDeviceName(deviceName))
                return ApiResults.Error(ApiErrorCode.BadRequest);

            var address = ClientAddress.Of(context);
            var result = await Task.Run(() => accounts.SignIn(name, password, deviceName, address));
            return Answer(result.Issued, result.Failure);
        });
    }

    private static bool IsDeviceName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= MaxDeviceName;

    private static IResult Answer(IssuedDevice? issued, RedeemFailure? failure) =>
        issued is not null ? ApiResults.Ok(new SessionReply { Token = issued.Token }) : ApiResults.Error(CodeOf(failure!.Value));

    private static ApiErrorCode CodeOf(RedeemFailure failure) => failure switch
    {
        RedeemFailure.InviteDead => ApiErrorCode.InviteDead,
        RedeemFailure.PasswordTooShort => ApiErrorCode.PasswordTooShort,
        RedeemFailure.Locked => ApiErrorCode.Locked,
        RedeemFailure.AccountDisabled => ApiErrorCode.Disabled,
        _ => ApiErrorCode.WrongPassword,
    };
}
