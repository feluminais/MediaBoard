using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MediaBoard.Server.Endpoints;

/// <summary>
/// Search, TMDB details and posters. TMDB is asked with the owner's key on the viewer's behalf: the key stays in
/// <see cref="SharedLibrary.SearchFor"/>'s provider and no reply carries it.
/// </summary>
internal static class Search
{
    // Every request here goes out on the owner's key and connection. A person typing and opening titles stays far below
    // these, and one image miss adds a file to the owner's drive.
    private const int TmdbCallsPerMinute = 120;
    private const int ImageDownloadsPerHour = 1_000;

    public static void Map(IEndpointRouteBuilder api, SharedLibrary shared, TimeProvider time)
    {
        var tmdb = new RequestBudget(TmdbCallsPerMinute, TimeSpan.FromMinutes(1), time);
        var downloads = new RequestBudget(ImageDownloadsPerHour, TimeSpan.FromHours(1), time);

        api.MapGet("search/local", (HttpContext context) =>
            Library.Limited(context, SearchService.MaxLibraryResults, limit =>
                ApiResults.Ok(Library.ViewerLibrary(context, shared).SearchLocal(context.Request.Query["q"].ToString(), limit))));

        api.MapGet("search", async (HttpContext context) =>
        {
            if (!tmdb.TryTake(UserOf(context))) return ApiResults.Error(ApiErrorCode.Busy);

            var results = await SearchFor(context, shared).SearchAsync(context.Request.Query["q"].ToString(), context.RequestAborted);
            return ApiResults.Ok(results);
        });

        api.MapGet("online/{kind}/{tmdbId:int}", async (HttpContext context, string kind, int tmdbId) =>
        {
            if (KindOf(kind) is not { } titleKind) return ApiResults.Error(ApiErrorCode.NotFound);
            if (!tmdb.TryTake(UserOf(context))) return ApiResults.Error(ApiErrorCode.Busy);

            var details = await SearchFor(context, shared).GetOnlineDetailsAsync(tmdbId, titleKind, context.RequestAborted);
            return details is null ? ApiResults.Error(ApiErrorCode.NotFound) : ApiResults.Ok(details);
        });

        api.MapGet("online/tv/{tmdbId:int}/seasons/{season:int}", async (HttpContext context, int tmdbId, int season) =>
        {
            if (!tmdb.TryTake(UserOf(context))) return ApiResults.Error(ApiErrorCode.Busy);

            var details = await SearchFor(context, shared).GetOnlineSeasonAsync(tmdbId, season, context.RequestAborted);
            return details is null ? ApiResults.Error(ApiErrorCode.NotFound) : ApiResults.Ok(details);
        });

        api.MapGet("images/{width:int}/{name}", async (HttpContext context, int width, string name) =>
        {
            // Only a TMDB file name and a width the cache keeps. The cache turns the name into a path of its own.
            if (width is not (300 or 342 or 780) || !TmdbImageName.IsValid(name)) return ApiResults.Error(ApiErrorCode.NotFound);

            // What's on disk costs nothing. A miss is a download, so it needs room and a share of the budget.
            var cached = shared.Images.Has("/" + name, width);
            if (!cached && (!shared.Images.HasRoom() || !downloads.TryTake(UserOf(context)))) return ApiResults.Error(ApiErrorCode.NotFound);

            if (await shared.Images.GetAsync("/" + name, width, context.RequestAborted) is not { } path) return ApiResults.Error(ApiErrorCode.NotFound);

            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                return Results.Stream(stream, name.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ApiResults.Error(ApiErrorCode.NotFound);
            }
        });
    }

    private static int UserOf(HttpContext context) => DeviceAuth.Viewer(context).Account.Id;

    // Built for this viewer: its library and the viewer it filters by must be the same one.
    private static ISearchService SearchFor(HttpContext context, SharedLibrary shared) =>
        shared.SearchFor(DeviceAuth.Viewer(context).Viewer);

    private static TitleKind? KindOf(string kind) => kind switch
    {
        "movie" => TitleKind.Movie,
        "tv" => TitleKind.Show,
        _ => null,
    };

}
