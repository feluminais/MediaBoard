using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MediaBoard.Core.Remote;

/// <param name="ProtectedToken">
/// The device token as <see cref="RemoteSession.Protect"/> left it, or null on a signed-out device, which keeps the rest
/// so its Connect screen can offer this server again.
/// </param>
public sealed record StoredConnection(ServerEndpoint Endpoint, string AccountName, string DeviceName, string? ProtectedToken)
{
    // The generated ToString would print the token into any log that formats this record.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Endpoint = {Endpoint}, AccountName = {AccountName}, DeviceName = {DeviceName}");
        return true;
    }
}

/// <summary>Keeps a device token under DPAPI, which only this Windows user on this PC can open.</summary>
public static class RemoteSession
{
    public static string Protect(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    /// <exception cref="CryptographicException">The blob came from another Windows user or PC.</exception>
    /// <exception cref="FormatException">The blob was never made by <see cref="Protect"/>.</exception>
    public static string Unprotect(string blob) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser));

    /// <summary>
    /// For the app's token function. A settings file brought from another PC can't be opened here. Null sends no token,
    /// the server answers that with 401, and the device lands on the signed-out path with its server still saved.
    /// </summary>
    public static string? TryUnprotect(string? blob)
    {
        if (blob is null) return null;

        try
        {
            return Unprotect(blob);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
