using CozyCouch.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CozyCouch.Server.Endpoints;

/// <summary>The one long request a client keeps open. Its end is how the client learns the server has gone.</summary>
internal static class Events
{
    /// <param name="api">The authenticated group.</param>
    /// <param name="hub">What the connections listen to.</param>
    /// <param name="watch">Answers whether a token is still alive.</param>
    /// <param name="stopping">Fires when the server stops. Kestrel would wait for an open request until its timeout.</param>
    public static void Map(IEndpointRouteBuilder api, EventHub hub, StreamWatch watch, CancellationToken stopping)
    {
        api.MapGet("events", async (HttpContext context) =>
        {
            var who = DeviceAuth.Viewer(context);
            using var listener = hub.Join(who);

            // A token can die after DeviceAuth but before the join, where its signed-out found no listener.
            if (!watch.IsSignedIn(who.Device.Id)) return ApiResults.Error(ApiErrorCode.Unauthorized);

            // Runs at once if the server began stopping while this request was on its way in.
            await using var stop = stopping.Register(context.Abort);
            await Send(listener, context);
            return Results.Empty;
        });
    }

    private static async Task Send(EventHub.Listener listener, HttpContext context)
    {
        var response = context.Response;
        var aborted = context.RequestAborted;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";

        try
        {
            // The headers go now: nothing else may be sent for a while, and the client waits on them to know it's connected.
            await response.Body.FlushAsync(aborted);
            await foreach (var frame in listener.Frames.ReadAllAsync(aborted))
            {
                await response.WriteAsync(frame, aborted);
                await response.Body.FlushAsync(aborted);
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // The client left or the server is stopping. Either is the normal end of this request.
        }
    }
}
