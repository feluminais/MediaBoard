using System.Security.Cryptography;
using System.Text;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Accounts;

public sealed class DeviceStoreTests : IDisposable
{
    private const string Address = "203.0.113.5";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly FakeClock _clock = new();
    private readonly AccountStore _accounts;
    private readonly DeviceStore _devices;
    private readonly int _anna;

    public DeviceStoreTests()
    {
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _accounts = new AccountStore(_db, _clock);
        _devices = new DeviceStore(_db, _clock);
        _anna = _accounts.Create("Anna", null).Id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private byte[] DatabaseBytes()
    {
        SqliteConnection.ClearAllPools();
        using var bytes = new MemoryStream();
        foreach (var path in Directory.GetFiles(_dir, "library.db*"))
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.CopyTo(bytes);
        }

        return bytes.ToArray();
    }

    private static bool Contains(byte[] haystack, string text) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    [Fact]
    public void Issue_Records_TheDevice()
    {
        var issued = _devices.Issue(_anna, "Laptop", Address);

        Assert.Equal(_anna, issued.Device.UserId);
        Assert.Equal("Laptop", issued.Device.Name);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, issued.Device.CreatedAt);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, issued.Device.LastSeen);
        Assert.Equal(Address, issued.Device.LastAddress);
    }

    [Fact]
    public void Issue_Gives256BitTokens_ThatDiffer()
    {
        var first = _devices.Issue(_anna, "Laptop", Address);
        var second = _devices.Issue(_anna, "Laptop", Address);

        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(32, Convert.FromBase64String(first.Token.Replace('-', '+').Replace('_', '/') + "=").Length);
    }

    [Fact]
    public void ByToken_FindsTheDevice()
    {
        var issued = _devices.Issue(_anna, "Laptop", Address);

        Assert.Equal(issued.Device, _devices.ByToken(issued.Token));
    }

    [Fact]
    public void IssuedDevice_DoesNotPrintTheToken()
    {
        var issued = _devices.Issue(_anna, "Laptop", Address);

        var text = issued.ToString();

        Assert.DoesNotContain(issued.Token, text, StringComparison.Ordinal);
        Assert.Contains("Laptop", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nothing-like-a-token")]
    public void ByToken_Unknown_IsNull(string token)
    {
        _devices.Issue(_anna, "Laptop", Address);

        Assert.Null(_devices.ByToken(token));
    }

    [Fact]
    public void Token_StoredOnlyAsHash()
    {
        var token = _devices.Issue(_anna, "Laptop", Address).Token;

        var bytes = DatabaseBytes();

        Assert.False(Contains(bytes, token));
        // The stored form is visible to this search, so the check above can fail.
        Assert.True(Contains(bytes, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))));
    }

    [Fact]
    public void Revoke_StopsToken()
    {
        var issued = _devices.Issue(_anna, "Laptop", Address);
        var other = _devices.Issue(_anna, "Phone", Address);

        _devices.Revoke(issued.Device.Id);

        Assert.Null(_devices.ByToken(issued.Token));
        Assert.NotNull(_devices.ByToken(other.Token));
    }

    [Fact]
    public void RevokeAll_StopsEveryTokenOfThatPerson()
    {
        var bert = _accounts.Create("Bert", null).Id;
        var first = _devices.Issue(_anna, "Laptop", Address);
        var second = _devices.Issue(_anna, "Phone", Address);
        var theirs = _devices.Issue(bert, "Laptop", Address);

        _devices.RevokeAll(_anna);

        Assert.Null(_devices.ByToken(first.Token));
        Assert.Null(_devices.ByToken(second.Token));
        Assert.NotNull(_devices.ByToken(theirs.Token));
    }

    [Fact]
    public void ForUser_ListsOnlyThatPerson()
    {
        var bert = _accounts.Create("Bert", null).Id;
        _devices.Issue(_anna, "Laptop", Address);
        _devices.Issue(_anna, "Phone", Address);
        _devices.Issue(bert, "Tablet", Address);

        Assert.Equal(["Laptop", "Phone"], _devices.ForUser(_anna).Select(d => d.Name));
    }

    [Fact]
    public void Touch_UpdatesLastSeenAndAddress()
    {
        var issued = _devices.Issue(_anna, "Laptop", Address);
        _clock.Advance(TimeSpan.FromHours(1));

        _devices.Touch(issued.Device.Id, "198.51.100.7");

        var device = _devices.ForUser(_anna).Single();
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, device.LastSeen);
        Assert.Equal("198.51.100.7", device.LastAddress);
    }

    [Fact]
    public void RemovingTheAccount_StopsItsTokens()
    {
        var issued = _devices.Issue(_anna, "Laptop", Address);

        _accounts.Remove(_anna);

        Assert.Null(_devices.ByToken(issued.Token));
    }
}
