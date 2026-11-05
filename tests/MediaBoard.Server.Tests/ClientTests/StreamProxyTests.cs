using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using MediaBoard.Core.Remote;
using MediaBoard.Desktop.Remote;
using Microsoft.AspNetCore.Http;

namespace MediaBoard.Server.Tests.ClientTests;

/// <summary>
/// The loopback proxy, between a plain HTTP client standing in for the player and either a <see cref="FakeApi"/>, which
/// cuts on demand, or the real server. No real player is involved.
/// </summary>
public sealed class StreamProxyTests
{
    private const string Token = "device-token-value";
    private const int FileId = 7;

    private static readonly TimeSpan Within = TimeSpan.FromSeconds(30);
    private static readonly byte[] Film = [.. Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251))];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ApiErrorCode> CutCodes => new()
    {
        ApiErrorCode.FileMissing,
        ApiErrorCode.DriveUnavailable,
        ApiErrorCode.StreamStopped,
    };

    private static int StatusOf(ApiErrorCode code) => code switch
    {
        ApiErrorCode.StreamStopped => StatusCodes.Status403Forbidden,
        ApiErrorCode.DriveUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status404NotFound,
    };

    private static Task RefuseAsync(HttpContext context, ApiErrorCode code) =>
        Results.Json(new ApiErrorBody(code), ApiJson.Options, statusCode: StatusOf(code)).ExecuteAsync(context);

    private static Task ServeAsync(HttpContext context) =>
        Results.Bytes(Film, "video/x-matroska", enableRangeProcessing: true).ExecuteAsync(context);

    /// <summary>Promises the whole film, sends the start of it, then drops the connection.</summary>
    private static async Task SendThenCutAsync(HttpContext context, int count)
    {
        context.Response.StatusCode = StatusCodes.Status206PartialContent;
        context.Response.ContentLength = Film.Length;
        context.Response.Headers.ContentRange = $"bytes 0-{Film.Length - 1}/{Film.Length}";
        await context.Response.Body.WriteAsync(Film.AsMemory(0, count), context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        await Task.Delay(100, context.RequestAborted);
        context.Abort();
    }

    private sealed class Fake : IAsyncDisposable
    {
        private readonly TaskCompletionSource<(int FileId, ApiErrorCode Code)> _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Fake(ServerFixture fixture, FakeApi api, ConcurrentQueue<string> ranges, ConcurrentQueue<string> calls)
        {
            Fixture = fixture;
            Api = api;
            Ranges = ranges;
            Calls = calls;
            Client = new ApiClient(new ServerEndpoint("Library", [api.Address], CertificateStore.Fingerprint(fixture.NewCertificate())), () => Token, _ => { });
            Proxy = new StreamProxy(Client);
            Proxy.StreamFailed += (fileId, code) =>
            {
                Failures.Enqueue((fileId, code));
                _failed.TrySetResult((fileId, code));
            };
        }

        public ServerFixture Fixture { get; }

        public FakeApi Api { get; }

        public ConcurrentQueue<string> Ranges { get; }

        public ConcurrentQueue<string> Calls { get; }

        public ConcurrentQueue<(int FileId, ApiErrorCode Code)> Failures { get; } = new();

        public ApiClient Client { get; }

        public StreamProxy Proxy { get; }

        public HttpClient Player { get; } = new();

        public Task<(int FileId, ApiErrorCode Code)> Failed => _failed.Task.WaitAsync(Within, Ct);

        /// <param name="answer">Takes the context and which request this is, from 1.</param>
        public static async Task<Fake> StartAsync(Func<HttpContext, int, Task> answer)
        {
            var fixture = new ServerFixture();
            var ranges = new ConcurrentQueue<string>();
            var calls = new ConcurrentQueue<string>();
            var count = 0;
            var api = await FakeApi.StartAsync(fixture.NewCertificate(), context =>
            {
                ranges.Enqueue(context.Request.Headers.Range.ToString());
                calls.Enqueue(context.Request.Path);
                return answer(context, Interlocked.Increment(ref count));
            });
            var fake = new Fake(fixture, api, ranges, calls);
            await fake.Proxy.StartAsync();
            return fake;
        }

        public string Url(string name = "Heat.mkv") => Proxy.UrlFor(FileId, name);

        public async Task<HttpResponseMessage> PlayAsync(string url, string? range = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
            return await Player.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            Player.Dispose();
            await Proxy.DisposeAsync();
            await Api.DisposeAsync();
            Client.Dispose();
            await Fixture.DisposeAsync();
        }
    }

    // ---- What the player sees ----

    [Fact]
    public async Task Binds_Loopback_Only()
    {
        await using var world = await Fake.StartAsync((context, _) => ServeAsync(context));

        var url = new Uri(world.Url());

        Assert.Equal("127.0.0.1", url.Host);
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(listener => listener.Port == url.Port).ToList();
        Assert.NotEmpty(listeners);
        Assert.All(listeners, listener => Assert.Equal(IPAddress.Loopback, listener.Address));
    }

    [Fact]
    public async Task Forwards_Range_AndPartialContent()
    {
        await using var world = await Fake.StartAsync((context, _) => ServeAsync(context));

        using var response = await world.PlayAsync(world.Url(), "bytes=100-199");

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes=100-199", Assert.Single(world.Ranges));
        Assert.Equal(new ContentRangeHeaderValue(100, 199, Film.Length).ToString(), response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(100, response.Content.Headers.ContentLength);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.Equal(Film[100..200], await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Empty(world.Failures);
    }

    [Fact]
    public async Task NoRange_AnswersTheWholeFile()
    {
        await using var world = await Fake.StartAsync((context, _) => ServeAsync(context));

        using var response = await world.PlayAsync(world.Url());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Film, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task RangePastTheEnd_IsForThePlayer_NotAFailure()
    {
        await using var world = await Fake.StartAsync((context, _) => ServeAsync(context));

        using var response = await world.PlayAsync(world.Url(), $"bytes={Film.Length + 1}-");

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public async Task WrongRandomSegment_404()
    {
        await using var world = await Fake.StartAsync((context, _) => ServeAsync(context));
        var url = new Uri(world.Url());
        var wrong = $"http://{url.Authority}/{new string('0', 32)}/{FileId}/Heat.mkv";

        using var response = await world.PlayAsync(wrong);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(world.Calls);
    }

    [Fact]
    public async Task NoTokenInPlayerFacingUrl()
    {
        await using var world = await Fake.StartAsync((context, _) => ServeAsync(context));

        var url = world.Url();
        using var response = await world.PlayAsync(url);

        Assert.DoesNotContain(Token, url, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, response.Headers.ToString() + response.Content.Headers, StringComparison.Ordinal);
        Assert.Equal($"Bearer {Token}", Assert.Single(world.Api.Authorizations));
    }

    [Theory]
    [InlineData("Голота.mkv", "file.mkv")]
    [InlineData("Голота S01E05 [Ukr.Eng].mkv", "S01E05Ukr.Eng.mkv")]
    [InlineData("Heat (1995)-remux.mp4", "Heat1995-remux.mp4")]
    [InlineData("...mkv", "file.mkv")]
    [InlineData("Голота.мкв", "file")]
    [InlineData("noextension", "noextension")]
    public async Task Cyrillic_FileName_GivesAsciiUrl_KeepsExtension_MapsBack(string fileName, string safe)
    {
        await using var world = await Fake.StartAsync((context, _) => ServeAsync(context));

        var url = world.Url(fileName);

        Assert.EndsWith($"/{FileId}/{safe}", url, StringComparison.Ordinal);
        Assert.True(url.All(char.IsAscii));
        Assert.Equal(FileId, world.Proxy.FileOf(url));
        using var response = await world.PlayAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FileOf_AnUnknownLocator_IsNull()
    {
        await using var world = await Fake.StartAsync((context, _) => ServeAsync(context));
        var url = world.Url();

        Assert.Null(world.Proxy.FileOf(url + "x"));
        Assert.Null(world.Proxy.FileOf(@"D:\Films\Heat.mkv"));
    }

    [Fact]
    public async Task UrlFor_StartsTheProxy_OnFirstPlay()
    {
        await using var fixture = new ServerFixture();
        await using var api = await FakeApi.StartAsync(fixture.NewCertificate(), ServeAsync);
        using var client = new ApiClient(new ServerEndpoint("Library", [api.Address], CertificateStore.Fingerprint(fixture.NewCertificate())), () => Token, _ => { });
        await using var proxy = new StreamProxy(client);
        using var player = new HttpClient();

        using var response = await player.GetAsync(proxy.UrlFor(FileId, "Heat.mkv"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Large_Offsets_Beyond4Gb_Forwarded()
    {
        const long From = 5_000_000_000;
        const long Total = 36_000_000_000;
        await using var world = await Fake.StartAsync(async (context, _) =>
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentLength = 10;
            context.Response.Headers.ContentRange = $"bytes {From}-{From + 9}/{Total}";
            await context.Response.Body.WriteAsync(Film.AsMemory(0, 10), context.RequestAborted);
        });

        using var response = await world.PlayAsync(world.Url(), $"bytes={From}-{From + 9}");

        Assert.Equal($"bytes={From}-{From + 9}", Assert.Single(world.Ranges));
        Assert.Equal(From, response.Content.Headers.ContentRange?.From);
        Assert.Equal(Total, response.Content.Headers.ContentRange?.Length);
        Assert.Equal(10, (await response.Content.ReadAsByteArrayAsync(Ct)).Length);
    }

    // ---- The player going away, which is never a failure ----

    [Fact]
    public async Task Seek_ForwardsNewRange()
    {
        var firstLeft = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var world = await Fake.StartAsync(async (context, request) =>
        {
            if (request > 1)
            {
                await ServeAsync(context);
                return;
            }

            context.RequestAborted.Register(firstLeft.SetResult);
            await FakeApi.SendAsync(context, "start");
            await FakeApi.HangAsync(context);
        });
        var url = world.Url();

        // Lavf opens the new request first, then drops the old one.
        var first = await world.PlayAsync(url, "bytes=0-");
        await using (var body = await first.Content.ReadAsStreamAsync(Ct))
        {
            await body.ReadExactlyAsync(new byte[5], Ct);
            using var second = await world.PlayAsync(url, "bytes=50000-");
            Assert.Equal(Film[50_000..], await second.Content.ReadAsByteArrayAsync(Ct));
        }

        first.Dispose();

        await firstLeft.Task.WaitAsync(Within, Ct);
        Assert.Equal(["bytes=0-", "bytes=50000-"], world.Ranges);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public async Task PlayerLeaves_ReachesTheServer_AndIsNoFailure()
    {
        var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var world = await Fake.StartAsync(async (context, _) =>
        {
            context.RequestAborted.Register(left.SetResult);
            await FakeApi.SendAsync(context, "start");
            await FakeApi.HangAsync(context);
        });
        var response = await world.PlayAsync(world.Url());
        var body = await response.Content.ReadAsStreamAsync(Ct);
        await body.ReadExactlyAsync(new byte[5], Ct);

        await body.DisposeAsync();
        response.Dispose();

        await left.Task.WaitAsync(Within, Ct);
        await Task.Delay(200, Ct);
        Assert.Single(world.Ranges);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public async Task ConnectionClose_EndingNormally_IsNoCut()
    {
        await using var world = await Fake.StartAsync((context, _) => ServeAsync(context));
        using var request = new HttpRequestMessage(HttpMethod.Get, world.Url());
        request.Headers.ConnectionClose = true;
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-");

        using var response = await world.Player.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        var body = await response.Content.ReadAsByteArrayAsync(Ct);

        Assert.Equal(Film, body);
        await Task.Delay(200, Ct);
        Assert.Single(world.Ranges);
        Assert.Empty(world.Failures);
    }

    // ---- The server going away, which is ----

    [Fact]
    public async Task Upstream_StreamStopped_RaisesStreamFailed()
    {
        await using var live = await LiveServer.StartAsync(Ct);
        var (id, _) = await live.AddFilmAsync("Heat (1995).mkv", 64 * 1024 * 1024);
        using var client = live.NewClient();
        await using var proxy = new StreamProxy(client);
        var failed = new TaskCompletionSource<(int, ApiErrorCode)>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.StreamFailed += (fileId, code) => failed.TrySetResult((fileId, code));
        using var player = new HttpClient();
        using var response = await player.GetAsync(proxy.UrlFor(id, "Heat (1995).mkv"), HttpCompletionOption.ResponseHeadersRead, Ct);
        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        await body.ReadExactlyAsync(new byte[64 * 1024], Ct);

        live.Fixture.Streams.Stop(live.Fixture.Streams.Current.Single().Id);

        await Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(Stream.Null, Ct).WaitAsync(Within, Ct));
        Assert.Equal((id, ApiErrorCode.StreamStopped), await failed.Task.WaitAsync(Within, Ct));
    }

    [Theory]
    [MemberData(nameof(CutCodes))]
    public async Task Refused_BeforeAnyBytes_RaisesStreamFailed(ApiErrorCode code)
    {
        await using var world = await Fake.StartAsync((context, _) => RefuseAsync(context, code));

        using var response = await world.PlayAsync(world.Url());

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal((FileId, code), await world.Failed);
    }

    [Theory]
    [MemberData(nameof(CutCodes))]
    public async Task UpstreamCut_ReRequestsOnce_ReadsTheCode(ApiErrorCode code)
    {
        await using var world = await Fake.StartAsync((context, request) =>
            request == 1 ? SendThenCutAsync(context, 20_000) : RefuseAsync(context, code));

        using var response = await world.PlayAsync(world.Url(), "bytes=0-");

        await Assert.ThrowsAnyAsync<Exception>(() => response.Content.ReadAsByteArrayAsync(Ct).WaitAsync(Within, Ct));
        Assert.Equal((FileId, code), await world.Failed);
        Assert.Equal(["bytes=0-", "bytes=20000-99999"], world.Ranges);
    }

    [Fact]
    public async Task UpstreamCut_ReRequestOk_ResumesFromSameOffset()
    {
        await using var world = await Fake.StartAsync((context, request) =>
            request == 1 ? SendThenCutAsync(context, 20_000) : ServeAsync(context));

        using var response = await world.PlayAsync(world.Url(), "bytes=0-");
        var body = await response.Content.ReadAsByteArrayAsync(Ct);

        Assert.Equal(Film, body);
        Assert.Equal(["bytes=0-", "bytes=20000-99999"], world.Ranges);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public async Task UpstreamCut_TheRestIsGone_RaisesFileMissing()
    {
        await using var world = await Fake.StartAsync((context, request) =>
        {
            if (request == 1) return SendThenCutAsync(context, 20_000);

            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            return Task.CompletedTask;
        });

        using var response = await world.PlayAsync(world.Url(), "bytes=0-");

        await Assert.ThrowsAnyAsync<Exception>(() => response.Content.ReadAsByteArrayAsync(Ct).WaitAsync(Within, Ct));
        Assert.Equal((FileId, ApiErrorCode.FileMissing), await world.Failed);
    }

    [Fact]
    public async Task UpstreamCut_ReRequestIsCutToo_GivesUp()
    {
        await using var world = await Fake.StartAsync((context, request) => request == 1
            ? SendThenCutAsync(context, 20_000)
            : CutAtOnceAsync(context));

        using var response = await world.PlayAsync(world.Url(), "bytes=0-");

        await Assert.ThrowsAnyAsync<Exception>(() => response.Content.ReadAsByteArrayAsync(Ct).WaitAsync(Within, Ct));
        Assert.Equal((FileId, ApiErrorCode.Unreachable), await world.Failed);
        Assert.Equal(2, world.Ranges.Count);

        static async Task CutAtOnceAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentLength = 80_000;
            context.Response.Headers.ContentRange = $"bytes 20000-99999/{Film.Length}";
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(100, context.RequestAborted);
            context.Abort();
        }
    }

    [Fact]
    public async Task ServerGone_RaisesUnreachable()
    {
        var world = await Fake.StartAsync((context, _) => ServeAsync(context));
        await using (world)
        {
            var url = world.Url();
            await world.Api.DisposeAsync();

            using var response = await world.PlayAsync(url);

            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal((FileId, ApiErrorCode.Unreachable), await world.Failed);
        }
    }
}
