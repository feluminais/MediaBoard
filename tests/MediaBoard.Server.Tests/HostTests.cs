using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using MediaBoard.Core.Remote;
using Microsoft.Extensions.Logging;

namespace MediaBoard.Server.Tests;

/// <summary>What only a real Kestrel on a real socket can show.</summary>
public sealed class HostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ping_LeaksNoServerHeader()
    {
        await using var fixture = new ServerFixture();
        var port = await StartAsync(fixture);

        var response = await fixture.TlsClient().GetAsync(Url(port, "/api/v1/ping"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Server"));
        Assert.Null(response.Headers.Server.FirstOrDefault());
    }

    [Fact]
    public async Task Ping_AnswersHttp11_EvenToAClientThatPrefersHttp2()
    {
        await using var fixture = new ServerFixture();
        var port = await StartAsync(fixture);
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(port, "/api/v1/ping"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        var response = await fixture.TlsClient().SendAsync(request, Ct);

        Assert.Equal(HttpVersion.Version11, response.Version);
    }

    [Fact]
    public async Task Stop_EndsOnTime_WhenARequestHangs_AndFreesThePort()
    {
        await using var fixture = new ServerFixture();
        var port = await StartAsync(fixture);
        var content = new HangingContent();
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(port, "/api/v1/sign-in")) { Content = content };
        using var endClient = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = fixture.TlsClient().SendAsync(request, endClient.Token);
        await content.Started.WaitAsync(Ct);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await fixture.Server.StopAsync();

        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        Assert.False(fixture.Server.IsRunning);
        await endClient.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        await fixture.Server.StartAsync(Options(fixture, port), Ct);
    }

    [Fact]
    public async Task Start_DoesNotServePlainHttp()
    {
        await using var fixture = new ServerFixture();
        var port = await StartAsync(fixture);

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => fixture.TlsClient().GetAsync(new Uri($"http://127.0.0.1:{port}/api/v1/ping"), Ct));
    }

    [Fact]
    public async Task Start_PortInUse_Throws()
    {
        await using var fixture = new ServerFixture();
        var taken = new TcpListener(IPAddress.IPv6Any, 0);
        taken.Server.DualMode = true;
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;

        try
        {
            var thrown = await Assert.ThrowsAsync<PortInUseException>(() => fixture.Server.StartAsync(Options(fixture, port), Ct));

            Assert.Equal(port, thrown.Port);
            Assert.False(fixture.Server.IsRunning);
        }
        finally
        {
            taken.Stop();
        }
    }

    [Fact]
    public async Task Start_AfterPortInUse_WorksOnceThePortIsFree()
    {
        await using var fixture = new ServerFixture();
        var taken = new TcpListener(IPAddress.IPv6Any, 0);
        taken.Server.DualMode = true;
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;
        await Assert.ThrowsAsync<PortInUseException>(() => fixture.Server.StartAsync(Options(fixture, port), Ct));
        taken.Stop();

        await fixture.Server.StartAsync(Options(fixture, port), Ct);

        Assert.True(fixture.Server.IsRunning);
    }

    [Fact]
    public async Task OneAddress_CanHoldOnlyItsShareOfConnections()
    {
        await using var fixture = new ServerFixture();
        var port = ServerFixture.FreePort();
        await fixture.Server.StartAsync(Options(fixture, port) with { ConnectionsPerAddress = 2 }, Ct);

        using var first = new TcpClient();
        using var second = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, port, Ct);
        await second.ConnectAsync(IPAddress.Loopback, port, Ct);
        await Task.Delay(100, Ct); // Kestrel counts a connection once it has accepted it.

        using var refused = new TcpClient();
        await refused.ConnectAsync(IPAddress.Loopback, port, Ct);

        // Cut before TLS: the server neither says hello nor waits for one.
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            var read = await refused.GetStream().ReadAsync(new byte[1], Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
            if (read == 0) throw new IOException("Closed.");
        });

        // A place that opens up is free again.
        first.Close();
        await Task.Delay(100, Ct);
        var response = await fixture.TlsClient().GetAsync(Url(port, "/api/v1/ping"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Stop_LetsGoOfTheKeptPartOfTheLibrary()
    {
        await using var fixture = new ServerFixture();
        fixture.AddFolder("open");
        var token = fixture.Join(fixture.NewPerson("Sam")).Token;
        var port = await StartAsync(fixture);
        using var request = fixture.Request("GET", $"https://127.0.0.1:{port}/api/v1/titles?kind=Movie", token);
        (await fixture.TlsClient().SendAsync(request, Ct)).EnsureSuccessStatusCode();
        Assert.True(fixture.Owner.SharedBase!.Holds);

        await fixture.Server.StopAsync();

        Assert.False(fixture.Owner.SharedBase.Holds);
    }

    [Fact]
    public async Task Start_Stop_Start_Works()
    {
        await using var fixture = new ServerFixture();
        var port = ServerFixture.FreePort();
        var client = fixture.TlsClient();

        Assert.False(fixture.Server.IsRunning);

        await fixture.Server.StartAsync(Options(fixture, port), Ct);
        Assert.True(fixture.Server.IsRunning);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(port, "/api/v1/ping"), Ct)).StatusCode);

        await fixture.Server.StopAsync();
        Assert.False(fixture.Server.IsRunning);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(Url(port, "/api/v1/ping"), Ct));

        await fixture.Server.StartAsync(Options(fixture, port), Ct);
        Assert.True(fixture.Server.IsRunning);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(port, "/api/v1/ping"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Start_WhileRunning_Throws()
    {
        await using var fixture = new ServerFixture();
        var port = await StartAsync(fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Server.StartAsync(Options(fixture, port), Ct));

        Assert.True(fixture.Server.IsRunning);
    }

    [Fact]
    public async Task Stop_WhenNotRunning_DoesNothing()
    {
        await using var fixture = new ServerFixture();

        await fixture.Server.StopAsync();

        Assert.False(fixture.Server.IsRunning);
    }

    [Fact]
    public async Task Ping_NamesTheServerFromTheOptions()
    {
        await using var fixture = new ServerFixture();
        var port = ServerFixture.FreePort();
        await fixture.Server.StartAsync(Options(fixture, port) with { ServerName = "Family library" }, Ct);

        var reply = await fixture.TlsClient().GetFromJsonAsync<PingReply>(Url(port, "/api/v1/ping"), ApiJson.Options, Ct);

        Assert.Equal("Family library", reply!.Name);
    }

    [Fact]
    public async Task Body_Over64Kb_Rejected_WithContentLength()
    {
        await using var fixture = new ServerFixture();
        var port = await StartAsync(fixture);

        var response = await fixture.TlsClient().PostAsync(Url(port, "/api/v1/sign-in"), new ByteArrayContent(new byte[65537]), Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ApiErrorCode.TooLarge, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task Body_Over64Kb_Rejected_WhenChunked()
    {
        await using var fixture = new ServerFixture();
        var port = await StartAsync(fixture);
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(port, "/api/v1/sign-in"))
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 70_000)))),
        };
        request.Headers.TransferEncodingChunked = true;

        var response = await fixture.TlsClient().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ApiErrorCode.TooLarge, await ServerFixture.CodeOf(response));
    }

    [Fact]
    public async Task Address_FromADualModeSocket_IsPlainIPv4()
    {
        await using var fixture = new ServerFixture();
        var port = await StartAsync(fixture);
        var body = new { code = fixture.Invites.Create(fixture.NewPerson().Id), password = ServerFixture.Password, deviceName = "Phone" };

        var response = await fixture.TlsClient().PostAsJsonAsync(Url(port, "/api/v1/invites/redeem"), body, Ct);

        var reply = await response.Content.ReadFromJsonAsync<SessionReply>(ApiJson.Options, Ct);
        Assert.Equal("127.0.0.1", fixture.Devices.ByToken(reply!.Token)!.LastAddress);
        Assert.Equal("Anna", reply.Name);
    }

    [Fact]
    public async Task Logs_HoldNoPasswordTokenOrQuery()
    {
        const string QuerySecret = "query-secret-value";
        const string TokenSecret = "token-secret-value";
        var logs = new LogCapture();
        await using var fixture = new ServerFixture(logs);
        var person = fixture.NewPerson();
        fixture.Join(person);
        var port = await StartAsync(fixture);
        var client = fixture.TlsClient();

        var signIn = new { name = person.Name, password = ServerFixture.Password, deviceName = "Phone" };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url(port, $"/api/v1/sign-in?q={QuerySecret}"), signIn, Ct)).StatusCode);
        await client.PostAsync(Url(port, "/api/v1/sign-in"), new StringContent(ServerFixture.Password), Ct);
        using var me = new HttpRequestMessage(HttpMethod.Get, Url(port, $"/api/v1/me?q={QuerySecret}"));
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenSecret);
        await client.SendAsync(me, Ct);
        await client.GetAsync(Url(port, $"/nothing?q={QuerySecret}"), Ct);
        await fixture.Server.StopAsync();

        Assert.NotEmpty(logs.Categories);
        Assert.DoesNotContain(logs.Lines, line =>
            line.Contains(ServerFixture.Password, StringComparison.Ordinal)
            || line.Contains(QuerySecret, StringComparison.Ordinal)
            || line.Contains(TokenSecret, StringComparison.Ordinal)
            || line.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
    }

    private static Uri Url(int port, string route) => new($"https://127.0.0.1:{port}{route}");

    private static SharingOptions Options(ServerFixture fixture, int port) =>
        new(port, ServerFixture.ServerName, fixture.NewCertificate());

    private static async Task<int> StartAsync(ServerFixture fixture)
    {
        var port = ServerFixture.FreePort();
        await fixture.Server.StartAsync(Options(fixture, port), Ct);
        return port;
    }

    /// <summary>Sends one byte and then nothing, until the client gives up.</summary>
    private sealed class HangingContent : HttpContent
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync("{"u8.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            _started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Keeps every line the host's filters let through.</summary>
    internal sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ConcurrentQueue<string> Categories { get; } = new();

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Enqueue(categoryName);
            return new Logger(categoryName, Lines);
        }

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
