using CozyCouch.Core.Accounts;
using CozyCouch.Core.Remote;
using CozyCouch.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CozyCouch.Server.Endpoints;

/// <summary>The signed-in device's own session.</summary>
internal static class Session
{
    public static void Map(IEndpointRouteBuilder api, AccountService service, AccountStore accounts)
    {
        api.MapGet("me", (HttpContext context) =>
            ApiResults.Ok(new MeReply(DeviceAuth.Viewer(context).Account.Name, accounts.Get(LibraryDatabase.OwnerId)!.Name)));

        api.MapPost("sign-out", (HttpContext context) =>
        {
            service.SignOutDevice(DeviceAuth.Viewer(context).Device.Id);
            return Results.NoContent();
        });

        api.MapPut("me/password", async (HttpContext context) =>
        {
            var body = await ApiResults.ReadJson<ChangePasswordRequest>(context);
            if (body is not { Current: { } current, Next: { } next }) return ApiResults.Error(ApiErrorCode.BadRequest);

            var who = DeviceAuth.Viewer(context);
            try
            {
                // A check waits in a gate and then hashes for a while, which mustn't hold a request thread.
                await Task.Run(() => service.ChangePassword(who.Account.Id, current, next, who.Device.Id));
                return Results.NoContent();
            }
            catch (PasswordRefusedException e)
            {
                return ApiResults.Error(Public.CodeOf(e.Failure));
            }
        });
    }
}
