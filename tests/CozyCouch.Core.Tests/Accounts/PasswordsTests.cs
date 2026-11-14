using System.Security.Cryptography;
using CozyCouch.Core.Accounts;

namespace CozyCouch.Core.Tests.Accounts;

public sealed class PasswordsTests
{
    [Fact]
    public void Hash_RoundTrips()
    {
        var stored = Passwords.Hash("correct horse battery");

        Assert.StartsWith("pbkdf2-sha256$600000$", stored, StringComparison.Ordinal);
        Assert.True(Passwords.Verify("correct horse battery", stored));
        Assert.False(Passwords.Verify("correct horse batterx", stored));
    }

    [Fact]
    public void Hash_IsSaltedDifferently()
    {
        Assert.NotEqual(Passwords.Hash("same password"), Passwords.Hash("same password"));
    }

    [Fact]
    public void Verify_KeepsWorkingAfterIterationsChange()
    {
        // A hash made when the work factor was lower, as an older version of the app would have stored it.
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2("old password!", salt, 1000, HashAlgorithmName.SHA256, 32);
        var stored = $"pbkdf2-sha256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

        Assert.True(Passwords.Verify("old password!", stored));
        Assert.False(Passwords.Verify("new password!", stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("pbkdf2-sha256$abc$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$0$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$1000$not base64!$AAAA")]
    [InlineData("pbkdf2-sha256$1000$AAAA$")]
    [InlineData("md5$1000$AAAA$AAAA")]
    public void Verify_RejectsAStoredValueThatIsNotOurs(string stored)
    {
        Assert.False(Passwords.Verify("anything at all", stored));
    }

    [Fact]
    public void VerifyNothing_DoesNotThrow() => Passwords.VerifyNothing("whatever");
}
