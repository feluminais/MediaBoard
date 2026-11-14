using System.Diagnostics;
using CozyCouch.Core.Accounts;
using CozyCouch.Core.Library;
using CozyCouch.Core.Remote;
using Microsoft.AspNetCore.Http;

namespace CozyCouch.Server;

/// <summary>Who a request is from. <see cref="DeviceAuth"/> sets it before any protected handler runs.</summary>
public sealed record RequestViewer(Viewer Viewer, Account Account, Device Device);

/// <summary>
/// Lets through only a live device token of an enabled account. Every protected route inherits this filter from one
/// group, and it doubles as that group's metadata, which is how the access table tells a route is inside.
/// </summary>
internal sealed class DeviceAuth(DeviceStore devices, AccountStore accounts, SharingLog log, TimeProvider time) : IEndpointFilter
{
    private const string Scheme = "Bearer ";

    // A playing client reports every 10 s, and the Sharing page needs nothing finer than this.
    private static readonly TimeSpan NoteSeenEvery = TimeSpan.FromMinutes(1);

    // A forgotten or stolen device stops by itself, and coming back after this long takes only the password.
    private static readonly TimeSpan IdleLimit = TimeSpan.FromDays(90);

    public static RequestViewer Viewer(HttpContext context) => (RequestViewer)context.Items[typeof(RequestViewer)]!;

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (Identify(context.HttpContext) is not { } viewer)
            return ValueTask.FromResult<object?>(ApiResults.Error(ApiErrorCode.Unauthorized));

        NoteSeen(viewer, context.HttpContext);
        context.HttpContext.Items[typeof(RequestViewer)] = viewer;
        return next(context);
    }

    private void NoteSeen(RequestViewer viewer, HttpContext context)
    {
        var address = ClientAddress.Of(context);
        var moved = viewer.Device.LastAddress is { } last && last != address && address != ClientAddress.Unknown;
        if (!moved && time.GetUtcNow().UtcDateTime - viewer.Device.LastSeen < NoteSeenEvery) return;

        devices.Touch(viewer.Device.Id, address);
        accounts.Touch(viewer.Account.Id);
        if (moved) LogMoved(viewer, address);
    }

    private void Retire(Device device)
    {
        devices.Revoke(device.Id);
        try
        {
            log.AddOncePerDay(SharingEvent.SignedOut, device.UserId, device.Name);
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"Logging an expired device failed: {e.GetType().Name}");
        }
    }

    // A token used from somewhere else is the one sign the owner gets that it may have been copied. The request is fine
    // either way, so a log that can't be written doesn't fail it.
    private void LogMoved(RequestViewer viewer, string address)
    {
        try
        {
            log.AddOncePerDay(SharingEvent.NewAddress, viewer.Account.Id, $"{viewer.Device.Name} connected from {address}");
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"Logging a new address failed: {e.GetType().Name}");
        }
    }

    private RequestViewer? Identify(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return null;

        if (devices.ByToken(header[Scheme.Length..].Trim()) is not { } device) return null;
        if (time.GetUtcNow().UtcDateTime - (device.LastSeen ?? device.CreatedAt) >= IdleLimit)
        {
            Retire(device);
            return null;
        }

        if (accounts.Get(device.UserId) is not { Disabled: false } account) return null;

        return new RequestViewer(accounts.ViewerOf(account), account, device);
    }
}
