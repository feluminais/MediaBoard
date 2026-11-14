using CozyCouch.Core.Parsing;

namespace CozyCouch.Core.Tests.Corpus;

public class CorpusParseTests
{
    private static readonly IReadOnlyList<(string RelativePath, long Size)> Snapshot = CorpusSnapshot.Load();

    private static string Directory(string path) =>
        path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";

    private static string FileName(string path) => path[(path.LastIndexOf('\\') + 1)..];

    private static readonly Lazy<IReadOnlyList<(string Path, ParsedMedia Parsed)>> Parsed = new(() =>
    {
        var videos = Snapshot
            .Where(e => MediaFileRules.IsVideo(e.RelativePath) && !MediaFileRules.IsIgnored(e.RelativePath, e.Size))
            .Select(e => e.RelativePath)
            .ToList();
        var byDirectory = videos.ToLookup(Directory);
        return videos
            .Select(p => (p, PathParser.Parse(
                p, byDirectory[Directory(p)].Where(o => o != p).Select(FileName).ToList())))
            .ToList();
    });

    private static IEnumerable<ParsedMedia> Under(string folder) =>
        Parsed.Value.Where(x => x.Path.StartsWith(folder + "\\", StringComparison.Ordinal)).Select(x => x.Parsed);

    [Fact]
    public void EveryVideoParses()
    {
        var bad = Parsed.Value
            .Where(x => x.Parsed.Kind == MediaKind.Unknown || string.IsNullOrWhiteSpace(x.Parsed.Title))
            .Select(x => x.Path)
            .ToList();

        Assert.True(bad.Count == 0, "Unparsed:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void EveryEpisodeHasSeasonAndNumber()
    {
        var bad = Parsed.Value
            .Where(x => x.Parsed.Kind == MediaKind.Episode && (x.Parsed.Season is null || x.Parsed.Episodes.Count == 0))
            .Select(x => x.Path)
            .ToList();

        Assert.True(bad.Count == 0, "Episodes without season or number:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void SpongeBobSplitEpisodes_AreEpisodes()
    {
        // "[8x15a] ..." and "[8x15b] ..." are two halves of one episode slot, not movies.
        var split = Parsed.Value.Where(x => FileName(x.Path).StartsWith("[8x15", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, split.Count);
        Assert.All(split, x =>
        {
            Assert.Equal(MediaKind.Episode, x.Parsed.Kind);
            Assert.Equal(8, x.Parsed.Season);
            Assert.Equal([15], x.Parsed.Episodes);
        });
    }

    [Fact]
    public void EpisodeCounts_OverTheGardenWall()
    {
        var folder = "[FanVoxUA]_Over_the_garden_wall_[1080p]";
        var episodes = Under(folder).ToList();

        Assert.Equal(10, episodes.Count);
        Assert.All(episodes, p =>
        {
            Assert.Equal(MediaKind.Episode, p.Kind);
            Assert.Equal(1, p.Season);
            Assert.Equal(folder, p.ShowFolder);
        });
        Assert.Equal(Enumerable.Range(1, 10), episodes.SelectMany(p => p.Episodes).Order());
    }

    [Theory]
    [InlineData("Brassic - Season 1 (2019) WEBRip 1080p [Ukr.Eng] [Цікава Ідея]", 6)]
    [InlineData("Widows.Bay.S01.2026.ATVP.WEB-DL.1080p", 10)]
    public void EpisodeCounts_SingleSeasonShows(string folder, int count)
    {
        var episodes = Under(folder).ToList();

        Assert.Equal(count, episodes.Count);
        Assert.All(episodes, p =>
        {
            Assert.Equal(MediaKind.Episode, p.Kind);
            Assert.Equal(1, p.Season);
        });
        Assert.Equal(count, episodes.SelectMany(p => p.Episodes).Distinct().Count());
    }
}
