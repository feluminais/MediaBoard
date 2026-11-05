using MediaBoard.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MediaBoard.Server.Endpoints;

internal static class Session
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("me", (HttpContext context) => ApiResults.Ok(new MeReply(DeviceAuth.Viewer(context).Account.Name)));
}
