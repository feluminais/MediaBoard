using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MediaBoard.Server;

/// <summary>
/// The server's self-signed certificate. Devices pin its fingerprint, so it stays the same until the owner replaces it.
/// The file is a PFX wrapped by DPAPI, which only this Windows user can open.
/// </summary>
public sealed class CertificateStore(string path, TimeProvider? time = null)
{
    private const int KeyBits = 2048;
    private const int ValidYears = 20;
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromDays(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<byte[], X509Certificate2> _open = Open;

    /// <summary>For tests: what <see cref="Replace"/> opens the new certificate with.</summary>
    internal CertificateStore(string path, Func<byte[], X509Certificate2> open)
        : this(path)
    {
        _open = open;
    }

    /// <exception cref="CryptographicException">
    /// The file can't be read, for example under another Windows user. A new certificate would cut off every device,
    /// so that stays the owner's call, through <see cref="Replace"/>.
    /// </exception>
    public X509Certificate2 LoadOrCreate() => File.Exists(path) ? Load(File.ReadAllBytes(path)) : Replace();

    public X509Certificate2 Replace()
    {
        var pfx = NewPfx();
        try
        {
            // Opened first: if the key store refuses it, the saved certificate is still the one devices pin.
            var certificate = _open(pfx);
            try
            {
                Save(ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser));
                return certificate;
            }
            catch
            {
                certificate.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    /// <summary>Lowercase hex SHA-256 of the certificate, as carried in an invite.</summary>
    public static string Fingerprint(X509Certificate2 cert) => cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();

    private static X509Certificate2 Load(byte[] protectedPfx)
    {
        var pfx = ProtectedData.Unprotect(protectedPfx, null, DataProtectionScope.CurrentUser);
        try
        {
            return Open(pfx);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    // TLS on Windows can't use an ephemeral key, so the key goes to the user's key store until the certificate is disposed.
    private static X509Certificate2 Open(byte[] pfx) => X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);

    private byte[] NewPfx()
    {
        using var key = RSA.Create(KeyBits);
        var request = new CertificateRequest("CN=MediaBoard", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], false));

        var now = _time.GetUtcNow();
        using var certificate = request.CreateSelfSigned(now - ClockSkew, now.AddYears(ValidYears));
        return certificate.Export(X509ContentType.Pfx);
    }

    private void Save(byte[] protectedPfx)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, protectedPfx);
        File.Move(temporary, path, overwrite: true);
    }
}
