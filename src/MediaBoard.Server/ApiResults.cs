using System.Text.Json;
using MediaBoard.Core.Remote;
using Microsoft.AspNetCore.Http;

namespace MediaBoard.Server;

internal static class ApiResults
{
    public static IResult Ok<T>(T value) => Results.Json(value, ApiJson.Options);

    public static IResult Error(ApiErrorCode code) => Results.Json(new ApiErrorBody(code), ApiJson.Options, statusCode: StatusOf(code));

    /// <returns>Null for a body that isn't the JSON of a <typeparamref name="T"/>.</returns>
    /// <exception cref="BadHttpRequestException">The body broke a limit. <see cref="SharingServer.MapTo"/> answers it.</exception>
    public static async Task<T?> ReadJson<T>(HttpContext context)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, ApiJson.Options, context.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 401 means only "this token is no good", which a client takes as being signed out. A wrong password isn't that.
    private static int StatusOf(ApiErrorCode code) => code switch
    {
        ApiErrorCode.NotFound => StatusCodes.Status404NotFound,
        ApiErrorCode.Unauthorized => StatusCodes.Status401Unauthorized,
        ApiErrorCode.InviteDead => StatusCodes.Status410Gone,
        ApiErrorCode.WrongPassword or ApiErrorCode.Disabled => StatusCodes.Status403Forbidden,
        ApiErrorCode.Locked => StatusCodes.Status429TooManyRequests,
        ApiErrorCode.TooLarge => StatusCodes.Status413PayloadTooLarge,
        _ => StatusCodes.Status400BadRequest,
    };
}
