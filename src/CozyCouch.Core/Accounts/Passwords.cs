using System.Globalization;
using System.Security.Cryptography;

namespace CozyCouch.Core.Accounts;

/// <summary>
/// PBKDF2-SHA256 with a salt per password. The work factor is stored in the hash, so raising
/// <see cref="Iterations"/> later leaves old hashes verifiable. Synchronous and slow on purpose: keep it off the UI thread.
/// </summary>
public static class Passwords
{
    public const int MinLength = 10;

    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static readonly byte[] DummySalt = new byte[SaltBytes];

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, Iterations, HashBytes);
        return string.Create(CultureInfo.InvariantCulture, $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>False for a wrong password and for a stored value that is not one of ours.</summary>
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts is not [Scheme, var iterationText, var saltText, var hashText]
            || !int.TryParse(iterationText, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations <= 0
            || !TryBase64(saltText, out var salt)
            || !TryBase64(hashText, out var expected)
            || expected.Length == 0)
            return false;

        var actual = Derive(password, salt, iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>The work of a failed <see cref="Verify"/>, so an unknown name costs the same time as a wrong password.</summary>
    public static void VerifyNothing(string password) => Derive(password, DummySalt, Iterations, HashBytes);

    private static byte[] Derive(string password, byte[] salt, int iterations, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length);

    private static bool TryBase64(string text, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
