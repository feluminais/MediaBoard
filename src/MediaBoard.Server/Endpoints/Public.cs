using System.Globalization;
using System.Text;
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

    public static void Map(IEndpointRouteBuilder api, AccountService accounts, AccountStore store)
    {
        api.MapGet("ping", () => ApiResults.Ok(new PingReply(ApiVersion.Current)));

        api.MapPost("invites/redeem", async (HttpContext context) =>
        {
            var body = await ApiResults.ReadJson<RedeemRequest>(context);
            if (body is not { Code: { } code, DeviceName: { } sent } || CleanDeviceName(sent) is not { } deviceName)
                return ApiResults.Error(ApiErrorCode.BadRequest);

            var address = ClientAddress.Of(context);
            // A check waits in a gate and then hashes for a while, which mustn't hold a request thread.
            var result = await Task.Run(() => accounts.Redeem(code, body.Password, deviceName, address));
            return Answer(result.Issued, result.Failure, store);
        });

        api.MapPost("sign-in", async (HttpContext context) =>
        {
            var body = await ApiResults.ReadJson<SignInRequest>(context);
            if (body is not { Name: { } name, Password: { } password, DeviceName: { } sent } || CleanDeviceName(sent) is not { } deviceName)
                return ApiResults.Error(ApiErrorCode.BadRequest);

            var address = ClientAddress.Of(context);
            var result = await Task.Run(() => accounts.SignIn(name, password, deviceName, address));
            return Answer(result.Issued, result.Failure, store);
        });
    }

    public static ApiErrorCode CodeOf(RedeemFailure failure) => failure switch
    {
        RedeemFailure.InviteDead => ApiErrorCode.InviteDead,
        RedeemFailure.PasswordTooShort => ApiErrorCode.PasswordTooShort,
        RedeemFailure.Locked => ApiErrorCode.Locked,
        RedeemFailure.AccountDisabled => ApiErrorCode.Disabled,
        RedeemFailure.Busy => ApiErrorCode.Busy,
        _ => ApiErrorCode.WrongPassword,
    };

    // The name lands on the owner's page and in the log, and a caller wrote it: direction overrides could make it read as
    // something else.
    private static string? CleanDeviceName(string name)
    {
        var kept = string.Concat(name.EnumerateRunes().Where(IsPlainText)).Trim();
        return kept.Length is > 0 and <= MaxDeviceName ? kept : null;
    }

    private static bool IsPlainText(Rune rune) => Rune.GetUnicodeCategory(rune)
        is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);

    internal static IResult Answer(IssuedDevice? issued, RedeemFailure? failure, AccountStore store)
    {
        if (issued is null) return ApiResults.Error(CodeOf(failure!.Value));

        // The owner can remove the account in the moment since it was issued, and a device token is then no use to anyone.
        return store.Get(issued.Device.UserId) is { } account
            ? ApiResults.Ok(new SessionReply { Token = issued.Token, Name = account.Name })
            : ApiResults.Error(ApiErrorCode.Unauthorized);
    }
}
