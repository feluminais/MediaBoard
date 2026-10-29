using MediaBoard.Core.Metadata;
using MediaBoard.Core.Parsing;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;

namespace MediaBoard.Core.Tests.Corpus;

/// <summary>The machinery behind the owner-reviewed corpus test: grouping into items, the file format, the comparison.</summary>
public class CorpusPipelineTests
{
    private static TitleRow Title(int tmdbId, TitleKind kind, string name, int? year) =>
        new(tmdbId + 1000, tmdbId, kind, name, year, null, null, null, null, null);

    private static CorpusPipeline.IdentifiedFile File(string path, MediaKind kind, string status, TitleRow? title) =>
        new(path, kind, status, title);

    [Fact]
    public void Group_ShowFolderIsOneItem_MovieInsideItIsItsOwn_LooseFileIsItsOwn_MovieFolderIsOne()
    {
        var show = Title(615, TitleKind.Show, "Futurama", 1999);
        var film = Title(949, TitleKind.Movie, "Heat", 1995);
        var other = Title(1, TitleKind.Movie, "Other", 2000);

        var items = CorpusPipeline.Group(
        [
            File(@"Futurama\S01E01.mkv", MediaKind.Episode, "matched", show),
            File(@"Futurama\S01E02.mkv", MediaKind.Episode, "matched", show),
            File(@"Futurama\Futurama Movie.mkv", MediaKind.Movie, "matched", film),
            File("Heat (1995).mkv", MediaKind.Movie, "matched", film),
            File(@"Other (2000)\other.mkv", MediaKind.Movie, "matched", other),
        ]);

        Assert.Equal(
        [
            new ReferenceItem("Futurama", "show", 615, "Futurama", 1999, "matched"),
            new ReferenceItem(@"Futurama\Futurama Movie.mkv", "movie", 949, "Heat", 1995, "matched"),
            new ReferenceItem("Heat (1995).mkv", "movie", 949, "Heat", 1995, "matched"),
            new ReferenceItem("Other (2000)", "movie", 1, "Other", 2000, "matched"),
        ], items);
    }

    [Fact]
    public void Group_UnmatchedEpisode_MakesTheWholeShowNeedReview_AndKeepsTheProvisionalName()
    {
        var provisional = new TitleRow(5, null, TitleKind.Show, "Mystery", null, null, null, null, null, null);

        var items = CorpusPipeline.Group(
        [
            File(@"Mystery\a.mkv", MediaKind.Episode, "review", provisional),
            File(@"Mystery\b.mkv", MediaKind.Episode, "offline", provisional),
            File("lone.mkv", MediaKind.Movie, "pending", null),
        ]);

        Assert.Equal(
        [
            new ReferenceItem("Mystery", "show", null, "Mystery", null, "review"),
            new ReferenceItem("lone.mkv", "movie", null, null, null, "pending"),
        ], items);
    }

    [Fact]
    public void Group_FolderWithSeveralShows_GetsOneItemPerShow()
    {
        var a = Title(10, TitleKind.Show, "A", 2000);
        var b = Title(20, TitleKind.Show, "B", 2001);

        var items = CorpusPipeline.Group(
        [
            File(@"Shows\A\1.mkv", MediaKind.Episode, "matched", a),
            File(@"Shows\B\1.mkv", MediaKind.Episode, "matched", b),
        ]);

        Assert.Equal([@"Shows#10", @"Shows#20"], items.Select(i => i.Path));
    }

    private static CorpusPipeline.IdentifiedFile Linked(string path, TitleRow show, string episodeTitle, params (int, int)[] episodes) =>
        new(path, MediaKind.Episode, "matched", show, episodes, [episodeTitle]);

    [Fact]
    public void SharedEpisodes_TwoFilesOnOneEpisode_AreReported()
    {
        var futurama = Title(615, TitleKind.Show, "Futurama", 1999);

        var problems = CorpusPipeline.SharedEpisodes(
        [
            Linked(@"Futurama\S04E01 Kif Gets Knocked Up a Notch.mkv", futurama, "Kif Gets Knocked Up a Notch", (5, 5)),
            Linked(@"Futurama\S05E05 The Beast with a Billion Backs. Part 1.mkv", futurama, "The Beast with a Billion Backs. Part 1", (5, 5)),
            Linked(@"Futurama\S04E02 A Tale of Two Santas.mkv", futurama, "A Tale of Two Santas", (4, 2)),
        ]);

        var problem = Assert.Single(problems);
        Assert.Contains("5x5", problem);
        Assert.Contains("Kif Gets Knocked Up a Notch", problem);
        Assert.Contains("The Beast with a Billion Backs. Part 1", problem);
    }

    [Fact]
    public void SharedEpisodes_PartsOfOneTitle_MayShareIt()
    {
        var futurama = Title(615, TitleKind.Show, "Futurama", 1999);

        var problems = CorpusPipeline.SharedEpisodes(
        [
            Linked(@"Futurama\S05E01 Bender's Big Score. Part 1.mkv", futurama, "Bender's Big Score. Part 1", (0, 1)),
            Linked(@"Futurama\S05E02 Bender's Big Score. Part 2.mkv", futurama, "Bender's Big Score. Part 2", (0, 1)),
        ]);

        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("Bender's Game. Part 1")]
    [InlineData("Bender's Big Score")]
    public void SharedEpisodes_APartAndAnotherTitle_AreReported(string otherTitle)
    {
        var futurama = Title(615, TitleKind.Show, "Futurama", 1999);

        var problems = CorpusPipeline.SharedEpisodes(
        [
            Linked(@"Futurama\a.mkv", futurama, "Bender's Big Score. Part 1", (0, 1)),
            Linked(@"Futurama\b.mkv", futurama, otherTitle, (0, 1)),
        ]);

        Assert.Single(problems);
    }

    [Fact]
    public void SharedEpisodes_SameNumberInTwoShows_IsNoProblem()
    {
        var problems = CorpusPipeline.SharedEpisodes(
        [
            Linked(@"A\S01E01.mkv", Title(10, TitleKind.Show, "A", 2000), "Pilot", (1, 1)),
            Linked(@"B\S01E01.mkv", Title(20, TitleKind.Show, "B", 2001), "Pilot", (1, 1)),
        ]);

        Assert.Empty(problems);
    }

    [Fact]
    public void ReferenceFile_RoundTrips_SortedByPath_KeepingNonAsciiReadable()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mediaboard-ref-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ReferenceFile.Write(path,
            [
                new ReferenceItem("b.mkv", "movie", null, null, null, "pending"),
                new ReferenceItem("Цікава Ідея", "show", 7, "Brassic", 2019, "matched"),
            ]);

            var text = System.IO.File.ReadAllText(path);
            Assert.Contains("\"path\": \"Цікава Ідея\"", text);
            Assert.Contains("\"tmdbId\": 7", text);
            Assert.True(text.IndexOf("b.mkv", StringComparison.Ordinal) < text.IndexOf("Цікава", StringComparison.Ordinal));
            Assert.Equal(
            [
                new ReferenceItem("b.mkv", "movie", null, null, null, "pending"),
                new ReferenceItem("Цікава Ідея", "show", 7, "Brassic", 2019, "matched"),
            ], ReferenceFile.Read(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Compare_ReportsMissingDifferingAndUnexpectedItems()
    {
        var same = new ReferenceItem("same", "movie", 1, "Same", 2000, "matched");
        ReferenceItem[] expected =
        [
            same,
            new ReferenceItem("gone", "movie", 2, "Gone", 2001, "matched"),
            new ReferenceItem("moved", "show", 3, "Moved", 2002, "matched"),
        ];
        ReferenceItem[] actual =
        [
            same,
            new ReferenceItem("moved", "show", 4, "Else", 2002, "matched"),
            new ReferenceItem("extra", "movie", 5, "Extra", 2003, "matched"),
        ];

        var problems = CorpusIdentificationTests.Compare(expected, actual);

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("missing: gone"));
        Assert.Contains(problems, p => p.StartsWith("differs: expected moved") && p.Contains("Else"));
        Assert.Contains(problems, p => p.StartsWith("unexpected: extra"));
        Assert.Empty(CorpusIdentificationTests.Compare([same], [same]));
    }

    [Fact]
    public async Task UnreachableTmdb_ScansTheWholeCorpusAndLeavesEveryItemOffline()
    {
        var offline = new StubHandler((_, _) => throw new HttpRequestException("no network"));

        var items = await CorpusPipeline.IdentifyAsync(offline, "k", new FakeClock(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Equal("offline", i.Status));
        Assert.All(items, i => Assert.Null(i.TmdbId));
        Assert.Contains(items, i => i.Path.StartsWith("Widows.Bay", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Path.StartsWith("Brassic", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Path.StartsWith("[FanVoxUA]_Over_the_garden_wall", StringComparison.Ordinal));
        Assert.DoesNotContain(items, i => i.Path.StartsWith("GTA IV", StringComparison.Ordinal));
        Assert.DoesNotContain(items, i => i.Path.StartsWith("Photoscape", StringComparison.Ordinal));
        Assert.DoesNotContain(items, i => i.Path.EndsWith(".parts", StringComparison.Ordinal));
        Assert.Equal(items.Count, items.Select(i => i.Path).Distinct().Count());
    }
}
