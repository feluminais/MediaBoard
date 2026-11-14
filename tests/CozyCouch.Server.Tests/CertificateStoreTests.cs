using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CozyCouch.Server.Tests;

public sealed class CertificateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cozycouch-test-" + Guid.NewGuid().ToString("N"));

    private string CertificatePath => Path.Combine(_dir, "certificate.bin");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void CertificateStore_ReusesTheSameCertificate()
    {
        using var first = new CertificateStore(CertificatePath).LoadOrCreate();
        using var second = new CertificateStore(CertificatePath).LoadOrCreate();

        Assert.Equal(CertificateStore.Fingerprint(first), CertificateStore.Fingerprint(second));
        Assert.True(second.HasPrivateKey);
    }

    [Fact]
    public void CertificateStore_KeyIsDpapiProtected()
    {
        using var certificate = new CertificateStore(CertificatePath).LoadOrCreate();

        var onDisk = File.ReadAllBytes(CertificatePath);

        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(onDisk, null));
        using var unwrapped = X509CertificateLoader.LoadPkcs12(
            ProtectedData.Unprotect(onDisk, null, DataProtectionScope.CurrentUser), null);
        Assert.True(unwrapped.HasPrivateKey);
        Assert.Equal(CertificateStore.Fingerprint(certificate), CertificateStore.Fingerprint(unwrapped));
    }

    [Fact]
    public void CertificateStore_ValidFor20Years()
    {
        var before = DateTimeOffset.UtcNow;

        using var certificate = new CertificateStore(CertificatePath).LoadOrCreate();

        Assert.True(certificate.NotBefore.ToUniversalTime() <= before.UtcDateTime);
        Assert.InRange(certificate.NotAfter.ToUniversalTime(), before.AddYears(20).AddMinutes(-1).UtcDateTime, before.AddYears(20).AddMinutes(1).UtcDateTime);
    }

    [Fact]
    public void CertificateStore_UsesRsa2048AndServerAuth()
    {
        using var certificate = new CertificateStore(CertificatePath).LoadOrCreate();

        using var key = certificate.GetRSAPrivateKey();
        Assert.Equal(2048, key!.KeySize);
        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages;
        Assert.Contains(usages.Cast<Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public void CertificateStore_Replace_GivesANewCertificateThatStays()
    {
        var store = new CertificateStore(CertificatePath);
        using var first = store.LoadOrCreate();

        using var replaced = store.Replace();
        using var reloaded = store.LoadOrCreate();

        Assert.NotEqual(CertificateStore.Fingerprint(first), CertificateStore.Fingerprint(replaced));
        Assert.Equal(CertificateStore.Fingerprint(replaced), CertificateStore.Fingerprint(reloaded));
    }

    [Fact]
    public void CertificateStore_Replace_OpeningFails_KeepsTheOldCertificate()
    {
        using var first = new CertificateStore(CertificatePath).LoadOrCreate();
        var onDisk = File.ReadAllBytes(CertificatePath);
        var store = new CertificateStore(CertificatePath, _ => throw new CryptographicException("no key store"));

        // The new certificate is opened before it is saved, so a failure there must not touch the file.
        Assert.Throws<CryptographicException>(store.Replace);

        Assert.Equal(onDisk, File.ReadAllBytes(CertificatePath));
        using var reloaded = new CertificateStore(CertificatePath).LoadOrCreate();
        Assert.Equal(CertificateStore.Fingerprint(first), CertificateStore.Fingerprint(reloaded));
    }

    [Fact]
    public void CertificateStore_Replace_SavingFails_DisposesTheOpenedCertificate()
    {
        using var first = new CertificateStore(CertificatePath).LoadOrCreate();
        var onDisk = File.ReadAllBytes(CertificatePath);
        // A directory where the temporary file goes makes the save fail.
        Directory.CreateDirectory(CertificatePath + ".tmp");
        X509Certificate2? opened = null;
        var store = new CertificateStore(CertificatePath, pfx =>
        {
            opened = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
            return opened;
        });

        Assert.Throws<UnauthorizedAccessException>(store.Replace);

        Assert.NotNull(opened);
        Assert.Equal(IntPtr.Zero, opened.Handle);
        Assert.Equal(onDisk, File.ReadAllBytes(CertificatePath));
    }

    [Fact]
    public void CertificateStore_UnreadableFile_ThrowsAndIsKept()
    {
        Directory.CreateDirectory(_dir);
        byte[] garbage = [1, 2, 3, 4];
        File.WriteAllBytes(CertificatePath, garbage);

        // A silent new certificate would cut every device off, which the Sharing page must warn about first.
        Assert.ThrowsAny<CryptographicException>(() => new CertificateStore(CertificatePath).LoadOrCreate());

        Assert.Equal(garbage, File.ReadAllBytes(CertificatePath));
    }

    [Fact]
    public void Fingerprint_IsLowercaseSha256Hex()
    {
        using var certificate = new CertificateStore(CertificatePath).LoadOrCreate();

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), CertificateStore.Fingerprint(certificate));
    }
}
