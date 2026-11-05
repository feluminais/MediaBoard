using MediaBoard.Core.Accounts;
using MediaBoard.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MediaBoard.Server.Endpoints;

/// <summary>The signed-in device's own session.</summary>
internal static class Session
{
    public static void Map(IEndpointRouteBuilder api, AccountService accounts)
    {
        api.MapGet("me", (HttpContext context) => ApiResults.Ok(new MeReply(DeviceAuth.Viewer(context).Account.Name)));

        api.MapPost("sign-out", (HttpContext context) =>
        {
            accounts.SignOutDevice(DeviceAuth.Viewer(context).Device.Id);
            return Results.NoContent();
        });

        api.MapPut("me/password", async (HttpContext context) =>
        {
            var body = await ApiResults.ReadJson<ChangePasswordRequest>(context);
            if (body is not { Current: { } current, Next: { } next }) return ApiResults.Error(ApiErrorCode.BadRequest);

            var userId = DeviceAuth.Viewer(context).Account.Id;
            try
            {
                // A check waits in a gate and then hashes for a while, which mustn't hold a request thread.
                await Task.Run(() => accounts.ChangePassword(userId, current, next));
                return Results.NoContent();
            }
            catch (PasswordRefusedException e)
            {
                return ApiResults.Error(Public.CodeOf(e.Failure));
            }
        });
    }
}
