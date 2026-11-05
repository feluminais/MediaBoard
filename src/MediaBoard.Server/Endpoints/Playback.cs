using MediaBoard.Core.Accounts;
using MediaBoard.Core.Library;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MediaBoard.Server.Endpoints;

/// <summary>
/// A file's details, its bytes and where the viewer got to. All take an id, and an id the viewer can't reach is simply
/// not found. A body is read inside its handler, after <see cref="DeviceAuth"/>: bound as a parameter, it would be
/// refused with a 415 or a 400 before a stranger reached the token check.
/// </summary>
internal static class Playback
{
    public static void Map(IEndpointRouteBuilder api, SharedLibrary shared, StreamRegistry streams, StreamWatch watch, SharingLog log)
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

        api.MapGet("files/{id:int}/stream", (HttpContext context, int id) =>
        {
            return Stream(context, id, shared, streams, watch);
        });

        api.MapPut("files/{id:int}/progress", async (HttpContext context, int id) =>
        {
            var who = DeviceAuth.Viewer(context);
            if (shared.Access.File(who.Viewer, id) is not { } file) return ApiResults.Error(ApiErrorCode.NotFound);

            var entry = await ApiResults.ReadJson<ProgressEntry>(context);
            if (entry is null || !IsSound(entry)) return ApiResults.Error(ApiErrorCode.BadRequest);

            // Whether or not the entry is kept: a device whose clock runs slow is still playing, and a pause sends only this.
            streams.Heartbeat(who.Device.Id, id, entry.PositionS, entry.DurationS);
            try
            {
                var wasWatched = IsWatched(shared, who.Account.Id, id);
                if (shared.Progress.SaveIfNewer(who.Account.Id, id, entry) && entry.Watched == true && !wasWatched)
                    log.Add(SharingEvent.FinishedWatching, who.Account.Id, file.Title);

                return Results.NoContent();
            }
            catch (ArgumentException)
            {
                return ApiResults.Error(ApiErrorCode.NotFound);
            }
        });

        api.MapPut("files/{id:int}/watched", async (HttpContext context, int id) =>
        {
            var who = DeviceAuth.Viewer(context);
            var viewer = who.Viewer;
            if (shared.Access.File(viewer, id) is not { } file) return ApiResults.Error(ApiErrorCode.NotFound);

            var body = await ApiResults.ReadJson<WatchedRequest>(context);
            if (body is not { Watched: { } watched }) return ApiResults.Error(ApiErrorCode.BadRequest);

            try
            {
                var wasWatched = IsWatched(shared, who.Account.Id, id);
                shared.Owner.ForViewer(viewer).SetWatched(id, watched);
                if (watched && !wasWatched) log.Add(SharingEvent.FinishedWatching, who.Account.Id, file.Title);

                return Results.NoContent();
            }
            catch (Exception e) when (e is ArgumentException || ProgressStore.IsUnknownFile(e))
            {
                return ApiResults.Error(ApiErrorCode.NotFound);
            }
        });
    }

    private static bool IsWatched(SharedLibrary shared, int userId, int fileId) => shared.Progress.Get(userId, fileId)?.Watched == true;

    private static bool IsSound(ProgressEntry entry) =>
        double.IsFinite(entry.PositionS) && entry.PositionS >= 0
        && double.IsFinite(entry.DurationS) && entry.DurationS >= 0;

    private static IResult Stream(HttpContext context, int id, SharedLibrary shared, StreamRegistry streams, StreamWatch watch)
    {
        var who = DeviceAuth.Viewer(context);
        if (Reachable(shared, who.Viewer, id) is not { } file) return ApiResults.Error(ApiErrorCode.NotFound);

        FileStream? opened = null;
        long length;
        try
        {
            opened = FileStreamResult.OpenFile(file.FullPath);
            length = opened.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            opened?.Dispose();
            return ApiResults.Error(FileStreamResult.ProblemWith(file.FullPath));
        }

        var source = opened;

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

        var started = false;
        try
        {
            // A token can die or access change after the checks above but before the session appeared, where neither
            // an event nor a re-check would have found this stream.
            if (!watch.IsSignedIn(who.Device.Id)) return ApiResults.Error(ApiErrorCode.Unauthorized);
            if (!watch.Reachable(who.Account.Id, id)) return ApiResults.Error(ApiErrorCode.NotFound);

            started = true;
            return new FileStreamResult(source, length, file.FullPath, streams, lease, stop);
        }
        finally
        {
            if (!started)
            {
                streams.Close(lease.Session);
                lease.Dispose();
                source.Dispose();
            }
        }
    }

    // The saved progress has the length only once this viewer has played the file. The probed one covers the rest.
    private static ReachableFile? Reachable(SharedLibrary shared, Viewer viewer, int id)
    {
        if (shared.Access.File(viewer, id) is not { } file) return null;

        return file.DurationS is null ? file with { DurationS = shared.Files.Get(id)?.DurationS } : file;
    }
}
