using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Remote;
using MediaBoard.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace MediaBoard.Server.Tests;

public sealed class PublicEndpointTests
{
    private const string Ping = "/api/v1/ping";
    private const string Redeem = "/api/v1/invites/redeem";
    private const string SignIn = "/api/v1/sign-in";
    private const int MaxBody = 65536;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ping_ReturnsOnlyTheVersion_WithoutToken()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();

        var body = await fixture.Client.GetStringAsync(new Uri(Ping, UriKind.RelativeOrAbsolute), Ct);

        // The default server name holds the owner's Windows user name, and a scanner gets this answer with no token.
        Assert.Equal($$"""{"version":{{ApiVersion.Current}}}""", body);
    }

    [Fact]
    public async Task Redeem_KeepsControlAndDirectionCharactersOutOfTheDeviceName()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var person = fixture.NewPerson("Anna");

        var response = await Post(fixture, Redeem, new { code = fixture.Invites.Create(person.Id), password = ServerFixture.Password, deviceName = "Pho\u202Ene\u2028\U000E0001\n" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Phone", Assert.Single(fixture.Devices.ForUser(person.Id)).Name);
    }

    [Theory]
    [InlineData(RedeemFailure.Busy, ApiErrorCode.Busy)]
    [InlineData(RedeemFailure.Locked, ApiErrorCode.Locked)]
    public void CodeOf_KeepsBusyApartFromAWrongPassword(RedeemFailure failure, ApiErrorCode code) =>
        Assert.Equal(code, Endpoints.Public.CodeOf(failure));

    [Fact]
    public async Task Redeem_ADeviceNameOfOnlySuchCharacters_IsRefused()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var person = fixture.NewPerson("Anna");

        var response = await Post(fixture, Redeem, new { code = fixture.Invites.Create(person.Id), password = ServerFixture.Password, deviceName = "\u202E\n" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The owner can remove the account between the device being issued and the answer, which no request can line up.
    [Fact]
    public async Task Answer_AccountRemovedOnceIssued_IsUnauthorized()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var person = fixture.NewPerson();
        var issued = fixture.Join(person);
        fixture.Service.Remove(person.Id);

        var answer = Assert.IsAssignableFrom<IStatusCodeHttpResult>(Public.Answer(issued, null, fixture.Accounts));

        Assert.Equal(StatusCodes.Status401Unauthorized, answer.StatusCode);
        Assert.Equal(ApiErrorCode.Unauthorized, Assert.IsAssignableFrom<IValueHttpResult<ApiErrorBody>>(answer).Value?.Code);
    }

    [Fact]
    public async Task Redeem_Works()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var person = fixture.NewPerson();

        var response = await Post(fixture, Redeem, new { code = fixture.Invites.Create(person.Id), password = ServerFixture.Password, deviceName = "Phone" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var device = fixture.Devices.ByToken(await TokenOf(response));
        Assert.Equal(person.Id, device!.UserId);
        Assert.Equal("Phone", device.Name);
    }

    [Fact]
    public async Task Redeem_KnownAccount_WithoutPassword_AsksForIt_AndKeepsTheInvite()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var person = fixture.NewPerson();
        fixture.Join(person);
        var code = fixture.Invites.Create(person.Id);

        var response = await Post(fixture, Redeem, new { code, deviceName = "Phone" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCode.WrongPassword, await ServerFixture.CodeOf(response));
        Assert.NotNull(fixture.Invites.Peek(code));
    }

    [Fact]
    public async Task Redeem_DeadInvite_IsCoded()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();

        var response = await Post(fixture, Redeem, new { code = "no-such-code", password = ServerFixture.Password, deviceName = "Phone" });

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal(ApiErrorCode.InviteDead, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task Redeem_ShortPassword_IsCoded()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var code = fixture.Invites.Create(fixture.NewPerson().Id);

        var response = await Post(fixture, Redeem, new { code, password = "short", deviceName = "Phone" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCode.PasswordTooShort, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task SignIn_Works()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var person = fixture.NewPerson();
        fixture.Join(person);

        var response = await Post(fixture, SignIn, new { name = person.Name, password = ServerFixture.Password, deviceName = "Phone" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Phone", fixture.Devices.ByToken(await TokenOf(response))!.Name);
    }

    [Fact]
    public async Task SignIn_LockedAnswersLocked()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var person = fixture.NewPerson();
        fixture.Join(person);
        for (var i = 0; i < 5; i++) fixture.Guard.RecordFailure(person.Name, null);

        var response = await Post(fixture, SignIn, new { name = person.Name, password = ServerFixture.Password, deviceName = "Phone" });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(ApiErrorCode.Locked, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task SignIn_DisabledAccount_AnswersDisabled()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var person = fixture.NewPerson();
        fixture.Join(person);
        fixture.Service.Disable(person.Id, true);

        var response = await Post(fixture, SignIn, new { name = person.Name, password = ServerFixture.Password, deviceName = "Phone" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCode.Disabled, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task SignIn_UnknownAndWrongAreIdentical()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var person = fixture.NewPerson();
        fixture.Join(person);

        var wrong = await Post(fixture, SignIn, new { name = person.Name, password = "not the password", deviceName = "Phone" });
        var unknown = await Post(fixture, SignIn, new { name = "Nobody", password = "not the password", deviceName = "Phone" });

        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(wrong.StatusCode, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(Ct), await unknown.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ApiErrorCode.WrongPassword, await ServerFixture.CodeOf(unknown));
    }

    [Theory]
    [InlineData("""{"password":"correct horse battery","deviceName":"Phone"}""")]
    [InlineData("""{"name":"Anna","deviceName":"Phone"}""")]
    [InlineData("""{"name":"Anna","password":"correct horse battery"}""")]
    [InlineData("""{"name":null,"password":"correct horse battery","deviceName":"Phone"}""")]
    [InlineData("""{"name":"Anna","password":null,"deviceName":"Phone"}""")]
    [InlineData("""{"name":"Anna","password":"correct horse battery","deviceName":null}""")]
    [InlineData("""{"name":"Anna","password":"correct horse battery","deviceName":"   "}""")]
    public async Task SignIn_MissingField_IsCoded(string json)
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();

        await AssertBadRequest(await PostRaw(fixture, SignIn, json));
    }

    [Theory]
    [InlineData("""{"password":"correct horse battery","deviceName":"Phone"}""")]
    [InlineData("""{"code":"abc"}""")]
    [InlineData("""{"code":null,"deviceName":"Phone"}""")]
    [InlineData("""{"code":"abc","deviceName":null}""")]
    public async Task Redeem_MissingField_IsCoded(string json)
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();

        await AssertBadRequest(await PostRaw(fixture, Redeem, json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"name":""")]
    [InlineData("""{"name":5,"password":"x","deviceName":"y"}""")]
    [InlineData("""{"name":"\ud800","password":"x","deviceName":"y"}""")]
    public async Task MalformedJson_IsCoded(string json)
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();

        await AssertBadRequest(await PostRaw(fixture, SignIn, json));
        await AssertBadRequest(await PostRaw(fixture, Redeem, json));
    }

    [Fact]
    public async Task InvalidUtf8_IsCoded()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        byte[] json = [.. "{\"name\":\""u8, 0xFF, 0xFE, .. "\"}"u8];

        await AssertBadRequest(await fixture.Client.PostAsync(SignIn, new ByteArrayContent(json), Ct));
    }

    [Fact]
    public async Task DeviceName_Over64Chars_IsRefused_BeforeAnythingIsChecked()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var person = fixture.NewPerson();
        var code = fixture.Invites.Create(person.Id);
        var longName = new string('x', 65);

        await AssertBadRequest(await Post(fixture, Redeem, new { code, password = ServerFixture.Password, deviceName = longName }));
        await AssertBadRequest(await Post(fixture, SignIn, new { name = person.Name, password = ServerFixture.Password, deviceName = longName }));

        Assert.NotNull(fixture.Invites.Peek(code));
    }

    [Fact]
    public async Task DeviceName_Of64Chars_IsAccepted()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var name = new string('x', 64);

        var response = await Post(fixture, Redeem, new { code = fixture.Invites.Create(fixture.NewPerson().Id), password = ServerFixture.Password, deviceName = name });

        Assert.Equal(name, fixture.Devices.ByToken(await TokenOf(response))!.Name);
    }

    [Fact]
    public async Task Body_Over64Kb_Rejected()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();

        var response = await PostRaw(fixture, SignIn, new string(' ', MaxBody + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ApiErrorCode.TooLarge, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task Body_Of64Kb_IsRead()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();

        var response = await PostRaw(fixture, SignIn, """{"name":"Nobody","password":"not the password","deviceName":"Phone"}""".PadRight(MaxBody));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public async Task Address_ReachesTheAccountNormalised(string remote, string expected)
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        fixture.RemoteAddress = IPAddress.Parse(remote);

        var response = await Post(fixture, Redeem, new { code = fixture.Invites.Create(fixture.NewPerson().Id), password = ServerFixture.Password, deviceName = "Phone" });

        Assert.Equal(expected, fixture.Devices.ByToken(await TokenOf(response))!.LastAddress);
    }

    [Theory]
    [InlineData("203.0.113.7", "::ffff:203.0.113.7")]
    [InlineData("2001:db8:1:2::/64", "2001:db8:1:2:ffff::9")]
    public async Task AddressLock_CoversEveryFormOfTheAddress(string lockedAddress, string remote)
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        for (var i = 0; i < 20; i++) fixture.Guard.RecordFailure(null, lockedAddress);
        fixture.RemoteAddress = IPAddress.Parse(remote);

        var response = await Post(fixture, SignIn, new { name = "Nobody", password = "not the password", deviceName = "Phone" });

        Assert.Equal(ApiErrorCode.Locked, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task AddressLock_LeavesTheNext64Alone()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        for (var i = 0; i < 20; i++) fixture.Guard.RecordFailure(null, "2001:db8:1:2::/64");
        fixture.RemoteAddress = IPAddress.Parse("2001:db8:1:3::9");

        var response = await Post(fixture, SignIn, new { name = "Nobody", password = "not the password", deviceName = "Phone" });

        Assert.Equal(ApiErrorCode.WrongPassword, await ServerFixture.CodeOf(response));
    }

    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Real-IP")]
    [InlineData("Forwarded")]
    public async Task Address_IgnoresForwardedHeaders(string header)
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        fixture.RemoteAddress = IPAddress.Parse("203.0.113.7");
        var body = new { code = fixture.Invites.Create(fixture.NewPerson().Id), password = ServerFixture.Password, deviceName = "Phone" };
        using var request = new HttpRequestMessage(HttpMethod.Post, Redeem) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation(header, "198.51.100.99");

        var response = await fixture.Client.SendAsync(request, Ct);

        Assert.Equal("203.0.113.7", fixture.Devices.ByToken(await TokenOf(response))!.LastAddress);
    }

    private static Task<HttpResponseMessage> Post(ServerFixture fixture, string route, object body) =>
        fixture.Client.PostAsJsonAsync(route, body, Ct);

    private static Task<HttpResponseMessage> PostRaw(ServerFixture fixture, string route, string json)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return fixture.Client.PostAsync(route, content, Ct);
    }

    private static async Task<string> TokenOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SessionReply>(ApiJson.Options, Ct))!.Token;
    }

    private static async Task AssertBadRequest(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCode.BadRequest, await ServerFixture.CodeOf(response));
    }
}
