using System.Net;
using System.Text.RegularExpressions;
using MediaBoard.Core.Remote;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MediaBoard.Server.Tests;

/// <summary>Every route but the three public ones answers to a live device token and nothing else.</summary>
public sealed class AccessTableTests
{
    /// <summary>Covers any path and any method, so it has no row: <see cref="Unknown_Route_NeedsAuth"/> tests it.</summary>
    private const string Fallback = "/{*path}";

    private static readonly (string Method, string Route)[] Public =
    [
        ("GET", "/api/v1/ping"),
        ("POST", "/api/v1/invites/redeem"),
        ("POST", "/api/v1/sign-in"),
    ];

    // Method, route; {f} = forbidden id.
    public static TheoryData<string, string> Protected => new()
    {
        { "GET", "/api/v1/me" },
    };

    [Theory, MemberData(nameof(Protected))]
    public async Task NoToken_401(string m, string r)
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();

        await AssertUnauthorized(fixture, m, r, token: null);
    }

    [Theory, MemberData(nameof(Protected))]
    public async Task UnknownToken_401(string m, string r)
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();

        await AssertUnauthorized(fixture, m, r, "no-such-token");
    }

    [Theory, MemberData(nameof(Protected))]
    public async Task RevokedToken_401(string m, string r)
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var issued = fixture.Devices.Issue(fixture.NewPerson().Id, "Phone", "198.51.100.1");
        fixture.Service.SignOutDevice(issued.Device.Id);

        await AssertUnauthorized(fixture, m, r, issued.Token);
    }

    [Theory, MemberData(nameof(Protected))]
    public async Task DisabledAccount_401(string m, string r)
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var person = fixture.NewPerson();
        var issued = fixture.Devices.Issue(person.Id, "Phone", "198.51.100.1");
        fixture.FlagDisabled(person.Id);

        await AssertUnauthorized(fixture, m, r, issued.Token);
    }

    [Theory]
    [InlineData("GET", "/nothing/here")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/nothing/here.txt")]
    [InlineData("POST", "/api/v1/nothing")]
    [InlineData("DELETE", "/api/v1/ping")]
    [InlineData("POST", "/api/v1/ping")]
    [InlineData("GET", "/api/v1/sign-in")]
    [InlineData("HEAD", "/api/v1/ping")]
    [InlineData("OPTIONS", "/api/v1/ping")]
    [InlineData("GET", "/api/v2/ping")]
    public async Task Unknown_Route_NeedsAuth(string m, string r)
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();

        await AssertUnauthorized(fixture, m, r, token: null);
    }

    [Fact]
    public async Task Unknown_Route_WithToken_IsNotFound()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var issued = fixture.Devices.Issue(fixture.NewPerson().Id, "Phone", "198.51.100.1");

        var response = await fixture.Client.SendAsync(
            fixture.Request("GET", "/nothing/here", issued.Token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCode.NotFound, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task Me_WithToken_NamesTheAccount()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var issued = fixture.Devices.Issue(fixture.NewPerson("Anna").Id, "Phone", "198.51.100.1");

        var response = await fixture.Client.SendAsync(
            fixture.Request("GET", "/api/v1/me", issued.Token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"name\":\"Anna\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryRouteIsInTheTable()
    {
        await using var fixture = await ServerFixture.StartTestHostAsync();
        var table = Protected.Select(row => ((string Method, string Route))row.Data).ToList();

        var endpoints = ((IEndpointRouteBuilder)fixture.App).DataSources.SelectMany(source => source.Endpoints).ToList();
        var found = new List<(string Method, string Pattern)>();

        foreach (var endpoint in endpoints)
        {
            var route = Assert.IsType<RouteEndpoint>(endpoint);
            var pattern = route.RoutePattern.RawText!;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];

            if (methods.Count == 1 && Public.Contains((methods[0], pattern)))
            {
                found.Add((methods[0], pattern));
                continue;
            }

            Assert.True(endpoint.Metadata.GetMetadata<DeviceAuth>() is not null, $"{pattern} is outside the authenticated group.");
            if (pattern == Fallback)
            {
                Assert.Empty(methods);
                continue;
            }

            var method = Assert.Single(methods);
            found.Add((method, pattern));
            Assert.True(table.Exists(row => row.Method == method && Fits(pattern, row.Route)), $"{method} {pattern} is not in the table.");
        }

        Assert.All(Public, row => Assert.Contains(row, found));
        Assert.All(table, row => Assert.Contains(found, route => route.Method == row.Method && Fits(route.Pattern, row.Route)));
    }

    private static async Task AssertUnauthorized(ServerFixture fixture, string method, string route, string? token)
    {
        var response = await fixture.Client.SendAsync(
            fixture.Request(method, route.Replace("{f}", "1", StringComparison.Ordinal), token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // A HEAD response has no body to read.
        if (method != "HEAD") Assert.Equal(ApiErrorCode.Unauthorized, await ServerFixture.CodeOf(response));
    }

    // {id} in a pattern stands for whatever a table row put there, {f} included.
    private static bool Fits(string pattern, string row) =>
        Regex.IsMatch(row, "^" + Regex.Replace(Regex.Escape(pattern), @"\\\{[^/]*}", "[^/]+") + "$", RegexOptions.None, TimeSpan.FromSeconds(1));
}
