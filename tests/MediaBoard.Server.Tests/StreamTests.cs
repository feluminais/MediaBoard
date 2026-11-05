using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Storage;
using Microsoft.Extensions.Logging;

namespace MediaBoard.Server.Tests;

/// <summary>
/// The stream route over TestHost, and over a real socket wherever a test needs a response cut halfway. The big file
/// outruns the socket buffers, so the server is still sending when the test changes something.
/// </summary>
public sealed class StreamTests
{
    private const int SmallSize = 4096;

    // Off the copy's 64 KB steps, so the last read isn't an exact fit that hides a read past the end.
    private const int BigSize = (64 * 1024 * 1024) + 1;

    private static readonly TimeSpan CutWithin = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World : IAsyncDisposable
    {
        private string _base = "";

        private World(ServerFixture fixture) => Fixture = fixture;

        public ServerFixture Fixture { get; }

        public HttpClient Client { get; private set; } = null!;

        public LibraryFolder Open { get; private set; } = null!;

        public LibraryFolder Closed { get; private set; } = null!;

        public int Movie { get; private set; }

        public int File { get; private set; }

        public int ForbiddenFile { get; private set; }

        public string FilePath => Path.Combine(Open.Path, "Sub", "Heat (1995).mkv");

        public Account Sam { get; private set; } = null!;

        public IssuedDevice Phone { get; private set; } = null!;

        public string Token => Phone.Token;

        public static async Task<World> OnTestHostAsync()
        {
            var fixture = await ServerFixture.StartTestHostAsync();
            var world = new World(fixture) { Client = fixture.Client };
            await world.FillAsync();
            return world;
        }

        public static async Task<World> OnSocketAsync(ILoggerProvider? logs = null)
        {
            var fixture = new ServerFixture(logs);
            var port = ServerFixture.FreePort();
            await fixture.Server.StartAsync(new SharingOptions(port, ServerFixture.ServerName, fixture.NewCertificate()), Ct);
            var world = new World(fixture) { Client = fixture.TlsClient(), _base = $"https://127.0.0.1:{port}" };
            await world.FillAsync();
            System.IO.File.Delete(world.FilePath);
            await using var big = System.IO.File.Create(world.FilePath);
            big.SetLength(BigSize);
            return world;
        }

        public Task<HttpResponseMessage> StreamAsync(string? range = null, int? fileId = null, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
        {
            var request = Fixture.Request("GET", $"{_base}/api/v1/files/{fileId ?? File}/stream", Token);
            if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
            return Client.SendAsync(request, completion, Ct);
        }

        public Task<HttpResponseMessage> GetFileAsync(int id) =>
            Client.SendAsync(Fixture.Request("GET", $"{_base}/api/v1/files/{id}", Token), Ct);

        /// <summary>A stream the server is in the middle of sending, a little of it read.</summary>
        public async Task<Stream> LiveBodyAsync(string? range = null)
        {
            var response = await StreamAsync(range, completion: HttpCompletionOption.ResponseHeadersRead);
            Assert.True(response.IsSuccessStatusCode, $"The stream answered {response.StatusCode}.");

            var body = await response.Content.ReadAsStreamAsync(Ct);
            await body.ReadExactlyAsync(new byte[64 * 1024], Ct);
            return body;
        }

        public RequestViewer Viewer() => new(new Viewer(Sam.Id, null), Sam, Phone.Device);

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();

        private async Task FillAsync()
        {
            Open = Fixture.AddFolder("open");
            Closed = Fixture.AddFolder("closed");
            Movie = Fixture.Movie("Heat", 101);
            File = await Fixture.AddFileAsync(Open, @"Sub\Heat (1995).mkv", Movie);
            ForbiddenFile = await Fixture.AddFileAsync(Closed, "Ronin (1998).mkv", Fixture.Movie("Ronin", 102));
            await System.IO.File.WriteAllBytesAsync(FilePath, Pattern(SmallSize), Ct);
            Sam = Fixture.NewPerson("Sam", [Open.Id]);
            Phone = Fixture.Devices.Issue(Sam.Id, "Phone", "198.51.100.1");
        }
    }

    private static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i % 251))];

    private static Task AssertCutAsync(Stream body) =>
        Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(Stream.Null, Ct).WaitAsync(CutWithin, Ct));

    private static async Task AssertErrorAsync(HttpStatusCode status, ApiErrorCode code, HttpResponseMessage response)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await ServerFixture.CodeOf(response));
    }

    // A response that breaks its Content-Length still reaches the client looking fine or merely cut. Only the log tells.
    private static async Task AssertNoLengthMismatchAsync(World world, HostTests.LogCapture logs)
    {
        await world.Fixture.Server.StopAsync();
        Assert.DoesNotContain(logs.Lines, line => line.Contains("Content-Length", StringComparison.Ordinal));
    }

    // ---- Ranges ----

    [Theory]
    [InlineData("bytes=0-99", 0, 99)]
    [InlineData("bytes=1000-1999", 1000, 1999)]
    [InlineData("bytes=-100", SmallSize - 100, SmallSize - 1)]
    [InlineData("bytes=4000-", 4000, SmallSize - 1)]
    public async Task Range_ReturnsThoseBytes(string range, int from, int to)
    {
        await using var world = await World.OnTestHostAsync();

        var response = await world.StreamAsync(range);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(from, to, SmallSize), response.Content.Headers.ContentRange);
        Assert.Equal(to - from + 1, response.Content.Headers.ContentLength);
        Assert.Equal(Pattern(SmallSize)[from..(to + 1)], await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Range_PastTheEnd_416()
    {
        await using var world = await World.OnTestHostAsync();

        var response = await world.StreamAsync($"bytes={SmallSize}-");

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal(SmallSize, response.Content.Headers.ContentRange?.Length);
    }

    [Fact]
    public async Task NoRange_Returns200WithLength()
    {
        await using var world = await World.OnTestHostAsync();

        var response = await world.StreamAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SmallSize, response.Content.Headers.ContentLength);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.Equal(Pattern(SmallSize), await response.Content.ReadAsByteArrayAsync(Ct));
    }

    // ---- The file changes while it streams ----

    [Fact]
    public async Task Stream_While_FileIsAppended_Works()
    {
        var logs = new HostTests.LogCapture();
        await using var world = await World.OnSocketAsync(logs);
        var body = await world.LiveBodyAsync();
        const int Appended = 2 * 1024 * 1024;

        // A torrent client's handle, opened while the stream reads. Sharing read only is the strict case.
        await using (var torrent = new FileStream(world.FilePath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            torrent.Seek(0, SeekOrigin.End);
            await torrent.WriteAsync(new byte[Appended], Ct);
        }

        var rest = 0L;
        var chunk = new byte[1024 * 1024];
        for (var read = 1; read > 0; rest += read) read = await body.ReadAsync(chunk, Ct);

        // The length promised in the headers holds, however much the file grew behind it.
        Assert.Equal(BigSize - (64 * 1024), rest);
        var again = await world.StreamAsync("bytes=0-0");
        Assert.Equal(BigSize + Appended, again.Content.Headers.ContentRange?.Length);
        await AssertNoLengthMismatchAsync(world, logs);
    }

    [Fact]
    public async Task Stream_While_ATorrentClientHoldsTheFile_Works()
    {
        await using var world = await World.OnTestHostAsync();
        await using var torrent = new FileStream(world.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        var response = await world.StreamAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        torrent.Seek(0, SeekOrigin.Begin);
        await torrent.WriteAsync(new byte[16], Ct);
    }

    [Fact]
    public async Task Stream_While_FileIsDeleted_EndsWithFileMissing()
    {
        await using var world = await World.OnSocketAsync();
        var body = await world.LiveBodyAsync();

        File.Delete(world.FilePath);

        await AssertCutAsync(body);
        await AssertErrorAsync(HttpStatusCode.NotFound, ApiErrorCode.FileMissing, await world.StreamAsync("bytes=0-0"));
    }

    [Fact]
    public async Task Stream_While_FileIsMoved_EndsWithFileMissing()
    {
        await using var world = await World.OnSocketAsync();
        var body = await world.LiveBodyAsync();

        File.Move(world.FilePath, world.FilePath + ".moved");

        await AssertCutAsync(body);
        await AssertErrorAsync(HttpStatusCode.NotFound, ApiErrorCode.FileMissing, await world.StreamAsync("bytes=0-0"));
    }

    [Fact]
    public async Task Stream_While_FileShrinks_IsCut()
    {
        var logs = new HostTests.LogCapture();
        await using var world = await World.OnSocketAsync(logs);
        var body = await world.LiveBodyAsync();

        await using (var torrent = new FileStream(world.FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            torrent.SetLength(1024 * 1024);
        }

        await AssertCutAsync(body);
        await AssertNoLengthMismatchAsync(world, logs);
    }

    [Fact]
    public async Task Stream_CutByAFileProblem_LogsNoPath()
    {
        var logs = new HostTests.LogCapture();
        await using var world = await World.OnSocketAsync(logs);
        var body = await world.LiveBodyAsync();

        File.Delete(world.FilePath);
        await AssertCutAsync(body);
        await world.StreamAsync("bytes=0-0");
        await world.Fixture.Server.StopAsync();

        Assert.DoesNotContain(logs.Lines, line => line.Contains(world.Open.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Stream_FileGone_FileMissing()
    {
        await using var world = await World.OnTestHostAsync();
        File.Delete(world.FilePath);

        await AssertErrorAsync(HttpStatusCode.NotFound, ApiErrorCode.FileMissing, await world.StreamAsync());

        Assert.Empty(world.Fixture.Streams.Current);
    }

    [Fact]
    public async Task Stream_FolderGone_FileMissing()
    {
        await using var world = await World.OnTestHostAsync();
        Directory.Delete(world.Open.Path, recursive: true);

        await AssertErrorAsync(HttpStatusCode.NotFound, ApiErrorCode.FileMissing, await world.StreamAsync());
    }

    [Fact]
    public async Task Stream_MissingDrive_DriveUnavailable()
    {
        await using var world = await World.OnTestHostAsync();
        var unused = "ZYXWVUTSRQPONMLKJIHGFED".First(letter => !Directory.Exists($@"{letter}:\"));
        using (var connection = world.Fixture.Db.Open())
        {
            connection.Execute("UPDATE folders SET path = @path WHERE id = @id", new { path = $@"{unused}:\Films", id = world.Open.Id });
        }

        await AssertErrorAsync(HttpStatusCode.ServiceUnavailable, ApiErrorCode.DriveUnavailable, await world.StreamAsync());

        Assert.Empty(world.Fixture.Streams.Current);
    }

    [Fact]
    public async Task Stream_DoesNotChangeFile()
    {
        await using var world = await World.OnTestHostAsync();
        var written = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(world.FilePath, written);

        await (await world.StreamAsync()).Content.ReadAsByteArrayAsync(Ct);

        Assert.Equal(SmallSize, new FileInfo(world.FilePath).Length);
        Assert.Equal(written, File.GetLastWriteTimeUtc(world.FilePath));
    }

    // ---- Who may stream ----

    [Fact]
    public async Task Stream_AccessForbidden_404()
    {
        await using var world = await World.OnTestHostAsync();
        var unknown = await world.StreamAsync(fileId: 99_999);

        var forbidden = await world.StreamAsync(fileId: world.ForbiddenFile);

        await AssertErrorAsync(HttpStatusCode.NotFound, ApiErrorCode.NotFound, forbidden);
        Assert.Equal(unknown.StatusCode, forbidden.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(Ct), await (await world.StreamAsync(fileId: world.ForbiddenFile)).Content.ReadAsStringAsync(Ct));
        Assert.Empty(world.Fixture.Streams.Current);
    }

    [Fact]
    public async Task Stream_HiddenTitle_404()
    {
        await using var world = await World.OnTestHostAsync();
        world.Fixture.Owner.SetHidden(TargetKind.Title, world.Movie, hidden: true);

        await AssertErrorAsync(HttpStatusCode.NotFound, ApiErrorCode.NotFound, await world.StreamAsync());
    }

    // ---- Noisy players ----

    [Fact]
    public async Task OverlappingRequests_ShowOneRow()
    {
        await using var world = await World.OnSocketAsync();

        var first = await world.LiveBodyAsync();
        var second = await world.LiveBodyAsync("bytes=1048576-");
        var third = await world.LiveBodyAsync("bytes=-1048576");

        var row = Assert.Single(world.Fixture.Streams.Current);
        Assert.Equal("Sam", row.UserName);
        Assert.Equal("Phone", row.DeviceName);
        Assert.Equal(world.File, row.FileId);
        Assert.True(row.MbitPerSecond > 0);
        await first.DisposeAsync();
        await second.DisposeAsync();
        await third.DisposeAsync();
    }

    [Fact]
    public async Task Seek_Reconnects_IntoTheSameRow()
    {
        await using var world = await World.OnTestHostAsync();

        await world.StreamAsync("bytes=0-99");
        var first = Assert.Single(world.Fixture.Streams.Current);
        await world.StreamAsync("bytes=2000-2099");

        Assert.Equal(first.Id, Assert.Single(world.Fixture.Streams.Current).Id);
    }

    [Fact]
    public async Task Stream_OverTheLimit_TooManyStreams()
    {
        await using var world = await World.OnTestHostAsync();
        for (var file = 1; file <= 8; file++)
        {
            world.Fixture.Streams.Open(
                world.Viewer(), new ReachableFile(100 + file, 1, "", "", "", null, null, false), CancellationToken.None, out _);
        }

        await AssertErrorAsync(HttpStatusCode.TooManyRequests, ApiErrorCode.TooManyStreams, await world.StreamAsync());
    }

    // ---- Stops and access changes during a stream ----

    [Fact]
    public async Task Stop_CutsTheStream_AndTheNextRequestIsRefused()
    {
        await using var world = await World.OnSocketAsync();
        var body = await world.LiveBodyAsync();

        world.Fixture.Streams.Stop(world.Fixture.Streams.Current.Single().Id);

        await AssertCutAsync(body);
        await AssertErrorAsync(HttpStatusCode.Forbidden, ApiErrorCode.StreamStopped, await world.StreamAsync("bytes=0-0"));
    }

    [Fact]
    public async Task FolderTaken_CutsTheStream_AndTheNextRequestIs404()
    {
        await using var world = await World.OnSocketAsync();
        var body = await world.LiveBodyAsync();

        world.Fixture.Service.SetFolders(world.Sam.Id, []);

        await AssertCutAsync(body);
        await AssertErrorAsync(HttpStatusCode.NotFound, ApiErrorCode.NotFound, await world.StreamAsync("bytes=0-0"));
        Assert.Empty(world.Fixture.Streams.Current);
    }

    [Fact]
    public async Task FolderTaken_LeavesOtherStreamsPlaying()
    {
        await using var world = await World.OnSocketAsync();
        var kept = await world.LiveBodyAsync();
        var row = world.Fixture.Streams.Current.Single();

        world.Fixture.Service.SetFolders(world.Sam.Id, [world.Open.Id, world.Closed.Id]);
        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);

        Assert.Equal(row.Id, Assert.Single(world.Fixture.Streams.Current).Id);
        await kept.ReadExactlyAsync(new byte[1024], Ct);
    }

    [Fact]
    public async Task FolderTaken_WhileTheSessionOpens_404AndNoRow()
    {
        await using var world = await World.OnTestHostAsync();
        // The session is listed by now but the request hasn't started sending. A change here raises no event in time
        // to find the stream, so this writes past the service to leave only the request's own check.
        world.Fixture.Streams.Changed += () => world.Fixture.Accounts.SetFolders(world.Sam.Id, []);

        await AssertErrorAsync(HttpStatusCode.NotFound, ApiErrorCode.NotFound, await world.StreamAsync());

        Assert.Empty(world.Fixture.Streams.Current);
    }

    [Fact]
    public async Task TitleHidden_CutsTheStream_AndTheNextRequestIs404()
    {
        await using var world = await World.OnSocketAsync();
        var body = await world.LiveBodyAsync();

        world.Fixture.Owner.SetHidden(TargetKind.Title, world.Movie, hidden: true);

        await AssertCutAsync(body);
        await AssertErrorAsync(HttpStatusCode.NotFound, ApiErrorCode.NotFound, await world.StreamAsync("bytes=0-0"));
    }

    [Fact]
    public async Task AccountDisabled_CutsTheStream_AndTheNextRequestIs401()
    {
        await using var world = await World.OnSocketAsync();
        var body = await world.LiveBodyAsync();

        world.Fixture.Service.Disable(world.Sam.Id, disabled: true);

        await AssertCutAsync(body);
        await AssertErrorAsync(HttpStatusCode.Unauthorized, ApiErrorCode.Unauthorized, await world.StreamAsync("bytes=0-0"));
    }

    [Fact]
    public async Task DeviceSignedOut_CutsTheStream_AndTheNextRequestIs401()
    {
        await using var world = await World.OnSocketAsync();
        var body = await world.LiveBodyAsync();

        world.Fixture.Service.SignOutDevice(world.Phone.Device.Id);

        await AssertCutAsync(body);
        await AssertErrorAsync(HttpStatusCode.Unauthorized, ApiErrorCode.Unauthorized, await world.StreamAsync("bytes=0-0"));
    }

    [Fact]
    public async Task ServerStop_EndsOpenStreams_WithoutWaitingOutTheTimeout()
    {
        await using var world = await World.OnSocketAsync();
        var body = await world.LiveBodyAsync();
        // Long enough for the socket to fill. The server then sits in a write, where only a connection abort reaches it.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);

        var clock = Stopwatch.StartNew();
        await world.Fixture.Server.StopAsync();

        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        Assert.Empty(world.Fixture.Streams.Current);
        await AssertCutAsync(body);
    }

    [Fact]
    public async Task ServerStop_ThenStart_StillWatchesAccess()
    {
        await using var world = await World.OnSocketAsync();
        await world.Fixture.Server.StopAsync();
        var port = ServerFixture.FreePort();
        await world.Fixture.Server.StartAsync(new SharingOptions(port, ServerFixture.ServerName, world.Fixture.NewCertificate()), Ct);
        var token = world.Token;
        using var request = world.Fixture.Request("GET", $"https://127.0.0.1:{port}/api/v1/files/{world.File}/stream", token);
        var response = await world.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        var body = await response.Content.ReadAsStreamAsync(Ct);
        await body.ReadExactlyAsync(new byte[1024], Ct);

        world.Fixture.Service.SetFolders(world.Sam.Id, []);

        await AssertCutAsync(body);
    }

    // ---- The file's details ----

    [Fact]
    public async Task GetFile_ReturnsNameTitleDurationProgress_WithoutPath()
    {
        await using var world = await World.OnTestHostAsync();
        world.Fixture.Progress.Save(world.Sam.Id, world.File, 120, 5400);

        var response = await world.GetFileAsync(world.File);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(@"Sub", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fullPath", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("library", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\\\", text, StringComparison.Ordinal);
        var reply = await response.Content.ReadFromJsonAsync<FileReply>(ApiJson.Options, Ct);
        Assert.Equal(world.File, reply!.FileId);
        Assert.Equal("Heat (1995).mkv", reply.FileName);
        Assert.Equal("Heat", reply.Title);
        Assert.Equal(5400, reply.DurationS);
        Assert.Equal(120, reply.Saved!.PositionS);
        Assert.False(reply.Missing);
    }

    [Fact]
    public async Task GetFile_NoProgress_FallsBackToTheFilesDuration()
    {
        await using var world = await World.OnTestHostAsync();
        world.Fixture.Files.SetDuration(world.File, 5500);

        var response = await world.GetFileAsync(world.File);

        var reply = await response.Content.ReadFromJsonAsync<FileReply>(ApiJson.Options, Ct);
        Assert.Equal(5500, reply!.DurationS);
        Assert.Null(reply.Saved);
    }

    [Fact]
    public async Task GetFile_FileGone_SaysMissing()
    {
        await using var world = await World.OnTestHostAsync();
        File.Delete(world.FilePath);

        var response = await world.GetFileAsync(world.File);

        var reply = await response.Content.ReadFromJsonAsync<FileReply>(ApiJson.Options, Ct);
        Assert.True(reply!.Missing);
    }

    [Fact]
    public async Task GetFile_AccessForbidden_404()
    {
        await using var world = await World.OnTestHostAsync();

        await AssertErrorAsync(HttpStatusCode.NotFound, ApiErrorCode.NotFound, await world.GetFileAsync(world.ForbiddenFile));
    }
}
