using System.Buffers.Text;
using System.Text;
using CozyCouch.Core.Accounts;

namespace CozyCouch.Core.Tests.Accounts;

public sealed class InviteStringTests
{
    private const string Fingerprint = "ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34";

    private static readonly InvitePayload Payload = new(
        "Andrii's library", ["192.168.1.20:47880", "media.example.net:47880"], Fingerprint, "the-code");

    private static string WithFingerprint(string json) => json.Replace("@fp", Fingerprint, StringComparison.Ordinal);

    private static string Wrap(string json) => "mb1-" + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(WithFingerprint(json)));

    [Fact]
    public void InviteString_AcceptsCompleteContent()
    {
        var text = Wrap("""{"serverName":"x","addresses":["a"],"fingerprint":"@fp","code":"c"}""");

        Assert.True(InviteString.TryDecode(text, out _));
    }

    private static void AssertSame(InvitePayload? payload)
    {
        Assert.NotNull(payload);
        Assert.Equal(Payload.ServerName, payload.ServerName);
        Assert.Equal(Payload.Addresses, payload.Addresses);
        Assert.Equal(Payload.Fingerprint, payload.Fingerprint);
        Assert.Equal(Payload.Code, payload.Code);
    }

    [Fact]
    public void InviteString_RoundTrips()
    {
        var text = InviteString.Encode(Payload);

        Assert.StartsWith("mb1-", text, StringComparison.Ordinal);
        Assert.True(InviteString.TryDecode(text, out var payload));
        AssertSame(payload);
    }

    [Fact]
    public void InviteString_RoundTrips_NonAsciiServerName()
    {
        var text = InviteString.Encode(Payload with { ServerName = "Бібліотека Андрія" });

        Assert.True(InviteString.TryDecode(text, out var payload));
        Assert.Equal("Бібліотека Андрія", payload!.ServerName);
    }

    [Fact]
    public void InviteString_Encode_UsesOnlyUrlSafeCharacters()
    {
        var text = InviteString.Encode(Payload with { ServerName = "???>>>~~~" });

        Assert.DoesNotContain('+', text);
        Assert.DoesNotContain('/', text);
        Assert.DoesNotContain('=', text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mb1-")]
    [InlineData("mb1-!!!not base64!!!")]
    [InlineData("hello world")]
    [InlineData("mb2-eyJjb2RlIjoiYSJ9")]
    public void InviteString_RejectsGarbage(string text)
    {
        Assert.False(InviteString.TryDecode(text, out var payload));
        Assert.Null(payload);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"serverName":"x","addresses":[],"fingerprint":"@fp","code":"c"}""")]
    [InlineData("""{"serverName":"x","addresses":[null],"fingerprint":"@fp","code":"c"}""")]
    [InlineData("""{"serverName":"x","addresses":["a"],"fingerprint":"","code":"c"}""")]
    [InlineData("""{"serverName":"x","addresses":["a"],"fingerprint":"@fp","code":""}""")]
    [InlineData("""{"serverName":"x","addresses":["a"],"fingerprint":"@fp"}""")]
    [InlineData("""{"serverName":null,"addresses":["a"],"fingerprint":"@fp","code":"c"}""")]
    [InlineData("""{"serverName":"x","addresses":"a","fingerprint":"@fp","code":"c"}""")]
    public void InviteString_RejectsBadContent(string json)
    {
        Assert.False(InviteString.TryDecode(Wrap(json), out var payload));
        Assert.Null(payload);
    }

    [Theory]
    [InlineData("ab12cd34")]
    [InlineData("ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd3")]
    [InlineData("ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd345")]
    [InlineData("gb12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34")]
    [InlineData("ab:2cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34")]
    [InlineData("аb12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34")]
    public void InviteString_RejectsAFingerprintThatIsNotSha256Hex(string fingerprint)
    {
        var text = InviteString.Encode(Payload with { Fingerprint = fingerprint });

        Assert.False(InviteString.TryDecode(text, out var payload));
        Assert.Null(payload);
    }

    [Fact]
    public void InviteString_RejectsInvalidUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes(WithFingerprint("""{"serverName":"x?","addresses":["a"],"fingerprint":"@fp","code":"c"}"""));
        bytes[Array.IndexOf(bytes, (byte)'?')] = 0xFF;

        Assert.False(InviteString.TryDecode("mb1-" + Base64Url.EncodeToString(bytes), out _));
    }

    [Fact]
    public void Payload_DoesNotPrintTheCode()
    {
        var text = Payload.ToString();

        Assert.DoesNotContain(Payload.Code, text, StringComparison.Ordinal);
        Assert.Contains(Payload.ServerName, text, StringComparison.Ordinal);
    }

    [Fact]
    public void InviteString_ToleratesPastedWhitespace()
    {
        var text = InviteString.Encode(Payload);
        var broken = "  \r\n" + text[..12] + "\r\n" + text[12..30] + " \t" + text[30..] + "\n ";

        Assert.True(InviteString.TryDecode(broken, out var payload));
        AssertSame(payload);
    }
}
