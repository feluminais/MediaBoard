using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Remote;
using MediaBoard.Core.Scanning;
using MediaBoard.Core.Storage;

namespace MediaBoard.Server.Tests;

/// <summary>
/// Not part of a normal run. With MEDIABOARD_MEASURE=1 it seeds 5,000 titles into a temp folder (never the real data
/// folder), drives 5 viewers over TestHost and prints what the sharing server costs: the <c>titles</c> latency the plan
/// asks for, and the per-request snapshot loads, the progress PUTs, the searches and a TLS handshake. Only
/// <c>titles</c> is asserted; the rest is for reading. The report goes to the test's diagnostics and to
/// <c>mediaboard-load-measure.txt</c> in the temp folder.
/// <code>
/// $env:MEDIABOARD_MEASURE = "1"
/// dotnet test --project tests/MediaBoard.Server.Tests -- --filter-trait "Category=Measure"
/// </code>
/// The TLS part listens on 127.0.0.1 only, so Windows Firewall has nothing to ask about.
/// </summary>
[Trait("Category", "Measure")]
public sealed class LoadMeasureTests
{
    private const string MeasureVariable = "MEDIABOARD_MEASURE";
    private const int MovieCount = 4_250;
    private const int ShowCount = 750;
    private const int EpisodesPerShow = 3;
    private const int Viewers = 5;
    private const int Rounds = 20;
    private const int FakeTmdbBase = 900_000_000;
    private const double TitlesBarMs = 300;

    private static readonly string[] Adjectives = ["Quiet", "Last", "Silent", "Golden", "Broken", "Hidden", "Crimson", "Distant", "Electric", "Paper"];
    private static readonly string[] Nouns = ["River", "Harbor", "Garden", "Signal", "Empire", "Letter", "Mirror", "Orchard", "Station", "Winter"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FiveViewers_OnFiveThousandTitles_GetTitlesWithinTheBar()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(MeasureVariable) == "1",
            $"Measuring is off: set {MeasureVariable}=1 to run it. It seeds 5,000 titles into a temp folder.");

        await using var fixture = await ServerFixture.StartTestHostAsync(shareSnapshots: true);
        var report = new StringBuilder();
        void Say(string line = "") => report.AppendLine(line);

        var seeded = await SeedAsync(fixture);
        var viewers = Enumerable.Range(1, Viewers)
            .Select(n => fixture.Devices.Issue(fixture.NewPerson($"Viewer {n}").Id, $"Device {n}", "198.51.100.1"))
            .ToList();
        foreach (var viewer in viewers)
        {
            // Some history, so Continue watching has rows to build.
            foreach (var fileId in seeded.FileIds.Take(300))
            {
                fixture.Progress.SaveIfNewer(viewer.Device.UserId, fileId, new ProgressEntry(120, 6000, false, DateTimeOffset.UtcNow));
            }
        }

        using var cpu = Process.GetCurrentProcess();
        Say($"Library: {seeded.FileIds.Count} files, {MovieCount + ShowCount} titles. {Viewers} viewers.");

        // ---- 1. Home and a title page, 5 viewers at once ----
        var timings = new ConcurrentDictionary<string, ConcurrentBag<double>>(StringComparer.Ordinal);
        await GetOkAsync(fixture, viewers[0].Token, "titles?kind=Movie");
        await GetOkAsync(fixture, viewers[0].Token, $"titles/{seeded.ShowTitleId}");
        await Task.WhenAll(viewers.Select(v => Task.Run(async () =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                await TimedAsync(fixture, v.Token, "continue", "continue?limit=20", timings);
                await TimedAsync(fixture, v.Token, "recent", "recent?limit=20", timings);
                await TimedAsync(fixture, v.Token, "titles (movies)", "titles?kind=Movie", timings);
                await TimedAsync(fixture, v.Token, "titles (shows)", "titles?kind=Show", timings);
                await TimedAsync(fixture, v.Token, "titles/{id}", $"titles/{seeded.ShowTitleId}", timings);
                await TimedAsync(fixture, v.Token, "titles/{id}/next", $"titles/{seeded.ShowTitleId}/next", timings);
            }
        }, Ct)));
        Say();
        Say($"1. Home and a title page, {Viewers} viewers at once, {Rounds} rounds each (ms)");
        foreach (var (name, bag) in timings.OrderBy(t => t.Key, StringComparer.Ordinal)) Say($"   {name,-18} {Describe(bag)}");

        var titlesWorst = timings.Where(t => t.Key.StartsWith("titles (", StringComparison.Ordinal)).Max(t => Percentile(t.Value, 0.95));
        Say($"   titles p95, worse of the two kinds: {titlesWorst:F0} ms (bar {TitlesBarMs:F0} ms)");

        // ---- 2. The unit: one snapshot load ----
        var asFirst = new Viewer(viewers[0].Device.UserId, null);
        var snapshot = Cost(() => _ = fixture.Access.CanReachTitle(asFirst, seeded.ShowTitleId), 100);
        Say();
        Say($"2. One LibrarySnapshot load (ViewerAccess.CanReachTitle), alone: {snapshot:F1} ms");

        // ---- 3. A stream request, and a re-check of open streams ----
        var token = viewers[0].Token;
        var streamCost = await CostAsync(async () =>
        {
            using var response = await fixture.Client.SendAsync(
                fixture.Request("GET", $"/api/v1/files/{seeded.StreamFileId}/stream", token), HttpCompletionOption.ResponseHeadersRead, Ct);
            Assert.True(response.IsSuccessStatusCode, $"stream answered {response.StatusCode}");
            await response.Content.ReadAsByteArrayAsync(Ct);
        }, 40);
        var shared = new SharedLibrary(fixture.Owner, fixture.Access, fixture.Files, fixture.Progress, _ => throw new NotSupportedException(), null!);
        using var watch = new StreamWatch(fixture.Streams, fixture.Service, fixture.Accounts, fixture.Devices, shared);
        var userId = viewers[0].Device.UserId;
        var recheck = Cost(() => _ = watch.Reachable(userId, seeded.StreamFileId), 100);
        Say();
        Say("3. Streams");
        Say($"   GET files/{{id}}/stream, headers and a 4 KB body: {streamCost:F1} ms (two snapshot loads inside)");
        Say($"   StreamWatch re-check of one session: {recheck:F1} ms; with 5 accounts x 8 streams = 40 open: {recheck * 40:F0} ms of one core per Changed");

        // ---- 4. PUT progress ----
        var put = await CostWithCpuAsync(async n =>
        {
            using var request = fixture.Request("PUT", $"/api/v1/files/{seeded.StreamFileId}/progress", token);
            request.Content = JsonContent.Create(new ProgressEntry(10 + n, 6000, null, DateTimeOffset.UtcNow), options: ApiJson.Options);
            using var response = await fixture.Client.SendAsync(request, Ct);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        }, 100, cpu);
        const double PutsPerSecond = 8 / 10.0;
        Say();
        Say("4. PUT files/{id}/progress");
        Say($"   {put.Wall:F1} ms wall, {put.Cpu:F1} ms CPU per request");
        Say($"   8 streams, one PUT per 10 s each = {PutsPerSecond:F1} per second: {put.Cpu * PutsPerSecond / 10:F2}% of one core");

        // ---- 5. Search ----
        var localSearch = await CostAsync(() => GetOkAsync(fixture, token, "search/local?q=silent harbor"), 40);
        for (var n = 0; n < 10; n++)
        {
            fixture.Provider.Hits.Add(new SearchCandidate(FakeTmdbBase + n, TitleKind.Movie, $"Hit {n}", null, 2000, 1, 7, null, null));
        }

        var onlineSearch = await CostAsync(async () =>
        {
            using var response = await fixture.Client.SendAsync(fixture.Request("GET", "/api/v1/search?q=silent harbor", token), Ct);
            var results = await response.Content.ReadFromJsonAsync<SearchResults>(ApiJson.Options, Ct);
            Assert.Equal(OnlineState.Ok, results!.State);
            Assert.Equal(10, results.Online.Count);
        }, 40);
        Say();
        Say("5. Search");
        Say($"   GET search/local: {localSearch:F1} ms (a fresh ForViewer builds its index every request)");
        Say($"   GET search with 10 online hits: {onlineSearch:F1} ms (local part plus up to 10 CanReachTitle)");

        // ---- 6. A TLS handshake on loopback ----
        var port = ServerFixture.FreePort();
        await fixture.Server.StartAsync(new SharingOptions(port, ServerFixture.ServerName, fixture.NewCertificate()) { LoopbackOnly = true }, Ct);
        var client = fixture.TlsClient();
        var url = new Uri($"https://127.0.0.1:{port}/api/v1/ping");
        (await client.GetAsync(url, Ct)).Dispose();
        var reused = await CostAsync(async () => (await client.GetAsync(url, Ct)).Dispose(), 100);
        var closed = await CostAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.ConnectionClose = true;
            (await client.SendAsync(request, Ct)).Dispose();
        }, 100);
        Say();
        Say("6. TLS on loopback, ping");
        Say($"   on a kept connection: {reused:F2} ms; on a new connection each time: {closed:F2} ms");
        Say($"   one handshake with the TCP connect: about {closed - reused:F2} ms; a remux seek's 9: about {9 * (closed - reused):F1} ms (loopback, so no network round trips)");

        var text = report.ToString();
        TestContext.Current.SendDiagnosticMessage(text);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "mediaboard-load-measure.txt"), text, Ct);

        Assert.True(titlesWorst <= TitlesBarMs, $"titles p95 was {titlesWorst:F0} ms, over the {TitlesBarMs:F0} ms bar.\n{text}");
    }

    private sealed record Seeded(IReadOnlyList<int> FileIds, int ShowTitleId, int StreamFileId);

    private static async Task<Seeded> SeedAsync(ServerFixture fixture)
    {
        var folder = fixture.AddFolder("seed");
        for (var i = 1; i <= MovieCount; i++) Write(folder.Path, MovieFile(i));
        for (var i = 1; i <= ShowCount; i++)
        {
            for (var e = 1; e <= EpisodesPerShow; e++) Write(folder.Path, EpisodeFile(i, e));
        }

        await new LibraryScanner(fixture.Db, new MediaBoard.Core.Activity.ActivityHub()).ScanAsync(folder, Ct);
        var ids = fixture.Files.ByFolder(folder.Id).ToDictionary(f => f.RelPath, f => f.Id, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(MovieCount + (ShowCount * EpisodesPerShow), ids.Count);

        var n = 0;
        for (var i = 1; i <= MovieCount; i++, n++)
        {
            var title = fixture.Titles.Upsert(new TitleDetails(FakeTmdbBase + n, TitleKind.Movie, MovieName(i), 2000, null, "About.", 7, null, 100, []));
            Assert.True(fixture.Matches.SetMatch(ids[MovieFile(i)], title, [], 1.0, "auto"));
        }

        var showTitle = 0;
        for (var i = 1; i <= ShowCount; i++, n++)
        {
            var title = fixture.Titles.Upsert(new TitleDetails(FakeTmdbBase + n, TitleKind.Show, ShowName(i), 2010, null, "About.", 7, null, 45, [1]));
            fixture.Titles.UpsertEpisodes(title, new SeasonDetails(
                1, Enumerable.Range(1, EpisodesPerShow).Select(e => new EpisodeDetails(1, e, $"Episode {e}", 45, null)).ToList()));
            var episodeIds = fixture.Titles.EpisodeIds(title, Enumerable.Range(1, EpisodesPerShow).Select(e => (1, e)));
            for (var e = 1; e <= EpisodesPerShow; e++)
            {
                Assert.True(fixture.Matches.SetMatch(ids[EpisodeFile(i, e)], title, [episodeIds[e - 1]], 1.0, "auto"));
            }

            showTitle = title;
        }

        // The stream reads a real, small file; the seeded ones are empty.
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, MovieFile(1)), new byte[4096], Ct);
        return new Seeded([.. ids.Values], showTitle, ids[MovieFile(1)]);
    }

    private static string MovieName(int i) => $"Seed Movie {i:D4} {Words(i)}";

    private static string ShowName(int i) => $"Seed Show {i:D4} {Words(i * 7)}";

    private static string MovieFile(int i) => $"{MovieName(i)} (2000).mkv";

    private static string EpisodeFile(int i, int e) =>
        Path.Combine($"{ShowName(i)} (2010)", "Season 01", string.Create(CultureInfo.InvariantCulture, $"{ShowName(i)} S01E{e:D2}.mkv"));

    private static string Words(int i) => $"{Adjectives[i % Adjectives.Length]} {Nouns[i / Adjectives.Length % Nouns.Length]}";

    private static void Write(string root, string relPath)
    {
        var path = Path.Combine(root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Create(path).Dispose();
    }

    private static async Task GetOkAsync(ServerFixture fixture, string token, string route)
    {
        using var response = await fixture.Client.SendAsync(fixture.Request("GET", "/api/v1/" + route, token), Ct);
        Assert.True(response.IsSuccessStatusCode, $"{route} answered {response.StatusCode}");
        await response.Content.ReadAsByteArrayAsync(Ct);
    }

    private static async Task TimedAsync(
        ServerFixture fixture, string token, string name, string route, ConcurrentDictionary<string, ConcurrentBag<double>> timings)
    {
        var clock = Stopwatch.StartNew();
        await GetOkAsync(fixture, token, route);
        timings.GetOrAdd(name, _ => []).Add(clock.Elapsed.TotalMilliseconds);
    }

    private static double Cost(Action action, int times)
    {
        action();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < times; i++) action();
        return clock.Elapsed.TotalMilliseconds / times;
    }

    private static async Task<double> CostAsync(Func<Task> action, int times)
    {
        await action();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < times; i++) await action();
        return clock.Elapsed.TotalMilliseconds / times;
    }

    private static async Task<(double Wall, double Cpu)> CostWithCpuAsync(Func<int, Task> action, int times, Process process)
    {
        await action(0);
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        for (var i = 1; i <= times; i++) await action(i);
        var wall = clock.Elapsed.TotalMilliseconds / times;
        process.Refresh();
        return (wall, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / times);
    }

    private static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(sorted.Length * p) - 1)];
    }

    private static string Describe(IEnumerable<double> values)
    {
        var all = values.ToArray();
        return $"n={all.Length} p50={Percentile(all, 0.5):F0} p95={Percentile(all, 0.95):F0} max={all.Max():F0}";
    }
}
