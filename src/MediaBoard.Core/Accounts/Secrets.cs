using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MediaBoard.Core.Accounts;

/// <summary>
/// Random values that are shown once and kept only as a hash. Plain SHA-256 is enough here, unlike for passwords:
/// the input is already too random to guess, and every lookup hashes again, so it must stay cheap.
/// </summary>
internal static class Secrets
{
    public static string New(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
