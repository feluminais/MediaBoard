using System.Globalization;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MediaBoard.Server.Endpoints;

/// <summary>
/// The lists and the title page, all read through the viewer's own <see cref="LocalLibrary"/>. Nothing here takes a
/// path or calls an edit, and <c>ILibrary.Folders</c> and <c>NeedsReview</c> have no route because they carry paths.
/// </summary>
internal static class Library
{
    public static void Map(IEndpointRouteBuilder api, SharedLibrary shared)
    {
        api.MapGet("continue", (HttpContext context) =>
            Limited(context, int.MaxValue, limit => ApiResults.Ok(ViewerLibrary(context, shared).ContinueWatching(limit))));

        api.MapPost("continue/{titleId:int}/dismiss", (HttpContext context, int titleId) =>
        {
            var viewer = DeviceAuth.Viewer(context).Viewer;
            if (!shared.Access.CanReachTitle(viewer, titleId)) return ApiResults.Error(ApiErrorCode.NotFound);

            shared.Owner.ForViewer(viewer).DismissContinue(titleId);
            return Results.NoContent();
        });

        api.MapGet("recent", (HttpContext context) =>
            Limited(context, int.MaxValue, limit => ApiResults.Ok(ViewerLibrary(context, shared).RecentlyAdded(limit))));

        // includeHidden is not read: hidden titles never leave this PC, owner or not.
        api.MapGet("titles", (HttpContext context) =>
        {
            if (!TryEnum<TitleKind>(context, "kind", fallback: null, out var kind)) return ApiResults.Error(ApiErrorCode.BadRequest);
            if (!TryEnum<TitleSort>(context, "sort", TitleSort.Name, out var sort)) return ApiResults.Error(ApiErrorCode.BadRequest);

            return ApiResults.Ok(ViewerLibrary(context, shared).Titles(kind, sort, includeHidden: false));
        });

        api.MapGet("titles/{id:int}", (HttpContext context, int id) =>
        {
            if (ViewerLibrary(context, shared).Title(id) is not { } title) return ApiResults.Error(ApiErrorCode.NotFound);

            // A relative path names the folders above the file, which are the owner's business.
            return ApiResults.Ok(title with { Files = [.. title.Files.Select(f => f with { RelPath = Path.GetFileName(f.RelPath) })] });
        });

        api.MapGet("titles/{id:int}/next", (HttpContext context, int id) =>
        {
            var viewer = DeviceAuth.Viewer(context).Viewer;
            if (!shared.Access.CanReachTitle(viewer, id)) return ApiResults.Error(ApiErrorCode.NotFound);

            return ApiResults.Ok(new NextReply(shared.Owner.ForViewer(viewer).NextEpisodeFileId(id)));
        });
    }

    internal static LocalLibrary ViewerLibrary(HttpContext context, SharedLibrary shared) =>
        shared.Owner.ForViewer(DeviceAuth.Viewer(context).Viewer);

    /// <summary>Answers 400 for a <c>limit</c> that isn't a positive number; <paramref name="fallback"/> stands for none.</summary>
    internal static IResult Limited(HttpContext context, int fallback, Func<int, IResult> answer)
    {
        var text = context.Request.Query["limit"].ToString();
        if (text.Length == 0) return answer(fallback);

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit > 0
            ? answer(limit)
            : ApiResults.Error(ApiErrorCode.BadRequest);
    }

    // Only the names count: Enum.TryParse would take "7" too.
    private static bool TryEnum<T>(HttpContext context, string key, T? fallback, out T value)
        where T : struct, Enum
    {
        value = default;
        var text = context.Request.Query[key].ToString();
        if (text.Length == 0)
        {
            if (fallback is not { } given) return false;

            value = given;
            return true;
        }

        var name = Enum.GetNames<T>().FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
        if (name is null) return false;

        value = Enum.Parse<T>(name);
        return true;
    }
}
