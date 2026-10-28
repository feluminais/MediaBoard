using System.Net;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Tests.Support;

namespace MediaBoard.Core.Tests.Metadata;

public sealed class ImageCacheTests : IDisposable
{
    private static readonly byte[] Image = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-images-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly ActivityHub _hub;
    private volatile bool _networkUp = true;
    private readonly Connectivity _connectivity;
    private HttpClient? _http;

    public ImageCacheTests()
    {
        _hub = new ActivityHub(_clock);
        // The delay keeps the retry loop from spinning while the fake clock makes every wait instant.
        _connectivity = new Connectivity(
            async ct =>
            {
                await Task.Delay(5, ct);
                return _networkUp;
            },
            _clock,
            _hub);
    }

    public void Dispose()
    {
        _connectivity.Dispose();
        _http?.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ImageCache CacheWith(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler);
        return new ImageCache(_http, _connectivity, _dir);
    }

    private static StubHandler Serving(byte[] body) =>
        new((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

    private async Task GoOfflineAsync()
    {
        _networkUp = false;
        await _connectivity.CheckAsync(Ct);
        Assert.False(_connectivity.IsOnline);
    }

    private string[] FilesInCacheDir() => Directory.Exists(_dir) ? Directory.GetFiles(_dir) : [];

    [Fact]
    public async Task Cached_ReturnsWithoutHttp()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "w342abc123.jpg");
        await File.WriteAllBytesAsync(file, Image, Ct);
        var handler = Serving(Image);

        var path = await CacheWith(handler).GetAsync("/abc123.jpg", 342, Ct);

        Assert.Equal(file, path);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Cached_Offline_StillReturnsTheFile()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "w780abc123.jpg");
        await File.WriteAllBytesAsync(file, Image, Ct);
        var handler = Serving(Image);
        await GoOfflineAsync();

        var path = await CacheWith(handler).GetAsync("/abc123.jpg", 780, Ct);

        Assert.Equal(file, path);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Missing_Online_Downloads()
    {
        Uri? requested = null;
        var handler = new StubHandler((_, request) =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Image) };
        });
        var cache = CacheWith(handler);

        var path = await cache.GetAsync("/abc123.jpg", 342, Ct);

        Assert.Equal(Path.Combine(_dir, "w342abc123.jpg"), path);
        Assert.Equal(Image, await File.ReadAllBytesAsync(path!, Ct));
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/w342/abc123.jpg"), requested);
        Assert.Equal(1, handler.Calls);
        Assert.Equal([path!], FilesInCacheDir());

        // The second ask comes from disk.
        Assert.Equal(path, await cache.GetAsync("/abc123.jpg", 342, Ct));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EachWidth_IsItsOwnFile()
    {
        var handler = Serving(Image);
        var cache = CacheWith(handler);

        var small = await cache.GetAsync("/abc123.jpg", 342, Ct);
        var large = await cache.GetAsync("/abc123.jpg", 780, Ct);

        Assert.NotEqual(small, large);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Missing_Offline_ReturnsNull()
    {
        var handler = Serving(Image);
        await GoOfflineAsync();

        var path = await CacheWith(handler).GetAsync("/abc123.jpg", 342, Ct);

        Assert.Null(path);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NetworkFailure_ReportsOfflineAndReturnsNull()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("no route"));

        var path = await CacheWith(handler).GetAsync("/abc123.jpg", 342, Ct);

        Assert.Null(path);
        Assert.False(_connectivity.IsOnline);
        Assert.Empty(FilesInCacheDir());
    }

    [Fact]
    public async Task NotFound_ReturnsNullAndIsNotRetried()
    {
        var handler = StubHandler.Status(HttpStatusCode.NotFound);
        var cache = CacheWith(handler);

        Assert.Null(await cache.GetAsync("/gone.jpg", 342, Ct));
        Assert.Null(await cache.GetAsync("/gone.jpg", 342, Ct));

        Assert.Equal(1, handler.Calls);
        Assert.True(_connectivity.IsOnline);
        Assert.Empty(FilesInCacheDir());
    }

    [Fact]
    public async Task ConcurrentRequests_DownloadOnce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedHandler(release.Task, Image);
        var cache = CacheWith(handler);

        var first = cache.GetAsync("/abc123.jpg", 342, Ct);
        var second = cache.GetAsync("/abc123.jpg", 342, Ct);
        release.SetResult();
        var paths = await Task.WhenAll(first, second);

        Assert.NotNull(paths[0]);
        Assert.Equal(paths[0], paths[1]);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ManyThreadsAskingAtOnce_DownloadEachImageOnce()
    {
        const int callersPerImage = 16;
        const int images = 20;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedHandler(release.Task, Image);
        var cache = CacheWith(handler);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;

        // Every caller waits at the start line, then they all enter GetAsync together. The handler holds each
        // download open until all of them have had time to get in, so a second download for the same image
        // would show up as an extra call.
        var callers = Enumerable.Range(0, images * callersPerImage).Select(i => Task.Run(async () =>
        {
            Interlocked.Increment(ref ready);
            await go.Task;
            return await cache.GetAsync($"/image{i % images}.jpg", 342, Ct);
        }, Ct)).ToList();

        while (Volatile.Read(ref ready) < callers.Count) await Task.Delay(1, Ct);
        go.SetResult();
        await Task.Delay(200, Ct);
        release.SetResult();
        var paths = await Task.WhenAll(callers);

        Assert.Equal(images, handler.Calls);
        Assert.All(paths, p => Assert.NotNull(p));
        for (var i = 0; i < images; i++)
            Assert.Single(paths.Where((_, index) => index % images == i).Distinct());
        Assert.Equal(images, FilesInCacheDir().Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(185)]
    public async Task OtherWidths_Throw(int width)
    {
        var cache = CacheWith(Serving(Image));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.GetAsync("/abc123.jpg", width, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc123.jpg")]
    [InlineData("/../abc123.jpg")]
    [InlineData("/..")]
    [InlineData("/a/b.jpg")]
    [InlineData("/a\\b.jpg")]
    [InlineData("/a:b.jpg")]
    [InlineData("/")]
    public async Task UnsafePath_ReturnsNullWithoutHttp(string posterPath)
    {
        var handler = Serving(Image);

        var path = await CacheWith(handler).GetAsync(posterPath, 342, Ct);

        Assert.Null(path);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class GatedHandler(Task gate, byte[] body) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await gate.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }
}
