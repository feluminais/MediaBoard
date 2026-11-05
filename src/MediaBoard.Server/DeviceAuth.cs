using MediaBoard.Core.Accounts;
using MediaBoard.Core.Library;
using MediaBoard.Core.Remote;
using Microsoft.AspNetCore.Http;

namespace MediaBoard.Server;

/// <summary>Who a request is from. <see cref="DeviceAuth"/> sets it before any protected handler runs.</summary>
public sealed record RequestViewer(Viewer Viewer, Account Account, Device Device);

/// <summary>
/// Lets through only a live device token of an enabled account. Every protected route inherits this filter from one
/// group, and it doubles as that group's metadata, which is how the access table tells a route is inside.
/// </summary>
internal sealed class DeviceAuth(DeviceStore devices, AccountStore accounts) : IEndpointFilter
{
    private const string Scheme = "Bearer ";

    public static RequestViewer Viewer(HttpContext context) => (RequestViewer)context.Items[typeof(RequestViewer)]!;

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (Identify(context.HttpContext) is not { } viewer)
            return ValueTask.FromResult<object?>(ApiResults.Error(ApiErrorCode.Unauthorized));

        context.HttpContext.Items[typeof(RequestViewer)] = viewer;
        return next(context);
    }

    private RequestViewer? Identify(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return null;

        if (devices.ByToken(header[Scheme.Length..].Trim()) is not { } device) return null;
        if (accounts.Get(device.UserId) is not { Disabled: false } account) return null;

        return new RequestViewer(accounts.ViewerOf(account), account, device);
    }
}
