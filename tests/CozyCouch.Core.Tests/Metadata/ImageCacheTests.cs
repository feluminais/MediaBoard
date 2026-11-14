using System.Net;
using CozyCouch.Core.Activity;
using CozyCouch.Core.Metadata;
using CozyCouch.Core.Tests.Support;

namespace CozyCouch.Core.Tests.Metadata;

public sealed class ImageCacheTests : IDisposable
{
    private static readonly byte[] Image = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cozycouch-images-" + Guid.NewGuid().ToString("N"));
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
        return new ImageCache(_http, _connectivity, _dir, _clock);
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

    [Theory]
    [InlineData("/abc123.jpg", 342, "w342abc123.jpg", "https://image.tmdb.org/t/p/w342/abc123.jpg")] // a poster
    [InlineData("/still.jpg", 300, "w300still.jpg", "https://image.tmdb.org/t/p/w300/still.jpg")] // an episode still
    public async Task Missing_Online_Downloads(string posterPath, int width, string fileName, string url)
    {
        Uri? requested = null;
        var handler = new StubHandler((_, request) =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Image) };
        });
        var cache = CacheWith(handler);

        var path = await cache.GetAsync(posterPath, width, Ct);

        Assert.Equal(Path.Combine(_dir, fileName), path);
        Assert.Equal(Image, await File.ReadAllBytesAsync(path!, Ct));
        Assert.Equal(new Uri(url), requested);
        Assert.Equal(1, handler.Calls);
        Assert.Equal([path!], FilesInCacheDir());

        // The second ask comes from disk.
        Assert.Equal(path, await cache.GetAsync(posterPath, width, Ct));
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
    public async Task HasRoom_MeasuresTheFolder_AtMostOnceAMinute()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "w342a.jpg"), new byte[10], Ct);
        _http = new HttpClient(Serving(Image));
        var cache = new ImageCache(_http, _connectivity, _dir, _clock, maxBytes: 20);
        Assert.True(cache.HasRoom());

        await File.WriteAllBytesAsync(Path.Combine(_dir, "w342b.jpg"), new byte[10], Ct);
        Assert.True(cache.HasRoom());

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(cache.HasRoom());
    }

    [Fact]
    public async Task Has_SaysWhatIsOnDisk()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "w342abc123.jpg"), Image, Ct);
        var cache = CacheWith(Serving(Image));

        Assert.True(cache.Has("/abc123.jpg", 342));
        Assert.False(cache.Has("/abc123.jpg", 780));
        Assert.False(cache.Has("/other.jpg", 342));
    }

    [Fact]
    public async Task NotFound_IsAskedAgainAfterFiveMinutes()
    {
        var handler = StubHandler.Status(HttpStatusCode.NotFound);
        var cache = CacheWith(handler);

        await cache.GetAsync("/gone.jpg", 342, Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));

        // The finished download clears itself a moment after it answers, and until then it answers again.
        for (var tries = 0; tries < 200 && handler.Calls < 2; tries++)
        {
            await cache.GetAsync("/gone.jpg", 342, Ct);
            await Task.Delay(5, Ct);
        }

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Failures_AreRememberedUpToALimit()
    {
        var handler = StubHandler.Status(HttpStatusCode.NotFound);
        var cache = CacheWith(handler);

        for (var name = 0; name <= 1_000; name++) await cache.GetAsync($"/n{name}.jpg", 342, Ct);

        Assert.InRange(cache.RememberedFailures, 1, 1_000);
    }

    [Theory]
    [InlineData("abc123.jpg", true)]
    [InlineData("poster.png", true)]
    [InlineData("a.gif", false)]
    [InlineData("a/b.jpg", false)]
    public void TmdbImageName_KnowsAName(string name, bool valid) => Assert.Equal(valid, TmdbImageName.IsValid(name));

    [Fact]
    public void TmdbImageName_HasALimit()
    {
        Assert.True(TmdbImageName.IsValid(new string('a', 60) + ".jpg"));
        Assert.False(TmdbImageName.IsValid(new string('a', 61) + ".jpg"));
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

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public async Task OtherWidths_Throw(int width)
    {
        var cache = CacheWith(Serving(Image));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.GetAsync("/abc123.jpg", width, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc123.jpg")]
    [InlineData("/../abc123.jpg")]
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
