using MediaBoard.Core.Library;
using MediaBoard.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MediaBoard.Server.Endpoints;

/// <summary>A file's details and its bytes. Both take an id, and an id the viewer can't reach is simply not found.</summary>
internal static class Playback
{
    public static void Map(IEndpointRouteBuilder api, SharedLibrary shared, StreamRegistry streams, StreamWatch watch)
    {
        api.MapGet("files/{id:int}", (HttpContext context, int id) =>
        {
            var viewer = DeviceAuth.Viewer(context).Viewer;
            if (Reachable(shared, viewer, id) is not { } file) return ApiResults.Error(ApiErrorCode.NotFound);

            // Spelled out field by field: ReachableFile holds the full path, which must never be serialised.
            return ApiResults.Ok(new FileReply(
                file.FileId, file.FileName, file.TitleName, file.EpisodeLabel, file.DurationS,
                shared.Progress.Get(viewer.UserId, id), file.Missing || !File.Exists(file.FullPath)));
        });

        api.MapGet("files/{id:int}/stream", (HttpContext context, int id) => Stream(context, id, shared, streams, watch));
    }

    private static IResult Stream(HttpContext context, int id, SharedLibrary shared, StreamRegistry streams, StreamWatch watch)
    {
        var who = DeviceAuth.Viewer(context);
        if (Reachable(shared, who.Viewer, id) is not { } file) return ApiResults.Error(ApiErrorCode.NotFound);

        FileStream source;
        try
        {
            source = FileStreamResult.OpenFile(file.FullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ApiResults.Error(FileStreamResult.ProblemWith(file.FullPath));
        }

        StreamLease lease;
        CancellationToken stop;
        try
        {
            lease = streams.Open(who, file, context.RequestAborted, out stop);
        }
        catch (StreamRefusedException e)
        {
            source.Dispose();
            return ApiResults.Error(e.Code);
        }

        // Access can change between the check above and the session appearing, where no re-check would see this stream.
        if (!watch.Reachable(who.Account.Id, id))
        {
            streams.Close(lease.Session);
            lease.Dispose();
            source.Dispose();
            return ApiResults.Error(ApiErrorCode.NotFound);
        }

        return new FileStreamResult(source, file.FullPath, streams, lease, stop);
    }

    // The saved progress has the length only once this viewer has played the file. The probed one covers the rest.
    private static ReachableFile? Reachable(SharedLibrary shared, Viewer viewer, int id)
    {
        if (shared.Access.File(viewer, id) is not { } file) return null;

        return file.DurationS is null ? file with { DurationS = shared.Files.Get(id)?.DurationS } : file;
    }
}
