using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace CozyCouch.Server;

/// <summary>
/// The one form of a caller's address that the guard counts and the devices table stores. Comes from the connection
/// only: a forwarded header is whatever the caller wrote, and the server sits behind no proxy.
/// </summary>
internal static class ClientAddress
{
    public const string Unknown = "unknown";

    private const int InterfaceIdBytes = 8;

    public static string Of(HttpContext context) => Of(context.Connection.RemoteIpAddress);

    public static string Of(IPAddress? address)
    {
        if (address is null) return Unknown;
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4().ToString();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        // One home gets a whole /64, so a guesser could otherwise walk a fresh address for every guess.
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, bytes.Length - InterfaceIdBytes, InterfaceIdBytes);
        return new IPAddress(bytes) + "/64";
    }
}
